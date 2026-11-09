using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Write.Core;
using Write.Desktop.Editing;

namespace Write.Desktop;

public partial class MainWindow
{
    // ---- clipboard and history ---------------------------------------------------------------

    private void Undo_Click(object sender, RoutedEventArgs e) { Editor.Undo(); Editor.Focus(); }
    private void Redo_Click(object sender, RoutedEventArgs e) { Editor.Redo(); Editor.Focus(); }
    private void Cut_Click(object sender, RoutedEventArgs e) { Editor.Cut(); Editor.Focus(); }
    private void Copy_Click(object sender, RoutedEventArgs e) { Editor.Copy(); Editor.Focus(); }
    private void Paste_Click(object sender, RoutedEventArgs e) { Editor.Paste(); Editor.Focus(); }
    private void SelectAll_Click(object sender, RoutedEventArgs e) { Editor.SelectAll(); Editor.Focus(); }

    private void PastePlain_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!Clipboard.ContainsText()) return;
            Editor.Selection.Text = Clipboard.GetText();
            Editor.CaretPosition = Editor.Selection.End;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // The clipboard is in use by another program; nothing to paste right now.
        }
        Editor.Focus();
    }

    // ---- context menu ------------------------------------------------------------------------

    private readonly List<object> _spellingItems = new();

    private void EditorMenu_Opened(object sender, RoutedEventArgs e)
    {
        foreach (var item in _spellingItems) EditorMenu.Items.Remove(item);
        _spellingItems.Clear();

        var point = Mouse.GetPosition(Editor);
        var position = Editor.GetPositionFromPoint(point, true) ?? Editor.CaretPosition;
        var error = Editor.SpellCheck.IsEnabled ? Editor.GetSpellingError(position) : null;
        if (error != null)
        {
            int index = 0;
            foreach (var suggestion in error.Suggestions.Take(6))
            {
                var item = new MenuItem { Header = suggestion.Replace("_", "__"), FontWeight = FontWeights.SemiBold };
                item.Click += (_, _) => error.Correct(suggestion);
                EditorMenu.Items.Insert(index++, item);
                _spellingItems.Add(item);
            }
            if (index == 0)
            {
                var none = new MenuItem { Header = "(geen suggesties)", IsEnabled = false };
                EditorMenu.Items.Insert(index++, none);
                _spellingItems.Add(none);
            }
            var ignore = new MenuItem { Header = "Alles negeren" };
            ignore.Click += (_, _) => error.IgnoreAll();
            EditorMenu.Items.Insert(index++, ignore);
            _spellingItems.Add(ignore);
            var separator = new Separator();
            EditorMenu.Items.Insert(index, separator);
            _spellingItems.Add(separator);
        }

        bool inTable = CurrentCell() != null;
        var tableVisibility = inTable ? Visibility.Visible : Visibility.Collapsed;
        TableSeparator.Visibility = tableVisibility;
        CtxRowBelow.Visibility = tableVisibility;
        CtxColRight.Visibility = tableVisibility;
        CtxDelRow.Visibility = tableVisibility;
        CtxDelCol.Visibility = tableVisibility;
        RemoveLinkItem.Visibility = CurrentHyperlink() != null ? Visibility.Visible : Visibility.Collapsed;
        ImageSizeItem.Visibility = SelectedImage() != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- hyperlinks --------------------------------------------------------------------------

    private Hyperlink? CurrentHyperlink() => FlowWalker.Ancestor<Hyperlink>(Editor.Selection.Start.Parent);

    private void InsertLink_Click(object sender, RoutedEventArgs e)
    {
        var existing = CurrentHyperlink();
        var selection = Editor.Selection;
        string text = existing != null ? new TextRange(existing.ContentStart, existing.ContentEnd).Text : selection.Text;
        string url = existing?.Tag as string ?? existing?.NavigateUri?.OriginalString ?? "";

        var dialog = new FormDialog(this, "Koppeling");
        int textField = dialog.AddText("Weergegeven tekst", text.Replace("\r", "").Replace("\n", " "));
        int urlField = dialog.AddText("Adres", url.Length > 0 ? url : "https://");
        if (!dialog.Run()) return;
        text = dialog.Values[textField].Trim();
        url = dialog.Values[urlField].Trim();
        if (url.Length == 0 || url == "https://") return;
        if (!url.Contains(':') && !url.StartsWith('#')) url = "https://" + url;
        if (text.Length == 0) text = url;

        var styleId = _doc.Styles.EnsureBuiltIn("Hyperlink", "character");
        var color = FlowFormat.Brush(_doc.Styles.ResolveRun(null, styleId).Color);

        Editor.BeginChange();
        try
        {
            Hyperlink link;
            if (existing != null)
            {
                link = existing;
                if (new TextRange(link.ContentStart, link.ContentEnd).Text != text)
                {
                    link.Inlines.Clear();
                    link.Inlines.Add(new Run(text));
                }
            }
            else
            {
                if (!selection.IsEmpty) selection.Text = "";
                var position = Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
                link = new Hyperlink(new Run(text), position);
            }
            link.Tag = url;
            link.ToolTip = url + "\nCtrl+klik om te openen";
            link.NavigateUri = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri : null;
            link.TextDecorations = null;
            foreach (var run in FlowWalker.Runs(link.Inlines))
            {
                run.Foreground = color;
                run.TextDecorations = FlowFormat.Decorations(true, false);
            }
            Editor.CaretPosition = link.ElementEnd;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            MessageBox.Show(this, "Op deze plaats kan geen koppeling worden ingevoegd.", "Write", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
    }

    private void RemoveLink_Click(object sender, RoutedEventArgs e)
    {
        var link = CurrentHyperlink();
        if (link == null) return;
        InlineCollection? siblings = link.Parent switch
        {
            Paragraph p => p.Inlines,
            Span s => s.Inlines,
            _ => null,
        };
        if (siblings == null) return;
        Editor.BeginChange();
        try
        {
            foreach (var child in link.Inlines.ToList())
            {
                link.Inlines.Remove(child);
                if (child is Run run)
                {
                    run.ClearValue(TextElement.ForegroundProperty);
                    run.ClearValue(System.Windows.Documents.Inline.TextDecorationsProperty);
                    run.Tag = null;
                }
                siblings.InsertBefore(link, child);
            }
            siblings.Remove(link);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
    }

    // ---- images ------------------------------------------------------------------------------

    private void InsertImage_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Afbeelding invoegen",
            Filter = "Afbeeldingen (*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff)|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.tif;*.tiff",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            InsertImage(File.ReadAllBytes(dialog.FileName), Path.GetExtension(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            MessageBox.Show(this, $"De afbeelding kan niet worden ingevoegd.\n\n{ex.Message}", "Write", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal bool InsertImage(byte[] data, string extension)
    {
        var source = FlowBuilder.LoadBitmap(data);
        if (source == null)
        {
            MessageBox.Show(this, "Dit bestand is geen afbeelding die Write kan lezen.", "Write", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        var ext = extension.TrimStart('.').ToLowerInvariant();
        if (ext is not ("png" or "jpg" or "jpeg" or "gif" or "bmp" or "tif" or "tiff")) ext = "png";

        // Natural size from the pixel dimensions and resolution, limited to the text width.
        double dpiX = source.DpiX > 1 ? source.DpiX : 96, dpiY = source.DpiY > 1 ? source.DpiY : 96;
        double width = source.PixelWidth * 96.0 / dpiX, height = source.PixelHeight * 96.0 / dpiY;
        double max = FlowFormat.Dip(_page.ContentWidth);
        if (width > max)
        {
            height *= max / width;
            width = max;
        }

        var image = new ImageInline { Data = data, Extension = ext, Cx = FlowFormat.DipToEmu(width), Cy = FlowFormat.DipToEmu(height) };
        var element = FlowBuilder.NewImageElement(image, source, out var tag);
        Editor.BeginChange();
        try
        {
            if (!Editor.Selection.IsEmpty) Editor.Selection.Text = "";
            var position = Editor.CaretPosition.GetInsertionPosition(LogicalDirection.Forward);
            var container = new InlineUIContainer(element, position) { BaselineAlignment = BaselineAlignment.Bottom, Tag = tag };
            Editor.CaretPosition = container.ElementEnd;
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
        return true;
    }

    private Image? SelectedImage()
    {
        var selection = Editor.Selection;
        foreach (var direction in new[] { LogicalDirection.Forward, LogicalDirection.Backward })
        {
            var pointer = direction == LogicalDirection.Forward ? selection.Start : selection.End;
            if (pointer.GetAdjacentElement(direction) is InlineUIContainer { Child: Image image }) return image;
        }
        if (selection.Start.Parent is InlineUIContainer { Child: Image inside }) return inside;
        // An image somewhere inside the selection.
        for (var p = selection.Start; p != null && p.CompareTo(selection.End) < 0; p = p.GetNextContextPosition(LogicalDirection.Forward))
            if (p.GetAdjacentElement(LogicalDirection.Forward) is InlineUIContainer { Child: Image found }) return found;
        return null;
    }

    private void ImageSize_Click(object sender, RoutedEventArgs e)
    {
        var image = SelectedImage();
        if (image == null)
        {
            MessageBox.Show(this, "Selecteer eerst een afbeelding.", "Afbeeldingsgrootte", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        double width = image.Width, height = image.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0) return;
        var dialog = new FormDialog(this, "Afbeeldingsgrootte", "De verhouding tussen breedte en hoogte blijft behouden.");
        int field = dialog.AddText("Breedte (cm)", $"{width / 96 * 2.54:0.##}");
        if (!dialog.Run()) return;
        if (!FormDialog.TryNumber(dialog.Values[field], out double cm) || cm < 0.1 || cm > 100) return;
        double newWidth = cm / 2.54 * 96;
        Editor.BeginChange();
        image.Height = height * newWidth / width;
        image.Width = newWidth;
        Editor.EndChange();
        MarkDirty();
    }

    // ---- page break --------------------------------------------------------------------------

    private void PageBreak_Click(object sender, RoutedEventArgs e) => InsertPageBreak();

    internal void InsertPageBreak()
    {
        if (CurrentCell() != null) return; // a page cannot break inside a table cell
        Editor.BeginChange();
        try
        {
            if (!Editor.Selection.IsEmpty) Editor.Selection.Text = "";
            var position = Editor.CaretPosition.InsertParagraphBreak();
            Editor.CaretPosition = position;
        }
        finally
        {
            Editor.EndChange();
        }
        FixAfterParagraphSplit();
        var para = Editor.CaretPosition.Paragraph;
        if (para != null) FlowFormat.SetPageBreak(para, true);
        MarkDirty();
        Editor.Focus();
    }

    // ---- tables ------------------------------------------------------------------------------

    private TableCell? CurrentCell() => FlowWalker.Ancestor<TableCell>(Editor.Selection.Start.Parent);

    private static readonly Brush GridBrush = FlowFormat.Brush("000000");

    private TableCell NewCell(TableCell? like)
    {
        var tableStyle = _doc.Styles.Get("TableGrid")?.Id ?? _doc.Styles.EnsureBuiltIn("Table Grid", "table");
        var para = new Paragraph();
        FlowFormat.ApplyPara(para, _doc.Styles.ResolvePara(null, tableStyle), _doc.Styles.ResolveRun(null, null, tableStyle), inList: false);
        var cell = new TableCell(para)
        {
            Padding = like?.Padding ?? FlowBuilder.CellPadding,
            BorderBrush = like?.BorderBrush ?? GridBrush,
            BorderThickness = like?.BorderThickness ?? new Thickness(1),
        };
        if (like?.Background != null) cell.Background = like.Background;
        return cell;
    }

    /// <summary>Grid position (row, column) of every cell, taking spans into account.</summary>
    private static List<(TableCell Cell, int Row, int Col)> Layout(Table table, out int rows, out int columns)
    {
        var result = new List<(TableCell, int, int)>();
        var occupied = new Dictionary<(int, int), bool>();
        int r = 0;
        columns = 0;
        foreach (var group in table.RowGroups)
        {
            foreach (var row in group.Rows)
            {
                int c = 0;
                foreach (var cell in row.Cells)
                {
                    while (occupied.ContainsKey((r, c))) c++;
                    result.Add((cell, r, c));
                    for (int dr = 0; dr < Math.Max(1, cell.RowSpan); dr++)
                    for (int dc = 0; dc < Math.Max(1, cell.ColumnSpan); dc++)
                        occupied[(r + dr, c + dc)] = true;
                    c += Math.Max(1, cell.ColumnSpan);
                    columns = Math.Max(columns, c);
                }
                r++;
            }
        }
        rows = r;
        return result;
    }

    /// <summary>
    /// Text elements draw every cell border separately, so inner edges are drawn by one cell only:
    /// each cell draws its top and left edge, and the last row and column also close the table.
    /// </summary>
    private static void NormalizeBorders(Table table)
    {
        var layout = Layout(table, out int rows, out int columns);
        foreach (var (cell, row, col) in layout)
        {
            var t = cell.BorderThickness;
            double line = Math.Max(Math.Max(t.Left, t.Top), Math.Max(t.Right, t.Bottom));
            if (line <= 0) continue;
            bool lastColumn = col + Math.Max(1, cell.ColumnSpan) >= columns;
            bool lastRow = row + Math.Max(1, cell.RowSpan) >= rows;
            cell.BorderThickness = new Thickness(line, line, lastColumn ? line : 0, lastRow ? line : 0);
        }
    }

    private void InsertTable_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new FormDialog(this, "Tabel invoegen");
        int rowsField = dialog.AddText("Aantal rijen", "3");
        int columnsField = dialog.AddText("Aantal kolommen", "3");
        if (!dialog.Run()) return;
        if (!int.TryParse(dialog.Values[rowsField].Trim(), out int rows) || !int.TryParse(dialog.Values[columnsField].Trim(), out int columns)) return;
        InsertTable(Math.Clamp(rows, 1, 200), Math.Clamp(columns, 1, 30));
    }

    internal void InsertTable(int rows, int columns)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0) };
        double available = FlowFormat.Dip(_page.ContentWidth);
        var cell = CurrentCell();
        if (cell != null && FlowWalker.Ancestor<Table>(cell) is { } outer)
        {
            // Nested table: fit inside the surrounding cell.
            var layout = Layout(outer, out _, out _);
            int col = layout.First(l => ReferenceEquals(l.Cell, cell)).Col;
            double cellWidth = 0;
            for (int i = col; i < col + cell.ColumnSpan && i < outer.Columns.Count; i++)
                cellWidth += outer.Columns[i].Width.IsAbsolute ? outer.Columns[i].Width.Value : 0;
            if (cellWidth > 40) available = cellWidth - 16;
        }
        double width = Math.Floor(available / columns * 15) / 15;
        for (int c = 0; c < columns; c++) table.Columns.Add(new TableColumn { Width = new GridLength(width) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        for (int r = 0; r < rows; r++)
        {
            var row = new TableRow();
            for (int c = 0; c < columns; c++) row.Cells.Add(NewCell(null));
            group.Rows.Add(row);
        }
        NormalizeBorders(table);

        var para = Editor.CaretPosition.Paragraph;
        Editor.BeginChange();
        try
        {
            if (para != null)
            {
                var siblings = para.SiblingBlocks;
                bool empty = new TextRange(para.ContentStart, para.ContentEnd).IsEmpty;
                if (empty && para.PreviousBlock is not Table && para.Parent is not ListItem)
                    siblings.InsertBefore(para, table);
                else
                {
                    siblings.InsertAfter(para, table);
                    if (table.NextBlock == null) siblings.InsertAfter(table, NewParagraphLike(para));
                }
            }
            else
            {
                Editor.Document.Blocks.Add(table);
                Editor.Document.Blocks.Add(NewParagraphLike(null));
            }
            var firstCell = group.Rows[0].Cells[0];
            Editor.CaretPosition = firstCell.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
        Editor.Focus();
    }

    private Paragraph NewParagraphLike(Paragraph? like)
    {
        var para = new Paragraph();
        var styleId = (like?.Tag as ParaTag)?.StyleId;
        if (styleId != null && _doc.Styles.HeadingLevel(styleId) != null) styleId = null;
        FlowFormat.ApplyPara(para, _doc.Styles.ResolvePara(styleId), _doc.Styles.ResolveRun(styleId), inList: false);
        para.Tag = new ParaTag
        {
            StyleId = styleId, StyleApplied = true, Precise = _doc.Styles.ResolvePara(styleId),
            Snapshot = FlowFormat.ReadPara(para, false, _doc.Styles.ResolvePara(null)),
        };
        return para;
    }

    private void RowAbove_Click(object sender, RoutedEventArgs e) => InsertRow(below: false);
    private void RowBelow_Click(object sender, RoutedEventArgs e) => InsertRow(below: true);
    private void ColumnLeft_Click(object sender, RoutedEventArgs e) => InsertColumn(right: false);
    private void ColumnRight_Click(object sender, RoutedEventArgs e) => InsertColumn(right: true);

    private bool TableContext(out TableCell cell, out TableRow row, out TableRowGroup group, out Table table)
    {
        cell = CurrentCell()!;
        row = (cell?.Parent as TableRow)!;
        group = (row?.Parent as TableRowGroup)!;
        table = (group?.Parent as Table)!;
        if (table != null) return true;
        MessageBox.Show(this, "Plaats de cursor eerst in een tabel.", "Tabel", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    internal void InsertRow(bool below)
    {
        if (!TableContext(out var cell, out var row, out var group, out var table)) return;
        Editor.BeginChange();
        try
        {
            var fresh = new TableRow();
            foreach (var existing in row.Cells)
            {
                var copy = NewCell(existing);
                copy.ColumnSpan = existing.ColumnSpan;
                fresh.Cells.Add(copy);
            }
            int index = group.Rows.IndexOf(row) + (below ? 1 : 0);
            group.Rows.Insert(index, fresh);
            NormalizeBorders(table);
            Editor.CaretPosition = fresh.Cells[Math.Min(row.Cells.IndexOf(cell), fresh.Cells.Count - 1)].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
    }

    internal void InsertColumn(bool right)
    {
        if (!TableContext(out var cell, out _, out _, out var table)) return;
        var layout = Layout(table, out _, out int columns);
        int at = layout.First(l => ReferenceEquals(l.Cell, cell)).Col;
        int target = right ? at + Math.Max(1, cell.ColumnSpan) : at;

        Editor.BeginChange();
        try
        {
            foreach (var group in table.RowGroups)
            {
                foreach (var row in group.Rows)
                {
                    // Insert before the first cell that starts at or after the target column.
                    int insertAt = row.Cells.Count;
                    TableCell? like = row.Cells.Count > 0 ? row.Cells[^1] : null;
                    for (int i = 0; i < row.Cells.Count; i++)
                    {
                        int col = layout.First(l => ReferenceEquals(l.Cell, row.Cells[i])).Col;
                        if (col >= target)
                        {
                            insertAt = i;
                            like = row.Cells[i];
                            break;
                        }
                    }
                    row.Cells.Insert(insertAt, NewCell(like));
                }
            }
            double width = at < table.Columns.Count && table.Columns[at].Width.IsAbsolute ? table.Columns[at].Width.Value : 96;
            if (table.Columns.Count >= columns)
                table.Columns.Insert(Math.Min(target, table.Columns.Count), new TableColumn { Width = new GridLength(width) });
            NormalizeBorders(table);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
    }

    private void DeleteRow_Click(object sender, RoutedEventArgs e)
    {
        if (!TableContext(out _, out var row, out var group, out var table)) return;
        if (table.RowGroups.Sum(g => g.Rows.Count) <= 1)
        {
            DeleteTable(table);
            return;
        }
        Editor.BeginChange();
        try
        {
            int index = group.Rows.IndexOf(row);
            group.Rows.Remove(row);
            NormalizeBorders(table);
            var next = group.Rows.Count > 0 ? group.Rows[Math.Min(index, group.Rows.Count - 1)] : null;
            if (next != null && next.Cells.Count > 0)
                Editor.CaretPosition = next.Cells[0].ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
    }

    private void DeleteColumn_Click(object sender, RoutedEventArgs e)
    {
        if (!TableContext(out var cell, out _, out _, out var table)) return;
        var layout = Layout(table, out _, out int columns);
        if (columns <= 1)
        {
            DeleteTable(table);
            return;
        }
        int at = layout.First(l => ReferenceEquals(l.Cell, cell)).Col;
        Editor.BeginChange();
        try
        {
            foreach (var (other, _, col) in layout)
            {
                int span = Math.Max(1, other.ColumnSpan);
                if (at < col || at >= col + span) continue;
                if (span > 1) other.ColumnSpan = span - 1;
                else (other.Parent as TableRow)?.Cells.Remove(other);
            }
            if (at < table.Columns.Count) table.Columns.RemoveAt(at);
            foreach (var group in table.RowGroups)
                foreach (var empty in group.Rows.Where(r => r.Cells.Count == 0).ToList())
                    group.Rows.Remove(empty);
            NormalizeBorders(table);
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
    }

    private void DeleteTable_Click(object sender, RoutedEventArgs e)
    {
        if (!TableContext(out _, out _, out _, out var table)) return;
        DeleteTable(table);
    }

    private void DeleteTable(Table table)
    {
        Editor.BeginChange();
        try
        {
            var siblings = table.SiblingBlocks;
            var neighbour = table.NextBlock ?? table.PreviousBlock;
            siblings.Remove(table);
            if (neighbour == null)
            {
                var para = NewParagraphLike(null);
                siblings.Add(para);
                neighbour = para;
            }
            Editor.CaretPosition = neighbour.ContentStart.GetInsertionPosition(LogicalDirection.Forward) ?? neighbour.ContentStart;
        }
        finally
        {
            Editor.EndChange();
        }
        MarkDirty();
    }
}
