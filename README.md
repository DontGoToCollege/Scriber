# Scriber

A notepad for outlines that other people can review. Write in indented rows, and anyone can attach comments to a row,
reply, resolve them and see how each comment was edited. One `Scriber.exe` with no install and no
internet.

## Download

Download `Scriber.exe` from the [latest release](https://github.com/DontGoToCollege/Scriber/releases/latest). It's a single program: no installer, just run it.

## Use it

- **Write**: each line is a row. `Enter` adds a row, `Tab` and `Shift+Tab` indent and outdent. Rows can be bullets or
  numbered, folded, dragged to a new place, and selected several at a time.
- **Format**: bold, italic, underline, font, size and text color, for the selected text or for whole rows. Rows can be
  tinted by level (Rainbow or Neon, with adjustable strength), and row numbers can be shown.
- **Paste** a list from Word, a web page or plain text and it becomes rows with the same indents.
- **Review**: double-click a row (or press the comment button) to comment on it. Open the Review panel with `Esc` to
  see every thread, reply, resolve or reopen, edit, and look at a comment's history. Filter by open or resolved and
  sort by row or date.
- **Undo and redo** with `Ctrl+Z` and `Ctrl+Y`.

Your outline is saved by itself as you type, in a `Scriber-Data` folder beside the exe. Carry the exe
and that folder together (for example on a USB stick) to take the outline with you. If that folder can't be written,
`%LOCALAPPDATA%\Scriber` is used instead.

### Sharing for review

- **Save HTML** saves the outline and all its comments as one web page. Send it to anyone: they open it in their
  browser, add comments, press Save HTML themselves and send it back. No program needed on their side.
- **Open file** (↥) opens one of those pages, or a backup. It replaces the outline on screen, so save a backup first if
  you still need the current one.
- **Save backup** (◫) saves a small `.notepack` file with everything in it.

### Shortcuts

| Keys | What it does |
|---|---|
| Click-drag, `Ctrl`+click, `Shift`+click | Select rows |
| `Ctrl+Shift+A` | Select all rows |
| `Alt`+click | Fold or unfold every row at that level |
| `Alt+Shift`+click | Fold or unfold everything |
| `Alt+←` / `Alt+→` | Outdent / indent |
| `Alt+Enter` | New row one level up |
| `Alt+C` then `1` | Black text |
| `Alt+C` then `2` | Last used text color |
| Double-click | Comment on the row |
| `Delete` | Remove the row and everything under it |
| `Backspace` (on an empty row or several rows) | Remove the row but keep what is under it |
| `Esc` | Show or hide Review |

Needs the Microsoft Edge **WebView2 runtime**, which is built into Windows 11 and already on most Windows 10 PCs. If
it's missing, the app says so instead of loading.

## History

Scriber was called Outline Review Notepad until 2.1.0. It began as a single HTML file opened in a browser (up to version 1.5.7, still on the
[releases page](https://github.com/DontGoToCollege/Scriber/releases)). From 2.0.0 it is a desktop
program. Outlines saved from the old versions open in the new one. See [CHANGELOG.md](CHANGELOG.md).

## Build

```powershell
.\publish.ps1          # -> Scriber.exe (self-contained .NET 8 WPF + WebView2, about 63 MB)
.\publish.ps1 -Run
```

Needs the .NET 8 SDK. The script refuses to rebuild a version that is already in `Versions\Archive`, and only keeps a
build that passes its own self-check (`Scriber.exe --smoke`, which works in a scratch profile and never
touches your outline).

- `web\index.html`: the whole app in one file (plain HTML, CSS and JavaScript, no dependencies). It has to stay one
  file, because Save HTML copies the page itself.
- `src\Scriber\`: the WPF shell. It serves `web\` from inside the exe on a private address and refuses
  every other request, so the app cannot reach the network. `AppInfo.cs` holds the app's name and colors; `smoke.js`
  is the self-check.
- `tools\make-icon.ps1`: redraws `src\Scriber\app.ico`.
- `--devtools` turns on the web inspector and right-click menu.

## License

MIT: free for anyone to use, change and share. See [LICENSE](LICENSE).
