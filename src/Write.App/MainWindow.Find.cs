using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Write.Desktop;

public partial class MainWindow
{
    /// <summary>The document text with, for every piece, where it sits in the document.</summary>
    private sealed class TextIndex
    {
        public readonly StringBuilder Text = new();
        public readonly List<(int Offset, TextPointer Start, int Length)> Pieces = new();

        public TextPointer? PointerAt(int index, bool isEnd)
        {
            // An end position belongs to the piece that holds the character before it.
            int lookup = isEnd ? index - 1 : index;
            foreach (var (offset, start, length) in Pieces)
                if (lookup >= offset && lookup < offset + length)
                    return start.GetPositionAtOffset(index - offset, LogicalDirection.Forward);
            return null;
        }

        public int IndexOf(TextPointer pointer)
        {
            foreach (var (offset, start, length) in Pieces)
            {
                var end = start.GetPositionAtOffset(length, LogicalDirection.Forward);
                if (end != null && pointer.CompareTo(end) <= 0)
                    return pointer.CompareTo(start) <= 0 ? offset : offset + start.GetOffsetToPosition(pointer);
            }
            return Text.Length;
        }
    }

    private TextIndex BuildTextIndex()
    {
        var index = new TextIndex();
        var pointer = Editor.Document.ContentStart;
        var end = Editor.Document.ContentEnd;
        while (pointer != null && pointer.CompareTo(end) < 0)
        {
            var context = pointer.GetPointerContext(LogicalDirection.Forward);
            if (context == TextPointerContext.Text)
            {
                var run = pointer.GetTextInRun(LogicalDirection.Forward);
                if (run.Length > 0)
                {
                    index.Pieces.Add((index.Text.Length, pointer, run.Length));
                    index.Text.Append(run);
                }
                pointer = pointer.GetPositionAtOffset(Math.Max(1, run.Length), LogicalDirection.Forward);
                continue;
            }
            // Paragraph ends, line breaks and embedded objects separate words.
            if (context == TextPointerContext.ElementEnd && pointer.Parent is Paragraph) index.Text.Append('\n');
            else if (context == TextPointerContext.ElementStart && pointer.GetAdjacentElement(LogicalDirection.Forward) is LineBreak or InlineUIContainer)
                index.Text.Append('\n');
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }
        return index;
    }

    private StringComparison FindComparison =>
        FindCase.IsChecked == true ? StringComparison.CurrentCulture : StringComparison.CurrentCultureIgnoreCase;

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        FindBar.Visibility = Visibility.Visible;
        var selected = Editor.Selection.Text;
        if (selected.Length > 0 && selected.Length < 200 && !selected.Contains('\n')) FindText.Text = selected;
        FindText.Focus();
        FindText.SelectAll();
    }

    internal void ShowFindBar(string text)
    {
        FindBar.Visibility = Visibility.Visible;
        FindText.Text = text;
    }

    internal void HideFindBar() => CloseFindBar();

    private void CloseFindBar()
    {
        FindBar.Visibility = Visibility.Collapsed;
        FindStatus.Text = "";
        Editor.Focus();
    }

    private void FindClose_Click(object sender, RoutedEventArgs e) => CloseFindBar();
    private void FindNext_Click(object sender, RoutedEventArgs e) => FindStep(forward: true);
    private void FindPrevious_Click(object sender, RoutedEventArgs e) => FindStep(forward: false);
    private void FindText_TextChanged(object sender, TextChangedEventArgs e) => FindStatus.Text = "";

    private void FindText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        FindStep(forward: (Keyboard.Modifiers & ModifierKeys.Shift) == 0);
    }

    private void ReplaceText_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        Replace_Click(sender, e);
    }

    /// <summary>Selects the next (or previous) occurrence, wrapping around the document.</summary>
    internal bool FindStep(bool forward)
    {
        var query = FindText.Text;
        if (query.Length == 0) return false;
        var index = BuildTextIndex();
        var text = index.Text.ToString();
        int found;
        if (forward)
        {
            int from = Math.Min(text.Length, index.IndexOf(Editor.Selection.End));
            found = text.IndexOf(query, from, FindComparison);
            if (found < 0) found = text.IndexOf(query, 0, FindComparison);
        }
        else
        {
            int before = index.IndexOf(Editor.Selection.Start) - 1;
            found = before >= 0 ? text.LastIndexOf(query, Math.Min(before + query.Length - 1, text.Length - 1), FindComparison) : -1;
            if (found >= 0 && found + query.Length > before + 1) found = found > 0 ? text.LastIndexOf(query, found - 1 + query.Length - 1, FindComparison) : -1;
            if (found < 0 && text.Length > 0) found = text.LastIndexOf(query, text.Length - 1, FindComparison);
        }

        if (found < 0)
        {
            FindStatus.Text = "Niet gevonden";
            return false;
        }
        var start = index.PointerAt(found, isEnd: false);
        var end = index.PointerAt(found + query.Length, isEnd: true);
        if (start == null || end == null) return false;
        Editor.Selection.Select(start, end);
        FindStatus.Text = "";
        var rect = start.GetCharacterRect(LogicalDirection.Forward);
        Editor.BringIntoView(new Rect(rect.X, Math.Max(0, rect.Y - 80), Math.Max(1, rect.Width), rect.Height + 160));
        return true;
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        var query = FindText.Text;
        if (query.Length == 0) return;
        if (string.Equals(Editor.Selection.Text, query, FindComparison))
        {
            Editor.Selection.Text = ReplaceText.Text;
            Editor.Selection.Select(Editor.Selection.End, Editor.Selection.End);
            MarkDirty();
        }
        FindStep(forward: true);
    }

    private void ReplaceAll_Click(object sender, RoutedEventArgs e) =>
        FindStatus.Text = ReplaceAll(FindText.Text, ReplaceText.Text) switch
        {
            0 => "Niet gevonden",
            1 => "1 keer vervangen",
            var n => $"{n} keer vervangen",
        };

    internal int ReplaceAll(string query, string replacement)
    {
        if (query.Length == 0) return 0;
        var index = BuildTextIndex();
        var text = index.Text.ToString();
        var matches = new List<int>();
        for (int at = text.IndexOf(query, FindComparison); at >= 0; at = text.IndexOf(query, at + query.Length, FindComparison))
            matches.Add(at);
        if (matches.Count == 0) return 0;

        // Resolve every position first, then replace from the end so earlier ones stay valid.
        var ranges = new List<TextRange>();
        foreach (int at in matches)
        {
            var start = index.PointerAt(at, isEnd: false);
            var end = index.PointerAt(at + query.Length, isEnd: true);
            if (start != null && end != null) ranges.Add(new TextRange(start, end));
        }
        Editor.BeginChange();
        try
        {
            for (int i = ranges.Count - 1; i >= 0; i--) ranges[i].Text = replacement;
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        return ranges.Count;
    }
}
