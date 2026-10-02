using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using Tomlyn;
using Tomlyn.Model;
using Tomlyn.Parsing;
using Tomlyn.Serialization;
using Tomlyn.Syntax;

namespace TickRiver.Core;

public static class ClientRegistration
{
    private static readonly object _lock = new();
    public static IReadOnlyList<string> Register(string executable, string home, string statePath, string? codexHome = null, bool retry = false,
        string? databasePath = null, string? applicationData = null, string? serverVersion = null)
    {
        lock (_lock) return RegisterCore(executable, home, statePath, codexHome, retry, databasePath, applicationData, serverVersion);
    }

    private static IReadOnlyList<string> RegisterCore(string executable, string home, string statePath, string? codexHome, bool retry,
        string? databasePath, string? applicationData, string? serverVersion)
    {
        if (!File.Exists(executable)) return ["The bundled MCP server was not found. Use a published TickRiver build."];
        executable = Path.GetFullPath(executable);
        var serverAssembly = Path.ChangeExtension(executable, ".dll");
        serverVersion ??= File.Exists(serverAssembly) ? AssemblyName.GetAssemblyName(serverAssembly).Version?.ToString(3) ?? "unknown" : "unknown";
        var arguments = new[] { "--database", Path.GetFullPath(databasePath ?? UserStorage.DatabasePath) };
        var originalState = File.Exists(statePath) ? File.ReadAllText(statePath) : null;
        var state = originalState == null ? new JsonObject() : JsonNode.Parse(originalState)?.AsObject() ?? new JsonObject();
        var results = new List<string>();
        var codex = codexHome ?? System.IO.Path.Combine(home, ".codex");
        var desktop = Path.Combine(applicationData ?? Path.Combine(home, "AppData", "Roaming"), "Claude");
        var clients = new[] {
            (Name: "Codex", Folder: codex, Config: System.IO.Path.Combine(codex, "config.toml"), Toml: true),
            (Name: "Claude Code", Folder: System.IO.Path.Combine(home, ".claude"), Config: System.IO.Path.Combine(home, ".claude.json"), Toml: false),
            (Name: "Claude Desktop", Folder: desktop, Config: Path.Combine(desktop, "claude_desktop_config.json"), Toml: false),
            (Name: "Kiro", Folder: System.IO.Path.Combine(home, ".kiro"), Config: System.IO.Path.Combine(home, ".kiro", "settings", "mcp.json"), Toml: false),
            (Name: "Grok Build", Folder: System.IO.Path.Combine(home, ".grok"), Config: System.IO.Path.Combine(home, ".grok", "config.toml"), Toml: true) };
        foreach (var client in clients)
        {
            if (!Directory.Exists(client.Folder) && !File.Exists(client.Config)) { results.Add($"{client.Name}: not detected."); continue; }
            try
            {
                var previous = state[client.Name] as JsonObject;
                var handled = state.ContainsKey(client.Name) && (previous == null || SamePath(previous["config_path"]?.GetValue<string>(), client.Config));
                var ownedCommand = handled ? previous?["command"]?.GetValue<string>() : null;
                var ownedArguments = handled ? previous?["args"]?.Deserialize<string[]>() : null;
                var original = File.Exists(client.Config) ? File.ReadAllText(client.Config) : null;
                var change = client.Toml
                    ? UpdateToml(original ?? "", executable, arguments, ownedCommand, ownedArguments, !handled || retry)
                    : UpdateJson(original ?? "{}", executable, arguments, ownedCommand, ownedArguments, !handled || retry, client.Name == "Claude Code");
                if (change.Text != null) WriteConfig(client.Config, original, change.Text);
                state[client.Name] = new JsonObject { ["checked_at"] = DateTimeOffset.UtcNow.ToString("O"), ["config_path"] = Path.GetFullPath(client.Config),
                    ["command"] = change.Command ?? ownedCommand, ["args"] = JsonSerializer.SerializeToNode(change.Arguments ?? ownedArguments),
                    ["server_version"] = change.Command == null ? previous?["server_version"]?.GetValue<string>() : serverVersion };
                var versionChanged = change.Command != null && previous?["server_version"]?.GetValue<string>() != serverVersion;
                results.Add($"{client.Name}: {change.Status}" + (versionChanged ? $" Server version {serverVersion}; reconnect the client to refresh available tools." : ""));
            }
            catch (Exception error) { results.Add($"{client.Name}: could not connect — {error.Message}"); }
        }
        WriteConfig(statePath, originalState, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), backup: false);
        return results;
    }

    private sealed record Change(string? Text, string? Command, string[]? Arguments, string Status);
    private static bool SamePath(string? left, string right) => left != null && Path.IsPathFullyQualified(left) && Path.IsPathFullyQualified(right)
        && string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
    private static bool ManagedArguments(string[]? current, string[] desired, string[]? owned) => current != null
        && (current.Length == 0 || current.SequenceEqual(desired) || owned != null && current.SequenceEqual(owned));
    private static Change Preserved(string reason) => new(null, null, null, reason + "; user configuration preserved.");

    private static Change UpdateJson(string original, string executable, string[] arguments, string? ownedCommand, string[]? ownedArguments, bool add, bool claudeCode)
    {
        using var document = JsonDocument.Parse(original); RejectDuplicateKeys(document.RootElement);
        var root = JsonNode.Parse(original) as JsonObject ?? throw new ArgumentException("Client config must be a JSON object.");
        var servers = root["mcpServers"] as JsonObject;
        if (root.ContainsKey("mcpServers") && servers == null) throw new ArgumentException("mcpServers must be a JSON object.");
        if (servers?.ContainsKey("tickriver") != true)
        {
            if (!add) return Preserved("TickRiver was removed");
            servers ??= new JsonObject(); if (!root.ContainsKey("mcpServers")) root["mcpServers"] = servers;
            var created = new JsonObject { ["command"] = executable, ["args"] = JsonSerializer.SerializeToNode(arguments) };
            if (claudeCode) created["type"] = "stdio";
            servers["tickriver"] = created;
            return new(root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), executable, arguments, "TickRiver connected. Restart the client if needed.");
        }
        if (servers["tickriver"] is not JsonObject entry) throw new ArgumentException("TickRiver configuration must be an object.");
        if (entry["disabled"]?.GetValue<bool>() == true || entry["enabled"]?.GetValue<bool>() == false) return Preserved("TickRiver is disabled");
        if (entry.ContainsKey("url") || entry["type"] != null && entry["type"]!.GetValue<string>() != "stdio") return Preserved("Custom TickRiver transport");
        var command = entry["command"]?.GetValue<string>();
        if (!SamePath(command, executable) && (ownedCommand == null || !SamePath(command, ownedCommand))) return Preserved("Custom TickRiver executable");
        var current = entry.ContainsKey("args") ? entry["args"]?.Deserialize<string[]>() : [];
        var managed = ManagedArguments(current, arguments, ownedArguments);
        var changed = !SamePath(command, executable) || managed && !current!.SequenceEqual(arguments);
        if (changed) { entry["command"] = executable; if (managed) entry["args"] = JsonSerializer.SerializeToNode(arguments); }
        return new(changed ? root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) : null, executable, managed ? arguments : null,
            managed ? changed ? "TickRiver connected; bundled executable and database updated. Restart the client if needed." : "Bundled executable and database verified." : "Bundled executable verified; custom arguments preserved.");
    }

    private static Change UpdateToml(string original, string executable, string[] arguments, string? ownedCommand, string[]? ownedArguments, bool add)
    {
        var metadata = new TomlMetadataStore();
        var root = TomlSerializer.Deserialize<TomlTable>(original, new TomlSerializerOptions { MetadataStore = metadata }) ?? new TomlTable();
        var servers = root.TryGetValue("mcp_servers", out var value) ? value as TomlTable ?? throw new ArgumentException("mcp_servers must be a TOML table.") : null;
        if (servers?.ContainsKey("tickriver") != true)
        {
            if (!add) return Preserved("TickRiver was removed");
            var updated = AddToml(original, executable)!;
            var change = UpdateToml(updated, executable, arguments, null, null, true);
            return change with { Text = change.Text ?? updated, Status = "TickRiver connected. Restart the client if needed." };
        }
        if (servers["tickriver"] is not TomlTable entry) throw new ArgumentException("TickRiver configuration must be a TOML table.");
        if (entry.TryGetValue("enabled", out var enabled) && enabled is not bool || entry.TryGetValue("disabled", out var disabled) && disabled is not bool)
            throw new ArgumentException("TickRiver enabled/disabled settings must be booleans.");
        if (enabled is false || disabled is true) return Preserved("TickRiver is disabled");
        if (entry.ContainsKey("url")) return Preserved("Custom TickRiver transport");
        var command = entry.TryGetValue("command", out var cmd) ? cmd as string : null;
        if (!SamePath(command, executable) && (ownedCommand == null || !SamePath(command, ownedCommand))) return Preserved("Custom TickRiver executable");
        string[]? current = entry.TryGetValue("args", out var args) ? args is TomlArray array && array.All(v => v is string) ? array.Cast<string>().ToArray() : null : null;
        var managed = ManagedArguments(current, arguments, ownedArguments);
        var replacements = new List<(int Offset, int Length, string Value)>();
        var syntax = SyntaxParser.ParseStrict(original);
        void Replace(string key, object replacement)
        {
            if (!metadata.TryGetProperties(entry, out var properties) || properties == null || !properties.TryGetProperty(key, out var property) || property == null)
                throw new ArgumentException($"TickRiver {key} must be present to update this TOML entry. Config was left untouched.");
            var node = FindValue(syntax, property.Span.Offset) ?? throw new ArgumentException("Cannot locate the TickRiver setting safely.");
            var encoded = TomlSerializer.Serialize(new TomlTable { [key] = replacement });
            var encodedValue = SyntaxParser.ParseStrict(encoded).KeyValues.Single().Value!;
            replacements.Add((node.Span.Offset, node.Span.Length, encoded.Substring(encodedValue.Span.Offset, encodedValue.Span.Length)));
        }
        if (!SamePath(command, executable)) Replace("command", executable);
        if (managed && !current!.SequenceEqual(arguments)) { var encodedArgs = new TomlArray(); foreach (var argument in arguments) encodedArgs.Add(argument); Replace("args", encodedArgs); }
        var text = original;
        foreach (var replacement in replacements.OrderByDescending(r => r.Offset)) text = text.Remove(replacement.Offset, replacement.Length).Insert(replacement.Offset, replacement.Value);
        if (replacements.Count > 0) TomlSerializer.Deserialize<TomlTable>(text);
        return new(replacements.Count > 0 ? text : null, executable, managed ? arguments : null,
            managed ? replacements.Count > 0 ? "TickRiver connected; bundled executable and database updated. Restart the client if needed." : "Bundled executable and database verified." : "Bundled executable verified; custom arguments preserved.");
    }

    private static ValueSyntax? FindValue(SyntaxNode node, int offset)
    {
        if (node is KeyValueSyntax pair && pair.Key != null
            && (pair.Key.Key?.Span.Offset == offset || pair.Key.DotKeys.Any(part => part.Key?.Span.Offset == offset))) return pair.Value;
        for (var i = 0; i < node.ChildrenCount; i++) { var child = node.GetChild(i); if (child != null && FindValue(child, offset) is { } value) return value; }
        return null;
    }

    public static string? AddToml(string original, string executable)
    {
        var parsed = TomlSerializer.Deserialize<TomlTable>(original) ?? new TomlTable();
        if (parsed.TryGetValue("mcp_servers", out var value))
        {
            if (value is not TomlTable servers) throw new ArgumentException("mcp_servers is not a TOML table. Existing config was left untouched.");
            if (servers.ContainsKey("tickriver")) return null;
            // Inline tables cannot be extended with a new table declaration in TOML.
            if (original.Split('\n').Any(line => line.TrimStart().StartsWith("mcp_servers", StringComparison.Ordinal) && line.Contains('=')))
                throw new ArgumentException("The MCP table uses inline syntax. Add TickRiver with the client's MCP command.");
        }
        var addition = TomlSerializer.Serialize(new TomlTable {
            ["mcp_servers"] = new TomlTable { ["tickriver"] = new TomlTable { ["command"] = executable, ["args"] = new TomlArray() } } });
        var updated = original.TrimEnd('\r', '\n') + Environment.NewLine + Environment.NewLine + "# TickRiver local time reports and timer control" + Environment.NewLine + addition;
        TomlSerializer.Deserialize<TomlTable>(updated); // Validate before changing any file.
        return updated;
    }

    public static string? AddJson(string original, string executable, bool claudeCode)
    {
        using var document = JsonDocument.Parse(original);
        // Duplicate keys can be interpreted differently by different clients; leave them untouched.
        RejectDuplicateKeys(document.RootElement);
        var root = JsonNode.Parse(original) as JsonObject ?? throw new ArgumentException("Client config must be a JSON object.");
        JsonObject servers;
        if (root.ContainsKey("mcpServers")) servers = root["mcpServers"] as JsonObject ?? throw new ArgumentException("mcpServers must be a JSON object. Config was left untouched.");
        else { servers = new JsonObject(); root["mcpServers"] = servers; }
        if (servers.ContainsKey("tickriver")) return null;
        var entry = new JsonObject { ["command"] = executable, ["args"] = new JsonArray() };
        if (claudeCode) entry["type"] = "stdio";
        servers["tickriver"] = entry;
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!keys.Add(property.Name)) throw new ArgumentException("Duplicate JSON keys found. Config was left untouched."); RejectDuplicateKeys(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) RejectDuplicateKeys(value);
    }
    private static void WriteConfig(string path, string? original, string updated, bool backup = true)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tickriver-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            var current = File.Exists(path) ? File.ReadAllText(path) : null;
            if (current != original) throw new IOException("Client config changed during setup. Try again when the client is idle.");
            if (backup && original != null) File.Copy(path, path + ".tickriver-backup-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
