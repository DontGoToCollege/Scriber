using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Win32;

namespace Scriber;

/// <summary>Read-only copies for people without Scriber: plain text, and printing (Microsoft Print to PDF makes a PDF).</summary>
public partial class MainWindow
{
    List<(Block b, int number)> NumberedRows()
    {
        var counts = new int[22];
        return _doc.Blocks.Select(b =>
        {
            for (int k = b.Level + 1; k < counts.Length; k++) counts[k] = 0;
            if (b.Type == "number") counts[b.Level]++;
            return (b, Math.Max(1, counts[b.Level]));
        }).ToList();
    }

    static string Marker(Block b, int number) => b.Type == "bullet" ? "• " : b.Type == "number" ? number + ". " : "";

    /// <summary>Comment threads grouped by row, in row order (threads on deleted rows last).</summary>
    IEnumerable<IGrouping<string, Comment>> ThreadsByRow() =>
        _doc.Comments.OrderBy(c => c.CreatedAt).GroupBy(c => c.BlockId)
            .OrderBy(g => Index(g.Key) < 0 ? int.MaxValue : Index(g.Key));

    string ExportText()
    {
        var sb = new StringBuilder();
        sb.AppendLine(_doc.Title).AppendLine();
        foreach (var (b, n) in NumberedRows())
        {
            var prefix = new string(' ', b.Level * 4) + Marker(b, n);
            sb.AppendLine(prefix + b.Text.Replace("\n", "\n" + new string(' ', prefix.Length)));
        }
        if (_doc.Comments.Count > 0)
        {
            sb.AppendLine().AppendLine("Comments").AppendLine("--------");
            foreach (var g in ThreadsByRow())
            {
                sb.AppendLine().AppendLine("Row " + RowPreview(g.Key).Replace(" | ", ": "));
                foreach (var c in g)
                {
                    sb.AppendLine($"  [{(c.Resolved ? "Resolved" : "Open")}] {c.Author} ({c.ModifiedAt.ToLocalTime():g}): {Indent(c.Text, 4)}");
                    foreach (var r in c.Replies) sb.AppendLine($"      {r.Author} ({r.ModifiedAt.ToLocalTime():g}): {Indent(r.Text, 8)}");
                }
            }
        }
        return sb.ToString();

        static string Indent(string s, int n) => s.Replace("\n", "\n" + new string(' ', n));
    }

    void ExportTextDialog()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Export as text", Filter = "Text file (*.txt)|*.txt", DefaultExt = ".txt", AddExtension = true,
            FileName = SafeName(_doc.Title) + ".txt",
        };
        if (_path != null) dlg.InitialDirectory = Path.GetDirectoryName(_path);
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, ExportText(), Encoding.UTF8);
            Toast("Exported " + Path.GetFileName(dlg.FileName));
        }
        catch (Exception ex)
        {
            Ask($"Could not export:\n\n{ex.Message}", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    FlowDocument PrintDocument()
    {
        var fd = new FlowDocument { FontFamily = new FontFamily(RowFmt.DefaultFont), FontSize = 13, Foreground = Br(RowFmt.Ink) };
        fd.Blocks.Add(new Paragraph(new Run(_doc.Title)) { FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        foreach (var (b, n) in NumberedRows())
        {
            var st = RowFmt.Of(b);
            var p = new Paragraph
            {
                Margin = new Thickness(b.Level * 24, 2, 0, 2), FontFamily = new FontFamily(st.Font), FontSize = st.Size,
                FontWeight = st.Bold ? FontWeights.Bold : FontWeights.Normal, FontStyle = st.Italic ? FontStyles.Italic : FontStyles.Normal,
                Foreground = Br(st.Color),
            };
            var marker = Marker(b, n);
            if (marker.Length > 0) p.Inlines.Add(new Run(marker) { Foreground = Br("#64748B") });
            p.Inlines.AddRange(Inlines(b, st));
            fd.Blocks.Add(p);
        }
        if (_doc.Comments.Count == 0) return fd;
        fd.Blocks.Add(new Paragraph(new Run("Comments")) { FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 24, 0, 6) });
        foreach (var g in ThreadsByRow())
        {
            fd.Blocks.Add(new Paragraph(new Run("Row " + RowPreview(g.Key).Replace(" | ", ": "))) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 10, 0, 2) });
            foreach (var c in g)
            {
                fd.Blocks.Add(MessageParagraph(c, 12, c.Resolved ? "Resolved" : "Open"));
                foreach (var r in c.Replies) fd.Blocks.Add(MessageParagraph(r, 36, null));
            }
        }
        return fd;

        static Paragraph MessageParagraph(Message m, double indent, string? status)
        {
            var p = new Paragraph { Margin = new Thickness(indent, 2, 0, 2) };
            if (status != null) p.Inlines.Add(new Run($"[{status}] ") { Foreground = Br("#64748B") });
            p.Inlines.Add(new Run(m.Author) { FontWeight = FontWeights.SemiBold });
            p.Inlines.Add(new Run($"  {m.ModifiedAt.ToLocalTime():g}") { Foreground = Br("#64748B"), FontSize = 11 });
            p.Inlines.Add(new LineBreak());
            p.Inlines.Add(new Run(m.Text));
            return p;
        }
    }

    void PrintOutline()
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;
        var fd = PrintDocument();
        fd.PageWidth = dlg.PrintableAreaWidth;
        fd.PageHeight = dlg.PrintableAreaHeight;
        fd.ColumnWidth = fd.PageWidth;
        fd.PagePadding = new Thickness(56);
        dlg.PrintDocument(((IDocumentPaginatorSource)fd).DocumentPaginator, _doc.Title);
    }
}
