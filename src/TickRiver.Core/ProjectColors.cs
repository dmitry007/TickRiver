using System.Text.Json;

namespace TickRiver.Core;

public sealed record ProjectColor(string Name, string Hex);

public sealed class ProjectColors
{
    private readonly string _path;
    private Dictionary<long, string> _overrides = new();
    public static IReadOnlyList<ProjectColor> Palette { get; } = Array.AsReadOnly<ProjectColor>([
        new("Sage", "#71947D"), new("Dusty blue", "#7196B5"), new("Muted teal", "#538F91"),
        new("Lavender", "#9683AE"), new("Terracotta", "#B77E6A"), new("Ochre", "#AF9757"),
        new("Rose", "#B07F91"), new("Slate", "#7A8D9F"), new("Olive", "#8B9664"),
        new("Periwinkle", "#7F89B3"), new("Clay", "#AA8C73"), new("Plum", "#9B7895")
    ]);

    public ProjectColors(string databasePath) => _path = System.IO.Path.GetFullPath(databasePath) + ".project-colors.json";

    public void Load()
    {
        var saved = File.Exists(_path) ? JsonSerializer.Deserialize<Dictionary<long, string?>>(File.ReadAllText(_path)) : null;
        _overrides = (saved ?? new Dictionary<long, string?>()).Where(p => p.Key > 0 && Palette.Any(c => string.Equals(c.Hex, p.Value, StringComparison.OrdinalIgnoreCase)))
            .ToDictionary(p => p.Key, p => Palette.First(c => string.Equals(c.Hex, p.Value, StringComparison.OrdinalIgnoreCase)).Hex);
    }

    public ProjectColor Get(long projectId)
    {
        if (_overrides.TryGetValue(projectId, out var hex)) return Palette.First(c => c.Hex == hex);
        return Default(projectId);
    }

    public static ProjectColor Default(long projectId) => Palette[projectId > 0 ? (int)((projectId - 1) % Palette.Count) : 0];

    public bool IsAutomatic(long projectId) => !_overrides.ContainsKey(projectId);

    public void Save(long projectId, string? hex)
    {
        if (projectId <= 0) throw new ArgumentException("Select a project before choosing its color.");
        var color = hex == null ? null : Palette.FirstOrDefault(c => string.Equals(c.Hex, hex, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Choose a color from the project palette.");
        if (color == null ? !_overrides.ContainsKey(projectId) : _overrides.TryGetValue(projectId, out var current) && current == color.Hex) return;
        var updated = new Dictionary<long, string>(_overrides);
        if (color == null) updated.Remove(projectId); else updated[projectId] = color.Hex;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(updated));
            File.Move(temporary, _path, true);
            _overrides = updated;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
