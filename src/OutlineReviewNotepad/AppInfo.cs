namespace OutlineReviewNotepad;

/// <summary>Everything about this app that the shell needs to know. The rest of the shell is generic.</summary>
static class AppInfo
{
    public const string Name = "Outline Review Notepad";
    /// <summary>Data folder beside the exe.</summary>
    public const string DataFolder = "OutlineReviewNotepad-Data";
    /// <summary>Folder under %LOCALAPPDATA%, used only when the exe's own folder is read-only.</summary>
    public const string FallbackFolder = "OutlineReviewNotepad";
    /// <summary>Private virtual host the embedded web app is served from. Nothing ever leaves the machine.</summary>
    public const string Host = "app.outlinereviewnotepad.portable";
    public const string Background = "#f4f7fb";
    public const string Text = "#172033";
}
