using System.IO;
using System.Text.Json;

namespace Scriber;

/// <summary>This PC's settings, in the portable data folder: window placement, the reviewer's name (never stored in
/// documents), recent files and the document to reopen.</summary>
public sealed class Settings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; }
    public string ReviewerName { get; set; } = "";
    public List<string> Recent { get; set; } = new();
    /// <summary>The file (or unsaved draft) that was open last.</summary>
    public string? LastDocument { get; set; }

    /// <summary>The self-check must not change this PC's settings.</summary>
    public static bool ReadOnly;

    // NaN (no saved position yet) is not valid JSON unless named literals are allowed
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static Settings Load()
    {
        foreach (var file in new[] { PortablePaths.SettingsFile, PortablePaths.WindowFile })
        {
            try
            {
                if (File.Exists(file)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(file), Json) ?? new();
            }
            catch (Exception ex)
            {
                PortablePaths.Log("settings unreadable: " + ex.Message);
            }
        }
        return new();
    }

    public void Save()
    {
        if (ReadOnly) return;
        try
        {
            File.WriteAllText(PortablePaths.SettingsFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            PortablePaths.Log("settings not saved: " + ex.Message);
        }
    }

    public void AddRecent(string path)
    {
        Recent.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        Recent.Insert(0, path);
        if (Recent.Count > 12) Recent.RemoveRange(12, Recent.Count - 12);
    }
}
