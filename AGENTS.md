# TickRiver delivery

After every user-requested implementation change, validate and commit the narrow
change, complete the independent commit review, and update the installed build
used by the user's desktop shortcut before reporting completion.

- Resolve the actual TickRiver `.lnk` target on the user's desktop, including
  redirected desktops such as OneDrive. Inspect matching Start menu shortcuts too.
- Publish the UI and MCP projects together as Release, Windows x64,
  self-contained builds. Use the stable SDK in `work/tools/dotnet` when needed.
- Keep the complete runtime, dependencies, and matching PDBs together. Use a new
  version/commit installation folder alongside the existing shortcut target so
  running UI or MCP processes do not block or partially receive an update.
- Update the existing desktop and matching Start menu shortcuts to the new
  `TickRiver.exe` and its working directory. Preserve their other settings.
- Verify installed files against the published files, run the installed
  executable's `--smoke-test` with disposable data, and verify launching the
  desktop shortcut opens the installed build. An already-running older UI needs
  to exit and reopen; preserve any active timer and the shared user database.
- Leave old installation folders and user data intact. Report the installed
  target and any remaining restart requirement. A repository build alone does
  not complete delivery.
