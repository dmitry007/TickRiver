using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomlyn;
using Tomlyn.Model;

namespace TickRiver.Core;

public static class ClientRegistration
{
    // Never rewrite an existing server, including disabled or customized entries.
    // Remember successful first-run attempts so removing a server remains authoritative.
    public static IReadOnlyList<string> Register(string executable, string home, string statePath, string? codexHome = null, bool retry = false)
    {
        if (!File.Exists(executable)) return ["The bundled MCP server was not found. Use a published TickRiver build."];
        var state = File.Exists(statePath) ? JsonNode.Parse(File.ReadAllText(statePath))?.AsObject() ?? new JsonObject() : new JsonObject();
        var results = new List<string>();
        var codex = codexHome ?? System.IO.Path.Combine(home, ".codex");
        var clients = new[] {
            (Name: "Codex", Folder: codex, Config: System.IO.Path.Combine(codex, "config.toml"), Toml: true),
            (Name: "Claude Code", Folder: System.IO.Path.Combine(home, ".claude"), Config: System.IO.Path.Combine(home, ".claude.json"), Toml: false),
            (Name: "Kiro", Folder: System.IO.Path.Combine(home, ".kiro"), Config: System.IO.Path.Combine(home, ".kiro", "settings", "mcp.json"), Toml: false),
            (Name: "Grok Build", Folder: System.IO.Path.Combine(home, ".grok"), Config: System.IO.Path.Combine(home, ".grok", "config.toml"), Toml: true) };
        foreach (var client in clients)
        {
            if (!Directory.Exists(client.Folder) && !File.Exists(client.Config)) { results.Add($"{client.Name}: not detected."); continue; }
            if (!retry && state.ContainsKey(client.Name)) { results.Add($"{client.Name}: first-run setup already handled; existing user choices preserved."); continue; }
            try
            {
                var original = File.Exists(client.Config) ? File.ReadAllText(client.Config) : null;
                var update = client.Toml ? AddToml(original ?? "", executable) : AddJson(original ?? "{}", executable, client.Name == "Claude Code");
                if (update != null) WriteConfig(client.Config, original, update);
                state[client.Name] = DateTimeOffset.UtcNow.ToString("O");
                results.Add($"{client.Name}: " + (update == null ? "existing TickRiver entry preserved." : "TickRiver connected. Restart the client if needed."));
            }
            catch (Exception error) { results.Add($"{client.Name}: could not connect — {error.Message}"); }
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(statePath)!);
        var temporary = statePath + ".tmp";
        File.WriteAllText(temporary, state.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
        File.Move(temporary, statePath, true);
        return results;
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
    private static void WriteConfig(string path, string? original, string updated)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tickriver-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, updated, new UTF8Encoding(false));
            var current = File.Exists(path) ? File.ReadAllText(path) : null;
            if (current != original) throw new IOException("Client config changed during setup. Try again when the client is idle.");
            if (original != null) File.Copy(path, path + ".tickriver-backup-" + DateTime.Now.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N"));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
