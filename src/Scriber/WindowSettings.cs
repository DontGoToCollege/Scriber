using System.IO;
using System.Text.Json;

namespace Scriber;

/// <summary>Window placement, stored in the portable data folder.</summary>
public sealed class WindowSettings
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public double Width { get; set; } = 1400;
    public double Height { get; set; } = 900;
    public bool Maximized { get; set; }

    // NaN (no saved position yet) is not valid JSON unless named literals are allowed
    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
    };

    public static WindowSettings Load()
    {
        try
        {
            if (File.Exists(PortablePaths.WindowFile))
                return JsonSerializer.Deserialize<WindowSettings>(File.ReadAllText(PortablePaths.WindowFile), Json) ?? new();
        }
        catch (Exception ex)
        {
            PortablePaths.Log("window settings unreadable: " + ex.Message);
        }
        return new();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(PortablePaths.WindowFile, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception ex)
        {
            PortablePaths.Log("window settings not saved: " + ex.Message);
        }
    }
}
