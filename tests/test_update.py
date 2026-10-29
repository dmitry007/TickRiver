"""Exercise the real native setup helper and MCP shutdown with disposable data.
Usage: python tests/test_update.py <helper.exe> <published-mcp.exe> [older-mcp.exe]
"""
import ctypes
import json
import os
import pathlib
import queue
import shutil
import sqlite3
import subprocess
import sys
import tempfile
import threading
import time

helper = pathlib.Path(sys.argv[1]).resolve()
published = pathlib.Path(sys.argv[2]).resolve()
folder = pathlib.Path(tempfile.mkdtemp(prefix="TickRiver-update-"))
processes = []


def fixture(name, source=published):
    target = folder / name
    def link(source_file, target_file):
        try:
            os.link(source_file, target_file)
        except OSError:
            shutil.copy2(source_file, target_file)
        return target_file
    shutil.copytree(source.parent, target, copy_function=link)
    database = folder / (name + ".sqlite")
    with sqlite3.connect(database) as db:
        db.executescript("""
        CREATE TABLE clients(id INTEGER PRIMARY KEY,name TEXT,currency TEXT,rate TEXT,archived INTEGER);
        CREATE TABLE projects(id INTEGER PRIMARY KEY,client_id INTEGER,name TEXT,rate TEXT,billable INTEGER,archived INTEGER);
        CREATE TABLE entries(id INTEGER PRIMARY KEY,project_id INTEGER,description TEXT,start INTEGER,end INTEGER,rate TEXT,currency TEXT,billable INTEGER,invoiced INTEGER);
        INSERT INTO clients VALUES(1,'Update fixture','USD','100',0);
        INSERT INTO projects VALUES(1,1,'Installer test',NULL,1,0);
        """)
    return target, database


class Server:
    def __init__(self, target, database, pipe=True):
        self.process = subprocess.Popen([str(target / "TickRiver.Mcp.exe"), "--database", str(database)],
                                        stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                        text=True, encoding="utf-8")
        processes.append(self.process)
        self.responses = queue.Queue()
        self.sequence = 0
        threading.Thread(target=self.read, daemon=True).start()
        self.call("initialize", {"protocolVersion": "2025-06-18", "capabilities": {},
                                 "clientInfo": {"name": "Installer fixture", "version": "1"}})
        self.process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n')
        self.process.stdin.flush()
        if pipe:
            deadline = time.monotonic() + 5
            while not ctypes.windll.kernel32.WaitNamedPipeW("\\\\.\\pipe\\TickRiver.Control." + str(self.process.pid), 100):
                assert self.process.poll() is None, "MCP exited before opening its control pipe"
                assert time.monotonic() < deadline, "Control pipe did not open"

    def read(self):
        for line in self.process.stdout:
            self.responses.put(json.loads(line))

    def send(self, method, params):
        self.sequence += 1
        self.process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": self.sequence, "method": method, "params": params}) + "\n")
        self.process.stdin.flush()
        return self.sequence

    def call(self, method, params):
        identity = self.send(method, params)
        while True:
            response = self.responses.get(timeout=10)
            if response.get("id") == identity:
                return response

    def tool(self, name, arguments):
        result = self.call("tools/call", {"name": name, "arguments": arguments})["result"]
        assert not result.get("isError"), result
        return json.loads(result["content"][0]["text"])


def shutdown(root, timeout=10000, extra=()):
    result = subprocess.run([str(helper), "--shutdown-for-update", str(root), str(root),
                             "--timeout", str(timeout), *extra], timeout=timeout / 1000 + 10)
    return result.returncode


def installer_test():
    root, database = fixture("msi")
    one, two = Server(root, database), Server(root, database)
    sdk = pathlib.Path(os.environ["ProgramFiles(x86)"])
    wix = next(path for path in sorted(sdk.glob("WiX Toolset v3.*/bin"), reverse=True) if (path / "candle.exe").exists())
    source = pathlib.Path(__file__).with_name("UpdateInstaller.wxs").resolve()
    obj = folder / "fixture.wixobj"
    msi = folder / "fixture.msi"
    for command in ([str(wix / "candle.exe"), "-nologo", "-arch", "x64", "-dTargetRoot=" + str(root),
                     "-dHelper=" + str(helper), "-ext", "WixUtilExtension", "-out", str(obj), str(source)],
                    [str(wix / "light.exe"), "-nologo", "-sice:ICE91", "-ext", "WixUtilExtension", "-out", str(msi), str(obj)]):
        result = subprocess.run(command, capture_output=True, text=True, timeout=60)
        assert result.returncode == 0, result.stdout + result.stderr
    log = folder / "msi-install.log"
    install = subprocess.Popen(["msiexec.exe", "/i", str(msi), "/qn", "/norestart", "/l*v!", str(log)])
    processes.append(install)
    try:
        deadline = time.monotonic() + 30
        observed = False
        while install.poll() is None and time.monotonic() < deadline:
            if log.exists():
                data = log.read_bytes()
                text = data.decode("utf-16" if data.startswith(b"\xff\xfe") else "utf-8", errors="replace")
                if any("Action ended" in line and "ShutdownFixture. Return value 1" in line for line in text.splitlines()):
                    deferred = subprocess.run([str(root / "TickRiver.Mcp.exe"), "--database", str(database)],
                                              input="", text=True, capture_output=True, timeout=5)
                    assert deferred.returncode == 1618, "Startup guard ended before the MSI execute sequence"
                    observed = True
                    break
            time.sleep(0.02)
        assert install.wait(timeout=30) == 0, "Fixture install failed; see " + str(log)
        assert observed, "Did not observe the startup gate after the shutdown custom action returned"
        assert one.process.wait(timeout=10) == 0 and two.process.wait(timeout=10) == 0
    finally:
        removed = subprocess.run(["msiexec.exe", "/x", str(msi), "/qn", "/norestart"], timeout=30)
        assert removed.returncode in (0, 1605), "Fixture cleanup failed"
    # A deferred failure rolls back, and must also release the transient gate.
    rollback = subprocess.run(["msiexec.exe", "/i", str(msi), "/qn", "/norestart", "WIXFAILWHENDEFERRED=1",
                               "/l*v", str(folder / "msi-rollback.log")], timeout=30)
    assert rollback.returncode == 1603
    resumed = Server(root, database)
    assert shutdown(root) == 0 and resumed.process.wait(timeout=10) == 0


try:
    # Only the helper EXE is present: it must run without a nearby .NET runtime or DLL.
    native = folder / "native"
    native.mkdir()
    standalone = native / helper.name
    shutil.copy2(helper, standalone)
    assert subprocess.run([str(standalone), "--shutdown-for-update", str(native), str(native)], timeout=10).returncode == 0

    target, database = fixture("current")
    outside, outside_database = fixture("current-other")
    one = Server(target, database)
    two = Server(target, database)
    untouched = Server(outside, outside_database)
    timer = one.tool("start_timer", {"projectId": 1, "description": "Preserve during update"})
    assert shutdown(target) == 0
    assert one.process.wait(timeout=10) == 0 and two.process.wait(timeout=10) == 0
    assert untouched.process.poll() is None, "Helper closed an instance outside the installation root"
    with sqlite3.connect(database) as db:
        assert db.execute("select end from entries where id=?", (timer["entry_id"],)).fetchone() == (None,)
        assert db.execute("pragma integrity_check").fetchone() == ("ok",)

    # A shutdown request must wait for an already-running SQLite mutation to finish.
    busy_target, busy_database = fixture("busy")
    busy = Server(busy_target, busy_database)
    with sqlite3.connect(busy_database) as lock:
        lock.execute("BEGIN IMMEDIATE")
        busy.send("tools/call", {"name": "add_entry", "arguments": {"projectId": 1, "start": "2026-09-15T09:00:00-07:00",
                  "end": "2026-09-15T10:00:00-07:00", "description": "Blocked write fixture"}})
        time.sleep(0.5)
        updater = subprocess.Popen([str(helper), "--shutdown-for-update", str(busy_target), str(busy_target), "--timeout", "10000"])
        processes.append(updater)
        time.sleep(1)
        assert busy.process.poll() is None and updater.poll() is None, "Shutdown interrupted the active mutation"
        # Startup is blocked while the shutdown helper holds the update gate.
        deferred = subprocess.run([str(busy_target / "TickRiver.Mcp.exe"), "--database", str(busy_database)],
                                  input="", text=True, capture_output=True, timeout=5)
        assert deferred.returncode == 1618, deferred
        lock.commit()
    assert updater.wait(timeout=15) == 0
    assert busy.process.wait(timeout=10) == 0
    with sqlite3.connect(busy_database) as db:
        assert db.execute("select description from entries").fetchall() == [("Blocked write fixture",)]
        assert db.execute("pragma integrity_check").fetchone() == ("ok",)
    restarted = Server(busy_target, busy_database)
    assert shutdown(busy_target) == 0 and restarted.process.wait(timeout=10) == 0

    if len(sys.argv) > 3:
        legacy_target, legacy_database = fixture("legacy", pathlib.Path(sys.argv[3]).resolve())
        legacy = Server(legacy_target, legacy_database, pipe=False)
        assert shutdown(legacy_target, 700) == 1603
        assert legacy.process.poll() is None, "Silent setup must never force terminate an older MCP server"
    assert shutdown(outside) == 0 and untouched.process.wait(timeout=10) == 0
    assert shutdown(target, extra=("--msi",)) == 1603, "An MSI guard must not outlive a nonexistent installation"
    installer_test()
    print("PASS: standalone native helper, matching instances, path isolation, persisted timer, active-write completion, startup gate through the real MSI execute sequence and rollback, silent legacy timeout. Artifacts:", folder)
finally:
    for process in processes:
        if process.poll() is None:
            if process.stdin:
                process.stdin.close()
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
