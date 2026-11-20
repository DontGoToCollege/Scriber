using System.IO;

namespace Scriber;

/// <summary>
/// Portable data lives in a folder next to the exe, so copying the exe together with that folder
/// moves everything. Falls back to LocalAppData if the exe's folder is read-only (e.g. a
/// write-protected stick).
/// </summary>
public static class PortablePaths
{
    public static string ExeDir { get; } =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    public static string DataDir { get; } = ResolveDataDir();
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    /// <summary>2.x kept window placement here; read once if settings.json doesn't exist yet.</summary>
    public static string WindowFile => Path.Combine(DataDir, "window.json");
    /// <summary>Autosaves of new documents that haven't been saved as a file yet.</summary>
    public static string UnsavedDir => Path.Combine(DataDir, "Unsaved");
    public static string LogFile => Path.Combine(DataDir, "app.log");

    static string ResolveDataDir()
    {
        var portable = Adopt(Path.Combine(ExeDir, AppInfo.DataFolder), Path.Combine(ExeDir, AppInfo.OldDataFolder));
        if (CanWrite(portable)) return portable;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var fallback = Adopt(Path.Combine(local, AppInfo.FallbackFolder), Path.Combine(local, AppInfo.OldFallbackFolder));
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    /// <summary>Renames a data folder left by the old app name. If it can't be moved (say the old app is still
    /// open), keeps using it where it is, so the outline never looks lost.</summary>
    static string Adopt(string dir, string oldDir)
    {
        if (Directory.Exists(dir) || !Directory.Exists(oldDir)) return dir;
        try
        {
            Directory.Move(oldDir, dir);
            return dir;
        }
        catch
        {
            return oldDir;
        }
    }

    static bool CanWrite(string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void Log(string message)
    {
        try
        {
            var info = new FileInfo(LogFile);
            if (info.Exists && info.Length > 512 * 1024) info.Delete();
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never crash the app
        }
    }
}
