namespace Scriber;

/// <summary>Everything about this app that the shell needs to know. The rest of the shell is generic.</summary>
static class AppInfo
{
    public const string Name = "Scriber";
    /// <summary>Data folder beside the exe.</summary>
    public const string DataFolder = "Scriber-Data";
    /// <summary>Folder under %LOCALAPPDATA%, used only when the exe's own folder is read-only.</summary>
    public const string FallbackFolder = "Scriber";
    /// <summary>Folder names from before the rename to Scriber (2.1.0); moved to the new names on first run.</summary>
    public const string OldDataFolder = "OutlineReviewNotepad-Data", OldFallbackFolder = "OutlineReviewNotepad";
    /// <summary>Private virtual host the embedded web app is served from. Nothing ever leaves the machine.
    /// Kept from the old name on purpose: the saved outline lives in this origin's storage.</summary>
    public const string Host = "app.outlinereviewnotepad.portable";
    public const string Background = "#f4f7fb";
    public const string Text = "#172033";
}
