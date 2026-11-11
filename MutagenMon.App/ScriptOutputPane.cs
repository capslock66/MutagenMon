using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MutagenMon.Core.Scripts;

namespace MutagenMon.App;

/// <summary>
/// Appends the output lines of a script run to a read-only
/// <see cref="RichTextBox"/> (FR-52.1, FR-52.4): stderr in red, only the last
/// <see cref="MaxLines"/> lines kept so a chatty script cannot exhaust memory.
/// Shared by the Scripts tab and the run dialog of the Sync tab (FR-56.5). UI
/// thread only.
/// </summary>
public static class ScriptOutputPane
{
    public const int MaxLines = 5000;

    public static void Append(RichTextBox box, ScriptOutputLine line)
    {
        var paragraph = new Paragraph(new Run(line.Text)) { Margin = new Thickness(0) };
        if (line.IsError)
            paragraph.Foreground = Brushes.Red;

        var blocks = box.Document.Blocks;
        blocks.Add(paragraph);
        while (blocks.Count > MaxLines)
            blocks.Remove(blocks.FirstBlock);

        box.ScrollToEnd();
    }
}
