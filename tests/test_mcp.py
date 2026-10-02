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
    process.stdin.write('{"jsonrpc":"2.0","method":"notifications/initialized"}\n'); process.stdin.flush()
    tools = request("tools/list", {})["result"]["tools"]
    assert {t["name"] for t in tools} == {"get_context", "list_clients", "list_projects", "get_hours", "list_entries", "start_timer", "stop_timer"}
    assert all(t["annotations"]["readOnlyHint"] == (t["name"] not in ["start_timer", "stop_timer"]) for t in tools)
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
    timer = tool("start_timer", {"project": "Website Ω", "description": "Actual MCP timer"})
    assert timer["status"] == "started"
    assert tool("start_timer", {"projectId": 1})["status"] == "already_running"
    assert tool("get_context", {})["active_timer"]["entry_id"] == timer["entry_id"]
    assert request("tools/call", {"name": "stop_timer", "arguments": {"entryId": timer["entry_id"] + 1}})["result"]["isError"]
    stopped = tool("stop_timer", {"entryId": timer["entry_id"]})
    assert stopped["status"] == "stopped" and stopped["duration"] is not None
    replacement = tool("start_timer", {"projectId": 1})
    assert request("tools/call", {"name": "stop_timer", "arguments": {"entryId": timer["entry_id"]}})["result"]["isError"]
    assert tool("get_context", {})["active_timer"]["entry_id"] == replacement["entry_id"]
    tool("stop_timer", {"entryId": replacement["entry_id"]})
    assert tool("get_context", {})["active_timer"] is None
    print("PASS: real MCP initialization, discovery, annotations, Unicode, totals, periods, bounds, start/stop and stale-timer guards.")
finally:
    process.stdin.close()
    try: process.wait(timeout=5)
    except subprocess.TimeoutExpired: process.kill(); process.wait()
