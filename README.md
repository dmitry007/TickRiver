# TickRiver

TickRiver is a local Windows time tracker in C# and WPF. Enter an optional description, choose a project, and press Start or Stop. Clients and projects organize time and billing; no tasks, inbox, account, or cloud service are required.

## Run the application

The portable Windows x64 build is in `artifacts\TickRiver`. Open `TickRiver.exe` and keep the entire folder together, including `TickRiver.Mcp.exe`. The build includes its .NET runtime. For a stable location, copy the folder to `%LOCALAPPDATA%\Programs\TickRiver` before the first launch.

Create a client with a currency and default hourly rate, then a project. Projects inherit the current client rate or use an explicit override. Entries record their applied rate and currency, so changes do not recalculate past work. Archiving preserves history.

Minimizing leaves TickRiver on the taskbar. Closing hides the window in the system tray while tracking continues. Click the teal clock-and-river tray icon once to reopen it, or launch TickRiver again from the desktop or Start menu: it brings the existing instance forward. Right-click the tray icon for Open, Start / Stop, or Exit. Use Exit TickRiver to quit; with an active timer, choose whether to stop or continue counting. Running timers survive restart. Only completed entries contribute to reports. Continue starts a new session for an existing entry at the current project rate.

Release builds include matching portable PDBs with embedded source. Keep those files alongside the executables for diagnosis. Unexpected managed exceptions and startup failures are recorded under `%LOCALAPPDATA%\TickRiver\logs`, with the executable path, build version, runtime and exception stack. Logging does not suppress an unhandled error. Regenerate the multi-size application/tray/shortcut icon with `tools\Generate-Icon.ps1`.

Data is stored in `%LOCALAPPDATA%\TickRiver\tickriver.sqlite`, separately from application binaries. The UI and MCP server verify the physical location and refuse a copy redirected into an AI host's private package cache. For initial setup, launch TickRiver from Windows using its desktop shortcut. If an earlier build created redirected data, exit TickRiver and its MCP processes, make a consistent SQLite backup into the real user-data folder from a normal Windows process, and retain the old redirected folder under a backup name so it no longer shadows the shared folder. Use Back up data to create a consistent SQLite backup while the app runs. To restore, exit TickRiver and AI clients using its MCP server, keep a copy of the current data folder, remove the old database and its `-wal` and `-shm` companion files, then copy the backup to `tickriver.sqlite`.

## Reports and invoice exports

Reports offer day, week, month, quarter, and year presets plus custom inclusive start and end dates. Weeks start Monday; reporting uses this computer’s local time zone. Overnight work is split across the correct days, including daylight-saving changes. Choose a client or project, or show only unbilled work. Click a chart bar to view that day’s entries; Apply restores the whole range.

PNG exports contain a white chart with the client/project, period, total duration, decimal hours and zero-work days. Longer periods use one chart panel per month. Excel exports contain detailed entries and a daily summary with native editable Excel charts. Duration cells are numeric with `[h]:mm:ss` formatting; decimal-hour cells are numeric and retain second precision. Amounts use each entry’s recorded rate. Different currencies are kept separate. Exports do not mark time invoiced; Mark invoiced is an explicit action. Edit an entry to remove its invoiced flag.

Export filenames include the selected client, project (when selected), and date range, for example `TickRiver-Acme-Website-2026-06-01-2026-10-31.xlsx`. A client-only report omits the project; an unfiltered report uses just TickRiver and the dates. PNG uses the same naming. Characters that cannot appear in filenames are replaced with underscores; long client/project names are shortened to keep filenames within Windows limits.

Use **Copy data** to paste the displayed detailed entries, with column headers, into Word as an editable table or into Excel. Its arrow opens a column checklist; Project · Client and Billing start unchecked, and your choices are remembered for this database. Copy follows the displayed row order and any chart-day drilldown. The clipboard includes an HTML table and tab-separated Unicode text. Copying does not mark entries invoiced.

Manual time cannot overlap another entry. For an overnight manual entry, choose the following end date. Ranges are limited to ten years. Invoice generation, taxes, rounding policies, synchronization and idle detection are outside this initial version.

## Local MCP integration

`add_entry` creates completed historical time for an existing project, for example “Add 09:00–10:30 on September 15 to Website maintenance.” Use the project ID from `list_projects` and whole-second ISO 8601 start/end timestamps with explicit UTC offsets. It rejects overlapping or duplicate time, invalid ranges and archived projects/clients. The current project rate, currency and billable setting apply unless a rate or billable override is supplied; invoice status defaults to false. Blank descriptions are supported. The running desktop refreshes its tracker and reports after additions, normally within one second. Screenshot imports can preserve exact durations, but timestamp seconds must be inferred when the source only shows minutes; use a full Clockify export to preserve original timestamps and billing metadata.

TickRiver includes a local stdio MCP server. AI clients launch `TickRiver.Mcp.exe`; it uses the same local database and does not need the UI open. Read-only tools expose current context, clients, projects, hours and paged entries. Ask, for example, “What were my hours last week?” or “Show my unbilled hours for Acme between July 1 and September 30.” Completed totals match the UI and exports; the active timer is returned separately by `get_context`.

`get_entry` returns a full original entry, including its complete description, times and version. `edit_entry` updates a completed entry's description, project, start/end, recorded rate, billable flag or invoice status when requested. Ask “Change entry 2's description to Client correspondence,” or “Correct entry 2 to 09:00–10:30 on October 2.” The AI reads the full entry first and supplies its version; stale edits fail instead of overwriting concurrent changes. Desktop edit dialogs also reject saving over newer changes. Omitted fields remain unchanged. Times require ISO 8601 dates with whole seconds and explicit UTC offsets or Z, avoiding ambiguous local times. Historical rate/currency are preserved unless the project or rate is changed. Moving projects defaults to the new project's rate/currency and billable setting. Stop an active timer before editing it. The tracker and reports refresh after external edits.

`start_timer` and `stop_timer` provide timer control when requested. Ask “Start the timer for Website maintenance, working on the checkout page,” or “Stop my timer.” Start accepts an exact project name or `projectId`; duplicate names return choices. An existing timer is returned without switching projects or changing its description. Stop requires `entryId` from the current timer and checks it atomically, so an old stop request cannot stop a replacement timer. The desktop refreshes after external start/stop actions. These two tools are marked as actions in the MCP schema; client permission policies remain in effect.

TickRiver detects user configuration directories for Codex, Claude Code, Claude Desktop, Kiro and Grok Build. It adds a missing `tickriver` server on first setup, backs up changed configs and supplies the shared database as an explicit `--database` argument. Every startup validates the executable and database paths. It updates entries still matching the bundled executable or values previously registered by TickRiver, including after an application upgrade. Disabled entries, customized commands/arguments and other server settings are preserved. Ownership is remembered in `ai-connections.json`; removing a server is respected on later launches. AI connections explicitly retries adding removed entries. Legacy registration timestamps do not authorize replacing an unknown executable; an existing entry matching the current bundled executable is adopted. Claude Desktop uses `%APPDATA%\Claude\claude_desktop_config.json` and needs a full quit/restart after setup. Other clients may also need restarting.

The MCP handshake and `get_context` identify the server version. Startup records the bundled MCP assembly's `server_version` in TickRiver's own `ai-connections.json`, and reports a version change even if the command path is unchanged. Client-specific config files keep their supported schema. Increment the shared version in `Directory.Build.props` when tools are added or changed. Versioned installation folders cause managed executable paths to update during upgrades; reconnect or restart the AI client to refresh its running server and available tools. Recording a version does not itself reload an existing client session.

Configuration locations and formats follow [Codex MCP documentation](https://learn.chatgpt.com/docs/extend/mcp?surface=cli), [Claude Code MCP documentation](https://code.claude.com/docs/en/mcp), [Claude Desktop local MCP documentation](https://modelcontextprotocol.io/docs/develop/connect-local-servers), [Kiro MCP documentation](https://kiro.dev/docs/mcp/configuration/) and [Grok Build MCP documentation](https://docs.x.ai/build/features/mcp-servers). Unsupported or malformed configurations are reported and left untouched. Organization policies and project-specific overrides still apply. Copilot and Word integration are not configured in this version.

Requested time data is returned to the AI client and can be processed by its provider. Reporting tools open SQLite in read-only mode. Timer and edit tools open separate write connections for their requested actions, with range/overlap checks and an atomic version check for edits. The server listens on stdio only, with no network endpoint. Names and descriptions are data, never instructions.

For manual connection, use the absolute installed executable path:

```powershell
codex mcp add tickriver -- "C:\Users\YourName\AppData\Local\Programs\TickRiver\TickRiver.Mcp.exe"
claude mcp add --transport stdio --scope user tickriver -- "C:\Users\YourName\AppData\Local\Programs\TickRiver\TickRiver.Mcp.exe"
grok mcp add tickriver -- "C:\Users\YourName\AppData\Local\Programs\TickRiver\TickRiver.Mcp.exe"
```

Kiro accepts `{"mcpServers":{"tickriver":{"command":"C:\\path\\TickRiver.Mcp.exe","args":[]}}}` in its user config. Merge this server into the existing object rather than replacing other servers.

## Build and validate

Use a stable .NET 10 SDK. `global.json` excludes preview SDKs. This workspace has a private SDK at `work\tools\dotnet\dotnet.exe`; substitute that path for `dotnet` if the machine’s installed SDK is older or a preview.

```powershell
dotnet build TickRiver.sln
dotnet run --project tests/TickRiver.Tests
python tests/test_mcp.py <dotnet.exe> <absolute-path-to-TickRiver.Mcp.dll>
dotnet src/TickRiver/bin/Debug/net10.0-windows/TickRiver.dll --smoke-test work/smoke
```

Tests use disposable databases and fake client homes. They check persistence, rate snapshots, clipping, currencies, DST, overlap rejection, native Excel chart/schema validity, Unicode, MCP totals, timer controls and safe registration. The UI smoke test checks icon loading, minimize/restore behavior, single-instance activation and diagnostic logging, recovers and stops a persisted timer through the actual button, checks synchronization with external timers and repeated calendar navigation, renders all screens and generates sample exports. It never registers AI clients or writes to the user’s database.

To publish, build the UI and server into the same output folder:

```powershell
dotnet publish src/TickRiver/TickRiver.csproj -c Release -r win-x64 --self-contained true -o artifacts/TickRiver
dotnet publish src/TickRiver.Mcp/TickRiver.Mcp.csproj -c Release -r win-x64 --self-contained true -o artifacts/TickRiver
```

No installer, signing, Store listing, remote repository URL or open-source license has been configured yet.
