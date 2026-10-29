"""Real stdio MCP test against a disposable database, never the user's data.
Usage: python tests/test_mcp.py <dotnet.exe> <TickRiver.Mcp.dll>
"""
import json
import pathlib
import queue
import sqlite3
import subprocess
import sys
import tempfile
import threading
import xml.etree.ElementTree as ET

version = ET.parse(pathlib.Path(__file__).resolve().parents[1] / "Directory.Build.props").findtext("PropertyGroup/Version")

folder = pathlib.Path(tempfile.mkdtemp(prefix="TickRiver-mcp-"))
database = folder / "fixture.sqlite"
with sqlite3.connect(database) as db:
    db.executescript("""
    CREATE TABLE clients(id INTEGER PRIMARY KEY,name TEXT,currency TEXT,rate TEXT,archived INTEGER);
    CREATE TABLE projects(id INTEGER PRIMARY KEY,client_id INTEGER,name TEXT,rate TEXT,billable INTEGER,archived INTEGER);
    CREATE TABLE entries(id INTEGER PRIMARY KEY,project_id INTEGER,description TEXT,start INTEGER,end INTEGER,rate TEXT,currency TEXT,billable INTEGER,invoiced INTEGER);
    INSERT INTO clients VALUES(1,'Client Ω','USD','125',0);
    INSERT INTO projects VALUES(1,1,'Website Ω',NULL,1,0);
    """)
    # Modern UTC timestamps expressed as .NET ticks, midnight safely outside this test's range edges.
    import datetime
    epoch = datetime.datetime(1, 1, 1, tzinfo=datetime.timezone.utc)
    start = datetime.datetime(2026, 9, 15, 16, tzinfo=datetime.timezone.utc)
    ticks = (start - epoch).days * 864000000000 + (start - epoch).seconds * 10000000
    db.execute("INSERT INTO entries VALUES(1,1,?,?,?,'125','USD',1,0)", ("Work Ω " + "x" * 2000, ticks, ticks + 5430 * 10000000))

command = [sys.argv[1], sys.argv[2], "--database", str(database)] if len(sys.argv) == 3 else [sys.argv[1], "--database", str(database)]
process = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
responses = queue.Queue()
def read_output():
    for line in process.stdout:
        try: responses.put(json.loads(line))
        except Exception as error: responses.put(error)
threading.Thread(target=read_output, daemon=True).start()
stderr = []
threading.Thread(target=lambda: stderr.extend(process.stderr.readlines()), daemon=True).start()
sequence = 0
def request(method, params):
    global sequence
    sequence += 1
    process.stdin.write(json.dumps({"jsonrpc": "2.0", "id": sequence, "method": method, "params": params}) + "\n")
    process.stdin.flush()
    while True:
        try: value = responses.get(timeout=20)
        except queue.Empty: raise AssertionError("Server did not answer: " + "".join(stderr))
        if isinstance(value, Exception): raise value
        if value.get("id") == sequence: return value
def tool(name, arguments):
    value = request("tools/call", {"name": name, "arguments": arguments})["result"]
    assert not value.get("isError"), value
    return json.loads(value["content"][0]["text"])
try:
    initialized = request("initialize", {"protocolVersion": "2025-06-18", "capabilities": {}, "clientInfo": {"name": "TickRiver-test", "version": "1"}})
    assert "result" in initialized, initialized
    assert initialized["result"]["serverInfo"] == {"name": "TickRiver", "version": version}
    process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); process.stdin.flush()
    tools = request("tools/list", {})["result"]["tools"]
    assert {t["name"] for t in tools} == {"get_context", "list_clients", "list_projects", "get_hours", "list_entries", "get_entry", "edit_entry", "add_entry", "start_timer", "stop_timer"}
    assert all(t["annotations"]["readOnlyHint"] == (t["name"] not in ["start_timer", "stop_timer", "edit_entry", "add_entry"]) for t in tools)
    assert next(t for t in tools if t["name"] == "edit_entry")["annotations"]["destructiveHint"]
    assert not next(t for t in tools if t["name"] == "add_entry")["annotations"]["destructiveHint"]
    assert tool("list_clients", {})["clients"][0]["name"] == "Client Ω"
    assert tool("list_projects", {})["projects"][0]["inherits_client_rate"]
    report = tool("get_hours", {"period": "custom", "from": "2026-09-01", "to": "2026-09-30"})
    assert report["total_seconds"] == 5430 and report["duration"] == "01:30:30"
    assert abs(report["decimal_hours"] - 1.5083333333333333) < 1e-12
    assert report["billable_amounts"][0]["amount"] == 188.54166666666666
    assert len(report["breakdown"]) == 30 and report["breakdown"][0]["seconds"] == 0
    entries = tool("list_entries", {"period": "custom", "from": "2026-09-01", "to": "2026-09-30"})["entries"]
    assert len(entries) == 1 and len(entries[0]["description"]) <= 501
    assert request("tools/call", {"name": "get_hours", "arguments": {"period": "unknown"}})["result"]["isError"]
    assert tool("get_context", {})["reporting_read_only"]
    for period in ["last_week", "this_month", "this_quarter", "this_year"]:
        assert "total_seconds" in tool("get_hours", {"period": period, "grouping": "month"})
    with sqlite3.connect(database) as db: assert db.execute("SELECT COUNT(*) FROM entries").fetchone()[0] == 1
    original = tool("get_entry", {"entryId": 1})
    assert len(original["description"]) > 2000 and original["duration"] == "01:30:30"
    edited = tool("edit_entry", {"entryId": 1, "expectedVersion": original["version"], "description": "Corrected work Ω"})
    assert edited["status"] == "updated" and edited["description"] == "Corrected work Ω" and edited["rate"] == 125
    assert edited["start"] == original["start"] and edited["end"] == original["end"]
    assert request("tools/call", {"name": "edit_entry", "arguments": {"entryId": 1, "expectedVersion": original["version"], "description": "Stale overwrite"}})["result"]["isError"]
    for invalid_end in ["2026-09-15T10:00:00", "2026-09-15T10:00:00-07:00 garbage", "2026-09-15T08:00:00-07:00", "2026-09-15T11:00:00.5-07:00"]:
        assert request("tools/call", {"name": "edit_entry", "arguments": {"entryId": 1, "expectedVersion": edited["version"], "end": invalid_end}})["result"]["isError"]
    edited = tool("edit_entry", {"entryId": 1, "expectedVersion": edited["version"], "end": "2026-09-15T11:00:00-07:00", "billable": False})
    assert edited["seconds"] == 7200 and not edited["billable"]
    assert tool("get_hours", {"period": "custom", "from": "2026-09-01", "to": "2026-09-30"})["total_seconds"] == 7200
    addition = {"projectId": 1, "start": "2026-08-30T10:00:00-07:00", "end": "2026-08-30T10:48:14-07:00"}
    added = tool("add_entry", addition)
    assert added["status"] == "added" and added["seconds"] == 2894 and added["description"] == ""
    assert added["rate"] == 125 and added["currency"] == "USD" and added["billable"] and not added["invoiced"]
    assert tool("get_entry", {"entryId": added["entry_id"]}) == dict(added, status="found")
    august = tool("get_hours", {"period": "custom", "from": "2026-08-01", "to": "2026-08-31"})
    assert august["total_seconds"] == 2894
    august_entries = tool("list_entries", {"period": "custom", "from": "2026-08-01", "to": "2026-08-31"})["entries"]
    assert len(august_entries) == 1 and august_entries[0]["entry_id"] == added["entry_id"]
    for invalid in [addition, dict(addition, start="2026-08-30T10:20:00-07:00"),
                    dict(addition, projectId=999), dict(addition, end=addition["start"]),
                    dict(addition, end="2026-08-30T09:00:00-07:00"), dict(addition, rate=-1), dict(addition, rate=100000001),
                    dict(addition, start="2026-08-30T10:00:00"), dict(addition, end="2026-08-30T10:48:14.5-07:00")]:
        assert request("tools/call", {"name": "add_entry", "arguments": invalid})["result"]["isError"]
    with sqlite3.connect(database) as db:
        assert db.execute("SELECT COUNT(*) FROM entries").fetchone()[0] == 2
        db.execute("UPDATE projects SET archived=1 WHERE id=1")
    separate = dict(addition, start="2026-08-30T11:00:00-07:00", end="2026-08-30T11:00:03-07:00")
    assert request("tools/call", {"name": "add_entry", "arguments": separate})["result"]["isError"]
    with sqlite3.connect(database) as db:
        db.execute("UPDATE projects SET archived=0 WHERE id=1")
        db.execute("UPDATE clients SET archived=1 WHERE id=1")
    assert request("tools/call", {"name": "add_entry", "arguments": separate})["result"]["isError"]
    with sqlite3.connect(database) as db: db.execute("UPDATE clients SET archived=0 WHERE id=1")
    overridden = tool("add_entry", dict(separate, description="Imported Ω", rate=80, billable=False, invoiced=True))
    assert overridden["seconds"] == 3 and overridden["rate"] == 80 and not overridden["billable"] and not overridden["invoiced"]
    assert tool("get_context", {})["entry_adding_enabled"]
    timer = tool("start_timer", {"project": "Website Ω", "description": "Actual MCP timer"})
    assert timer["status"] == "started"
    running = tool("get_entry", {"entryId": timer["entry_id"]})
    assert running["running"]
    assert request("tools/call", {"name": "edit_entry", "arguments": {"entryId": timer["entry_id"], "expectedVersion": running["version"], "description": "Must not edit"}})["result"]["isError"]
    assert tool("start_timer", {"projectId": 1})["status"] == "already_running"
    assert tool("get_context", {})["active_timer"]["entry_id"] == timer["entry_id"]
    assert request("tools/call", {"name": "add_entry", "arguments": {"projectId": 1, "start": timer["start"], "end": "2099-01-01T00:00:00Z"}})["result"]["isError"]
    assert request("tools/call", {"name": "stop_timer", "arguments": {"entryId": timer["entry_id"] + 1}})["result"]["isError"]
    stopped = tool("stop_timer", {"entryId": timer["entry_id"]})
    assert stopped["status"] == "stopped" and stopped["duration"] is not None
    replacement = tool("start_timer", {"projectId": 1})
    assert request("tools/call", {"name": "stop_timer", "arguments": {"entryId": timer["entry_id"]}})["result"]["isError"]
    assert tool("get_context", {})["active_timer"]["entry_id"] == replacement["entry_id"]
    tool("stop_timer", {"entryId": replacement["entry_id"]})
    assert tool("get_context", {})["active_timer"] is None
    print("PASS: real MCP discovery, annotations, reports, historical additions/readback, overlap/archive/time guards, full-entry reads, edits and timer controls.")
finally:
    process.stdin.close()
    try: process.wait(timeout=5)
    except subprocess.TimeoutExpired: process.kill(); process.wait()
