using System.Globalization;
using System.IO.Compression;
using System.Xml.Linq;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using TickRiver.Core;

var directory = Path.Combine(Path.GetTempPath(), "TickRiver-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var store = new Store(Path.Combine(directory, "test.sqlite"));
var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
void Reject(Action action, string message) { try { action(); } catch (ArgumentException) { checks++; return; } throw new Exception(message); }
var client = store.SaveClient(0, "Acme", "USD", 120, false);
var project = store.SaveProject(0, client, "Website", null, true, false);
var other = store.SaveProject(0, client, "Design", 160, true, false);
Check(store.Projects().Single(p => p.Id == project).EffectiveRate == 120, "Client inheritance");
Check(store.Projects().Single(p => p.Id == other).EffectiveRate == 160, "Project override");
var start = new DateTimeOffset(2026, 9, 1, 23, 30, 0, TimeSpan.Zero);
store.Start(project, "Overnight", start);
Check(new Store(store.Path).Entries().Single().End == null, "Timer survives repository restart");
Reject(() => store.Start(other, "Duplicate", start.AddSeconds(1)), "Second active timer accepted");
store.SaveClient(client, "Acme", "EUR", 200, false);
store.Stop(start.AddSeconds(5430));
var entry = store.Entries().Single();
Check(entry.Rate == 120 && entry.Currency == "USD", "Historic rate and currency changed");
Check(entry.Duration == "01:30:30", "Second precision lost");
Reject(() => store.SaveEntry(0, other, "Overlap", start.AddMinutes(1), start.AddMinutes(3), 160, true, false), "Overlapping entry accepted");
Reject(() => store.SaveEntry(0, other, "Backwards", start, start.AddSeconds(-1), 160, true, false), "Invalid interval accepted");
store.Start(project, "Future rate", start.AddDays(2)); store.Stop(start.AddDays(2).AddHours(1));
Check(store.Entries().First().Rate == 200 && store.Entries().First().Currency == "EUR", "Future rate not inherited");
var report = ReportBuilder.Build(store.Entries(), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), "Acme", zone: TimeZoneInfo.Utc);
Check(report.Days.Count == 30 && report.Days[0].Seconds == 1800 && report.Days[1].Seconds == 3630, "Midnight splitting failed");
Check(report.Days[4].Seconds == 0, "Zero-work day missing");
Check(report.Seconds == 9030, "Aggregate mismatch");
Check(report.Rows.Where(r => r.Entry.Id == entry.Id).Sum(r => r.DecimalHours) == 5430m / 3600m, "Decimal hours rounded prematurely");
Check(report.AmountLabel.Contains("USD 181.00") && report.AmountLabel.Contains("EUR 200.00"), "Currencies combined or amount wrong");
var clipped = ReportBuilder.Build(store.Entries(), new DateOnly(2026, 9, 2), new DateOnly(2026, 9, 2), "Acme", zone: TimeZoneInfo.Utc);
Check(clipped.Seconds == 3630, "Inclusive range clipping");
Reject(() => store.MarkInvoiced(clipped, true), "Partial entries marked invoiced");
store.MarkInvoiced(report, true);
Check(ReportBuilder.Build(store.Entries(), report.From, report.To, "Acme", unbilledOnly: true, zone: TimeZoneInfo.Utc).Seconds == 0, "Unbilled filter failed");
store.MarkInvoiced(report, false);
store.SaveProject(project, client, "Website", null, true, true);
Reject(() => store.Start(project, "Archived", start.AddDays(10)), "Archived project can track");
Check(store.Entries().Count == 2, "Archiving lost history");
store.SaveProject(project, client, "Website", null, true, false);
var longStart = start.AddDays(10);
store.SaveEntry(0, project, "Long session", longStart, longStart.AddHours(25), 200, true, false);
Check(store.Entries().First().Duration == "25:00:00", "Duration wrapped after 24h");
var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
var dstStart = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
var dstEntry = entry with { Start = dstStart, End = dstStart.AddHours(25) };
Check(ReportBuilder.Build([dstEntry], new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 1), "DST", zone: zone).Seconds == 90000, "Fall-back day clipped to 24h");
var backup = Path.Combine(directory, "backup.sqlite"); store.Backup(backup);
Check(new Store(backup).Entries().Count == 3, "SQLite live backup incomplete");
Reject(() => store.Backup(store.Path), "Self backup accepted");
var xlsx = Path.Combine(directory, "report.xlsx"); ExcelExport.Save(report, xlsx);
using (var document = SpreadsheetDocument.Open(xlsx, false))
{
    var errors = new OpenXmlValidator().Validate(document).ToArray();
    Check(errors.Length == 0, "Invalid Excel XML: " + string.Join("; ", errors.Select(e => e.Description + " at " + e.Path?.XPath)));
    Check(document.WorkbookPart!.WorksheetParts.Count() == 2, "Missing worksheet");
    Check(document.WorkbookPart.WorksheetParts.Sum(p => p.DrawingsPart?.ChartParts.Count() ?? 0) == 1, "Missing editable chart");
}
using (var zip = ZipFile.OpenRead(xlsx))
{
    XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var xml = XDocument.Load(stream);
    var cell = xml.Descendants(ns + "c").Single(c => c.Attribute("r")?.Value == "H5");
    Check(decimal.Parse(cell.Element(ns + "v")!.Value, CultureInfo.InvariantCulture) == 0.5m, "Excel decimal hours are text or incorrect");
}
var multi = ReportBuilder.Build(store.Entries(), new DateOnly(2026, 7, 1), new DateOnly(2026, 9, 30), "Quarter", zone: TimeZoneInfo.Utc);
ExcelExport.Save(multi, xlsx);
using (var document = SpreadsheetDocument.Open(xlsx, false))
{
    Check(!new OpenXmlValidator().Validate(document).Any(), "Invalid quarterly workbook");
    Check(document.WorkbookPart!.WorksheetParts.Sum(p => p.DrawingsPart?.ChartParts.Count() ?? 0) == 3, "Quarter requires three charts");
}
var readOnly = new Store(store.Path, readOnly: true);
Check(readOnly.Entries().Count == 3, "Read-only store cannot query");
try { readOnly.SaveClient(0, "Forbidden", "USD", 1, false); throw new Exception("Read-only store wrote to database"); }
catch (Microsoft.Data.Sqlite.SqliteException) { checks++; }
var tools = new TickRiver.Mcp.TimeTools(readOnly);
using (var hours = System.Text.Json.JsonDocument.Parse(tools.GetHours("custom", "2026-09-01", "2026-09-30")))
{
    var localReport = ReportBuilder.Build(store.Entries(), report.From, report.To, "Same report");
    Check(hours.RootElement.GetProperty("total_seconds").GetInt64() == localReport.Seconds, "MCP and UI totals differ");
}
using (var page = System.Text.Json.JsonDocument.Parse(tools.ListEntries("custom", "2026-09-01", "2026-09-30", limit: 1)))
    Check(page.RootElement.GetProperty("entries").GetArrayLength() == 1 && page.RootElement.GetProperty("next_offset").GetInt32() == 1, "MCP pagination broken");
Reject(() => tools.GetHours("last_week", from: "2026-09-01"), "MCP ignores contradictory dates");
Reject(() => tools.GetHours("custom", "2026-09-01", "2026-09-30", clientId: 999), "Unknown ID accepted");
Reject(() => tools.ListClients(limit: 500), "Unbounded tool results accepted");
var originalToml = "# Preserve comment\nmodel = \"example\"\n[mcp_servers.other]\ncommand = \"other.exe\"\n";
var registeredToml = ClientRegistration.AddToml(originalToml, "C:\\Program Files\\TickRiver\\TickRiver.Mcp.exe")!;
Check(registeredToml.StartsWith(originalToml.TrimEnd()), "TOML setup rewrote unrelated content");
Check(ClientRegistration.AddToml(registeredToml, "somewhere-else.exe") == null, "Existing TOML registration overwritten");
Check(ClientRegistration.AddToml("[mcp_servers.'tickriver']\nenabled = false\ncommand = \"custom\"\n", "new.exe") == null, "Disabled quoted TOML entry overridden");
try { ClientRegistration.AddToml("invalid = {", "new.exe"); throw new Exception("Invalid TOML was modified"); }
catch (Tomlyn.TomlException) { checks++; }
var registeredJson = ClientRegistration.AddJson("{\"settings\":{\"theme\":\"dark\"},\"mcpServers\":{\"other\":{\"command\":\"other.exe\"}}}", "new.exe", true)!;
using (var json = System.Text.Json.JsonDocument.Parse(registeredJson))
    Check(json.RootElement.GetProperty("settings").GetProperty("theme").GetString() == "dark" && json.RootElement.GetProperty("mcpServers").GetProperty("other").GetProperty("command").GetString() == "other.exe", "JSON setup lost unrelated configuration");
Check(ClientRegistration.AddJson("{\"mcpServers\":{\"tickriver\":{\"disabled\":true}}}", "new.exe", false) == null, "Disabled JSON entry overwritten");
Reject(() => ClientRegistration.AddJson("{\"mcpServers\":{},\"mcpServers\":{}}", "new.exe", false), "Duplicate JSON keys accepted");
var fakeHome = Path.Combine(directory, "fake-home");
Directory.CreateDirectory(Path.Combine(fakeHome, ".codex")); Directory.CreateDirectory(Path.Combine(fakeHome, ".kiro")); Directory.CreateDirectory(Path.Combine(fakeHome, ".claude"));
File.WriteAllText(Path.Combine(fakeHome, ".codex", "config.toml"), originalToml);
var fakeExecutable = Path.Combine(directory, "TickRiver.Mcp.exe"); File.WriteAllText(fakeExecutable, "fixture only");
var statePath = Path.Combine(directory, "state.json");
var setup = ClientRegistration.Register(fakeExecutable, fakeHome, statePath);
Check(setup.Count(s => s.Contains("connected")) == 3, "First-run setup did not connect clients: " + string.Join(";", setup));
var kiroConfig = Path.Combine(fakeHome, ".kiro", "settings", "mcp.json");
File.WriteAllText(kiroConfig, "{\"mcpServers\":{}}");
ClientRegistration.Register(fakeExecutable, fakeHome, statePath);
Check(File.ReadAllText(kiroConfig) == "{\"mcpServers\":{}}", "Removed connection was silently reinstalled");
ClientRegistration.Register(fakeExecutable, fakeHome, statePath, retry: true);
Check(File.ReadAllText(kiroConfig).Contains("tickriver"), "Explicit reconnect did not work");
var desktopFolder = Path.Combine(fakeHome, "AppData", "Roaming", "Claude"); Directory.CreateDirectory(desktopFolder);
File.WriteAllText(Path.Combine(desktopFolder, "claude_desktop_config.json"), "{\"preferences\":{\"theme\":\"dark\"},\"mcpServers\":{\"other\":{\"command\":\"other.exe\"}}}");
var newerExecutable = Path.Combine(directory, "updated", "TickRiver.Mcp.exe"); Directory.CreateDirectory(Path.GetDirectoryName(newerExecutable)!); File.WriteAllText(newerExecutable, "fixture");
var sharedDatabase = Path.Combine(directory, "shared.sqlite");
ClientRegistration.Register(newerExecutable, fakeHome, statePath, databasePath: sharedDatabase);
var kiroNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(kiroConfig))!;
Check(kiroNode["mcpServers"]!["tickriver"]!["command"]!.GetValue<string>() == newerExecutable, "Owned executable not updated");
Check(kiroNode["mcpServers"]!["tickriver"]!["args"]![1]!.GetValue<string>() == sharedDatabase, "Owned database not updated");
var desktopNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(desktopFolder, "claude_desktop_config.json")))!;
Check(desktopNode["preferences"]!["theme"]!.GetValue<string>() == "dark" && desktopNode["mcpServers"]!["other"]!["command"]!.GetValue<string>() == "other.exe"
    && desktopNode["mcpServers"]!["tickriver"]!["command"]!.GetValue<string>() == newerExecutable, "Claude Desktop registration damaged existing config");
var codexConfig = Path.Combine(fakeHome, ".codex", "config.toml");
Check(File.ReadAllText(codexConfig).StartsWith(originalToml.TrimEnd()) && File.ReadAllText(codexConfig).Contains(sharedDatabase.Replace("\\", "\\\\")), "TOML update lost comments or database");
kiroNode["mcpServers"]!["tickriver"]!["disabled"] = true; File.WriteAllText(kiroConfig, kiroNode.ToJsonString());
var disabledConfig = File.ReadAllText(kiroConfig);
ClientRegistration.Register(fakeExecutable, fakeHome, statePath, retry: true);
Check(File.ReadAllText(kiroConfig) == disabledConfig, "Disabled entry changed on upgrade or reconnect");
kiroNode["mcpServers"]!["tickriver"]!.AsObject().Remove("disabled");
kiroNode["mcpServers"]!["tickriver"]!["command"] = "C:\\Custom\\TickRiver.Mcp.exe";
File.WriteAllText(kiroConfig, kiroNode.ToJsonString()); var customConfig = File.ReadAllText(kiroConfig);
ClientRegistration.Register(fakeExecutable, fakeHome, statePath);
Check(File.ReadAllText(kiroConfig) == customConfig, "Customized executable overwritten");
kiroNode["mcpServers"]!["tickriver"]!["command"] = fakeExecutable;
kiroNode["mcpServers"]!["tickriver"]!["args"] = new System.Text.Json.Nodes.JsonArray("--database", "C:\\Custom\\data.sqlite");
kiroNode["mcpServers"]!["tickriver"]!["env"] = new System.Text.Json.Nodes.JsonObject { ["TEST"] = "keep" };
File.WriteAllText(kiroConfig, kiroNode.ToJsonString());
ClientRegistration.Register(fakeExecutable, fakeHome, statePath);
ClientRegistration.Register(newerExecutable, fakeHome, statePath, databasePath: sharedDatabase);
kiroNode = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(kiroConfig))!;
Check(kiroNode["mcpServers"]!["tickriver"]!["command"]!.GetValue<string>() == newerExecutable && kiroNode["mcpServers"]!["tickriver"]!["args"]![1]!.GetValue<string>() == "C:\\Custom\\data.sqlite"
    && kiroNode["mcpServers"]!["tickriver"]!["env"]!["TEST"]!.GetValue<string>() == "keep", "Custom arguments or environment overwritten");
File.WriteAllText(codexConfig, "# Unicode Ω\nmcp_servers.'tickriver' = { command = '" + newerExecutable + "', args = ['--database', '" + sharedDatabase + "'], startup_timeout_sec = 42 } # trailing\n");
ClientRegistration.Register(fakeExecutable, fakeHome, statePath, databasePath: sharedDatabase);
Check(File.ReadAllText(codexConfig).StartsWith("# Unicode Ω") && File.ReadAllText(codexConfig).EndsWith("# trailing\n") && File.ReadAllText(codexConfig).Contains("startup_timeout_sec = 42")
    && ((Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(File.ReadAllText(codexConfig))!["mcp_servers"])["tickriver"])["command"].ToString() == fakeExecutable, "Inline quoted TOML update damaged config");
var legacyState = Path.Combine(directory, "legacy-state.json"); File.WriteAllText(legacyState, "{\"Kiro\":\"2026-10-01\"}");
File.WriteAllText(codexConfig, "# dotted Ω\nmcp_servers.tickriver.'command' = '" + fakeExecutable + "' # command comment\nmcp_servers.tickriver.args = ['--database', '" + sharedDatabase + "'] # args comment\nmcp_servers.tickriver.startup_timeout_sec = 42\n");
var newerDatabase = Path.Combine(directory, "shared-new.sqlite");
ClientRegistration.Register(newerExecutable, fakeHome, statePath, databasePath: newerDatabase);
var dottedEntry = (Tomlyn.Model.TomlTable)((Tomlyn.Model.TomlTable)Tomlyn.TomlSerializer.Deserialize<Tomlyn.Model.TomlTable>(File.ReadAllText(codexConfig))!["mcp_servers"])["tickriver"];
Check(dottedEntry["command"].ToString() == newerExecutable && ((Tomlyn.Model.TomlArray)dottedEntry["args"])[1]?.ToString() == newerDatabase, "Owned dotted-key TOML registration not upgraded");
Check(File.ReadAllText(codexConfig).Contains("# command comment") && File.ReadAllText(codexConfig).Contains("# args comment") && File.ReadAllText(codexConfig).Contains("startup_timeout_sec = 42"), "Dotted TOML upgrade changed comments or unrelated settings");
File.WriteAllText(kiroConfig, customConfig); ClientRegistration.Register(fakeExecutable, fakeHome, legacyState);
Check(File.ReadAllText(kiroConfig) == customConfig, "Legacy timestamp claimed ownership of custom executable");
File.WriteAllText(kiroConfig, "{\"mcpServers\":{}}"); ClientRegistration.Register(fakeExecutable, fakeHome, legacyState);
Check(File.ReadAllText(kiroConfig) == "{\"mcpServers\":{}}", "Legacy removed entry reinstalled");
var validationFolder = Path.Combine(Environment.CurrentDirectory, "work", "storage-validation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(validationFolder);
var validationDatabase = Path.Combine(validationFolder, "data.sqlite"); UserStorage.Validate(validationDatabase);
Check(!File.Exists(validationDatabase) && !Directory.GetFiles(validationFolder).Any(), "Storage validation created a database or left a probe");
var validationStore = new Store(validationDatabase); var validationClient = validationStore.SaveClient(0, "Fixture", "USD", 1, false);
var validationProject = validationStore.SaveProject(0, validationClient, "Fixture", null, true, false); validationStore.Start(validationProject, "Active timer", start);
UserStorage.Validate(validationDatabase, readOnly: true);
var migrationBackup = Path.Combine(validationFolder, "moved.sqlite"); validationStore.Backup(migrationBackup);
File.Move(migrationBackup, Path.Combine(validationFolder, "renamed.sqlite"));
Check(new Store(Path.Combine(validationFolder, "renamed.sqlite"), readOnly: true).Entries().Single().End == null, "Backup lost running timer or kept file locked");
var cuba = TimeZoneInfo.FindSystemTimeZoneById("Cuba Standard Time");
var springMidnight = new DateOnly(2026, 3, 8);
Check(ReportBuilder.Build([], springMidnight, springMidnight, "Skipped midnight", zone: cuba).Days.Count == 1, "Skipped midnight breaks report");
var fallMidnight = new DateOnly(2026, 11, 1);
var cubaStart = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-4));
Check(ReportBuilder.Build([entry with { Start = cubaStart, End = cubaStart.AddHours(25) }], fallMidnight, fallMidnight, "Midnight fall-back", zone: cuba).Seconds == 90000, "Ambiguous midnight lost an hour");
var ambiguousStart = new DateTimeOffset(2026, 11, 1, 1, 15, 0, TimeSpan.FromHours(-4));
var ambiguousEnd = new DateTimeOffset(2026, 11, 1, 1, 45, 0, TimeSpan.FromHours(-5));
Check(TimeFormat.ParseLocal(new DateTime(2026, 11, 1), "01:15:00", ambiguousStart, zone) == ambiguousStart, "Unchanged ambiguous start loses original instant");
Check(TimeFormat.ParseLocal(new DateTime(2026, 11, 1), "01:45:00", ambiguousEnd, zone) == ambiguousEnd, "Unchanged ambiguous end loses original instant");
Reject(() => TimeFormat.ParseLocal(new DateTime(2026, 11, 1), "01:30:00", ambiguousStart, zone), "Changed ambiguous time accepted without disambiguation");
var unicodeEntry = entry with { ClientName = "Studio 🚀", ProjectName = "Build 🛠", Description = "Work 🎉\u0001" };
ExcelExport.Save(ReportBuilder.Build([unicodeEntry], report.From, report.To, "Unicode", zone: TimeZoneInfo.Utc), xlsx);
using (var zip = ZipFile.OpenRead(xlsx))
{
    using var stream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open(); var xml = XDocument.Load(stream);
    Check(xml.ToString().Contains("Studio 🚀") && xml.ToString().Contains("Build 🛠") && xml.ToString().Contains("Work 🎉"), "Excel discarded surrogate pairs");
}
var timerTools = new TickRiver.Mcp.TimerTools(readOnly);
var duplicateClient = store.SaveClient(0, "Other client", "USD", 50, false);
store.SaveProject(0, duplicateClient, "Website", null, true, false);
using (var result = System.Text.Json.JsonDocument.Parse(timerTools.StartTimer(project: "Website")))
    Check(result.RootElement.GetProperty("status").GetString() == "ambiguous_project", "Ambiguous project started without a choice");
Check(store.Entries(runningOnly: true).Count == 0, "Ambiguous lookup changed timer");
long timerId;
using (var result = System.Text.Json.JsonDocument.Parse(timerTools.StartTimer(project: "Website", client: "Acme", description: "MCP work")))
{
    Check(result.RootElement.GetProperty("status").GetString() == "started", "MCP failed to start");
    timerId = result.RootElement.GetProperty("entry_id").GetInt64();
}
using (var result = System.Text.Json.JsonDocument.Parse(timerTools.StartTimer(projectId: other, description: "Must not switch")))
    Check(result.RootElement.GetProperty("status").GetString() == "already_running" && result.RootElement.GetProperty("entry_id").GetInt64() == timerId, "MCP switched or duplicated an active timer");
Check(store.Entries(runningOnly: true).Single().Description == "MCP work", "Active description overwritten");
Reject(() => timerTools.StopTimer(timerId + 1), "Mismatched timer stop accepted");
using (var result = System.Text.Json.JsonDocument.Parse(timerTools.StopTimer(timerId)))
    Check(result.RootElement.GetProperty("status").GetString() == "stopped" && result.RootElement.GetProperty("duration").GetString() != null, "MCP failed to save stop");
timerTools.StartTimer(projectId: other);
Reject(() => timerTools.StopTimer(timerId), "Delayed stop affected a replacement timer");
Check(store.Entries(runningOnly: true).Single().ProjectId == other, "Replacement timer changed");
store.Stop(TimeFormat.Now);
long deletedTimerId;
using (var result = System.Text.Json.JsonDocument.Parse(timerTools.StartTimer(projectId: project))) deletedTimerId = result.RootElement.GetProperty("entry_id").GetInt64();
timerTools.StopTimer(deletedTimerId); store.DeleteEntry(deletedTimerId);
long replacementId;
using (var result = System.Text.Json.JsonDocument.Parse(timerTools.StartTimer(projectId: project))) replacementId = result.RootElement.GetProperty("entry_id").GetInt64();
Check(replacementId > deletedTimerId, "Deleted highest timer ID was reused");
Reject(() => timerTools.StopTimer(deletedTimerId), "Deleted timer ID stopped replacement timer");
timerTools.StopTimer(replacementId);
var oldPath = Path.Combine(directory, "old-schema.sqlite");
using (var old = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=" + oldPath))
{
    old.Open(); using var command = old.CreateCommand();
    command.CommandText = """
        CREATE TABLE clients(id INTEGER PRIMARY KEY,name TEXT NOT NULL,currency TEXT NOT NULL,rate TEXT NOT NULL,archived INTEGER NOT NULL);
        CREATE TABLE projects(id INTEGER PRIMARY KEY,client_id INTEGER NOT NULL REFERENCES clients(id),name TEXT NOT NULL,rate TEXT,billable INTEGER NOT NULL,archived INTEGER NOT NULL);
        CREATE TABLE entries(id INTEGER PRIMARY KEY,project_id INTEGER NOT NULL REFERENCES projects(id),description TEXT NOT NULL,start INTEGER NOT NULL,end INTEGER,rate TEXT NOT NULL,currency TEXT NOT NULL,billable INTEGER NOT NULL,invoiced INTEGER NOT NULL);
        INSERT INTO clients VALUES(1,'Old client','USD','99',0);
        INSERT INTO projects VALUES(1,1,'Old project',NULL,1,0);
        INSERT INTO entries VALUES(40,1,'Old timer',638000000000000000,NULL,'99','USD',1,0);
        PRAGMA user_version=1;
        """;
    command.ExecuteNonQuery();
}
var upgraded = new Store(oldPath);
Check(upgraded.Entries(runningOnly: true).Single().Id == 40 && upgraded.Entries().Single().Rate == 99, "Schema upgrade lost active timer or rate snapshot");
upgraded.Stop(TimeFormat.Now, 40); upgraded.DeleteEntry(40);
Check(upgraded.Start(1, "After upgrade", TimeFormat.Now) > 40, "Schema upgrade did not preserve monotonic IDs");
Console.WriteLine($"PASS: {checks} checks. Artifacts: {directory}");
