namespace Scriber;

/// <summary>Names and colors used across the app.</summary>
static class AppInfo
{
    public const string Name = "Scriber";
    /// <summary>The csproj's Version (the only place it is set).</summary>
    public static readonly string Version = typeof(AppInfo).Assembly.GetName().Version!.ToString(3);
    /// <summary>Data folder beside the exe.</summary>
    public const string DataFolder = "Scriber-Data";
    /// <summary>Folder under %LOCALAPPDATA%, used only when the exe's own folder is read-only.</summary>
    public const string FallbackFolder = "Scriber";
    /// <summary>Folder names from before the rename to Scriber; moved to the new names on first run.</summary>
    public const string OldDataFolder = "OutlineReviewNotepad-Data", OldFallbackFolder = "OutlineReviewNotepad";
    public const string FileExtension = ".scriber";
    public const string Background = "#f4f7fb";
    public const string Text = "#172033";
}
