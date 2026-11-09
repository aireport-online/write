using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Write.Core;
using MBlock = Write.Core.Block;
using MInline = Write.Core.Inline;
using WInline = System.Windows.Documents.Inline;

namespace Write.Desktop.Editing;

/// <summary>Builds the editable WPF document from a <see cref="DocModel"/>.</summary>
internal sealed class FlowBuilder
{
    private readonly DocModel _doc;
    private readonly bool _print;

    public static readonly Thickness CellPadding = new(7.2, 1, 7.2, 1);

    public FlowBuilder(DocModel doc, bool forPrint = false)
    {
        _doc = doc;
        _print = forPrint;
    }

    public FlowDocument Build()
    {
        var flow = new FlowDocument();
        var def = _doc.Styles.ResolveRun(null);
        flow.FontFamily = FlowFormat.Font(def.Font);
        flow.FontSize = FlowFormat.SizeDip(def.Size);
        flow.Foreground = Brushes.Black;
        flow.TextAlignment = TextAlignment.Left;
        flow.IsHyphenationEnabled = false;
        flow.IsOptimalParagraphEnabled = false;
        ApplyPage(flow, _doc.Page, _print);
        AddBlocks(flow.Blocks, _doc.Blocks, null);
        if (flow.Blocks.Count == 0) flow.Blocks.Add(new Paragraph());
        return flow;
    }

    public static void ApplyPage(FlowDocument flow, PageSetup page, bool forPrint)
    {
        double width = FlowFormat.Dip(page.Width);
        flow.PageWidth = width;
        flow.PagePadding = new Thickness(FlowFormat.Dip(page.MarginLeft), FlowFormat.Dip(page.MarginTop),
            FlowFormat.Dip(page.MarginRight), FlowFormat.Dip(page.MarginBottom));
        flow.ColumnWidth = width;
        flow.ColumnGap = 0;
        if (forPrint) flow.PageHeight = FlowFormat.Dip(page.Height);
    }

    private string? ValidStyle(string? id) => id != null && _doc.Styles.Get(id) != null ? id : null;

    private void AddBlocks(BlockCollection target, IReadOnlyList<MBlock> blocks, string? tableStyle)
    {
        int i = 0;
        while (i < blocks.Count)
        {
            switch (blocks[i])
            {
                case Para { List: { Continuation: false } }:
                    i = AddList(target, blocks, i, tableStyle);
                    break;
                case Para p:
                    target.Add(BuildParagraph(p, tableStyle, inList: false, Neighbour(blocks, i - 1), Neighbour(blocks, i + 1)));
                    i++;
                    break;
                case TableBlock t:
                    target.Add(BuildTable(t));
                    i++;
                    break;
                default:
                    i++;
                    break;
            }
        }
    }

    // ---- lists -----------------------------------------------------------------------------

    private static bool SameList(ListRef a, ListRef b) =>
        a.NumId == b.NumId && a.Format == b.Format && ReferenceEquals(a.Group, b.Group);

    private int IndentOf(ListRef list) =>
        _doc.Numbering.Level(list.NumId, list.Level)?.IndLeft ?? 720 * (list.Level + 1);

    /// <summary>Turns the run of list paragraphs starting at <paramref name="start"/> into nested lists.</summary>
    private int AddList(BlockCollection target, IReadOnlyList<MBlock> blocks, int start, string? tableStyle)
    {
        var stack = new List<(List List, ListRef Key)>();
        int i = start;
        for (; i < blocks.Count && blocks[i] is Para { List: not null } p; i++)
        {
            var key = p.List!;
            if (key.Continuation)
            {
                var item = stack.Count > 0 ? stack[^1].List.ListItems.LastListItem : null;
                if (item == null) break;
                item.Blocks.Add(BuildParagraph(p, tableStyle, inList: true, Neighbour(blocks, i - 1), Neighbour(blocks, i + 1)));
                continue;
            }

            while (stack.Count > 0 && stack[^1].Key.Level > key.Level) stack.RemoveAt(stack.Count - 1);
            if (stack.Count > 0 && stack[^1].Key.Level == key.Level && !SameList(stack[^1].Key, key))
            {
                // A different list at this level: close the current one. At the top level
                // that means the next list starts as a new block.
                stack.RemoveAt(stack.Count - 1);
                if (stack.Count == 0 && i > start) break;
            }

            if (stack.Count == 0 || stack[^1].Key.Level < key.Level)
            {
                int indent = IndentOf(key) - (stack.Count > 0 ? IndentOf(stack[^1].Key) : 0);
                var list = new List
                {
                    MarkerStyle = FlowFormat.ToMarker(key.Format),
                    StartIndex = Math.Max(1, key.Ordinal),
                    Margin = new Thickness(0),
                    Padding = new Thickness(Math.Max(18, FlowFormat.Dip(indent)), 0, 0, 0),
                    Tag = new ListTag { NumId = key.NumId, Level = key.Level, Marker = FlowFormat.ToMarker(key.Format) },
                };
                if (stack.Count == 0) target.Add(list);
                else stack[^1].List.ListItems.LastListItem.Blocks.Add(list);
                stack.Add((list, key));
            }

            stack[^1].List.ListItems.Add(new ListItem(BuildParagraph(p, tableStyle, inList: true, Neighbour(blocks, i - 1), Neighbour(blocks, i + 1))));
        }
        return Math.Max(i, start + 1);
    }

    // ---- paragraphs ------------------------------------------------------------------------

    private static Para? Neighbour(IReadOnlyList<MBlock> blocks, int index) =>
        index >= 0 && index < blocks.Count ? blocks[index] as Para : null;

    private Paragraph BuildParagraph(Para p, string? tableStyle, bool inList, Para? previous, Para? next)
    {
        var para = new Paragraph();
        var styleId = ValidStyle(p.Props.StyleId);
        var baseRun = _doc.Styles.ResolveRun(styleId, null, tableStyle);
        FlowFormat.ApplyPara(para, p.Props, baseRun, inList);

        // Styles such as List Paragraph leave no space between neighbours of the same style.
        if (_doc.Styles.HasContextualSpacing(styleId, tableStyle))
        {
            if (previous != null && ValidStyle(previous.Props.StyleId) == styleId) para.Padding = new Thickness(0);
            if (next != null && ValidStyle(next.Props.StyleId) == styleId)
                para.Margin = new Thickness(para.Margin.Left, 0, para.Margin.Right, 0);
        }

        bool breakBefore = p.BreakBefore || p.Props.PageBreakBefore;
        if (_print) para.BreakPageBefore = breakBefore;
        else if (breakBefore) FlowFormat.SetPageBreak(para, true);
        AddInlines(para.Inlines, p.Inlines, baseRun);
        para.Tag = new ParaTag { Source = p, StyleId = styleId, Precise = p.Props, BreakBr = p.BreakBefore };
        return para;
    }

    private void AddInlines(InlineCollection target, IReadOnlyList<MInline> inlines, RunProps baseRun)
    {
        Hyperlink? link = null;
        string? current = null;
        foreach (var inline in inlines)
        {
            if (inline.Link != current)
            {
                current = inline.Link;
                link = null;
                if (!string.IsNullOrEmpty(current) && !_print)
                {
                    link = NewHyperlink(current);
                    target.Add(link);
                }
            }
            var element = BuildInline(inline, baseRun, link != null);
            if (element == null) continue;
            if (link != null) link.Inlines.Add(element);
            else target.Add(element);
        }
    }

    public static Hyperlink NewHyperlink(string target)
    {
        var link = new Hyperlink { Tag = target, ToolTip = target + "\nCtrl+klik om te openen", TextDecorations = null };
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri)) link.NavigateUri = uri;
        return link;
    }

    private WInline? BuildInline(MInline inline, RunProps baseRun, bool inLink)
    {
        switch (inline)
        {
            case TextRun t:
            {
                var run = new Run(t.Text);
                FlowFormat.ApplyRun(run, t.Props, baseRun, inLink);
                run.Tag = new RunTag { Loaded = t.Loaded, RawRPr = t.RawRPr, Precise = t.Props };
                return run;
            }
            case LineBreakInline br:
                return new LineBreak { Tag = br };
            case ImageInline image:
                return BuildImage(image);
            case RawInline raw:
                return BuildRaw(raw, baseRun);
            default:
                return null;
        }
    }

    public static BitmapSource? LoadBitmap(byte[] data)
    {
        try
        {
            var bitmap = new BitmapImage();
            using var ms = new MemoryStream(data);
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = ms;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Creates the image element for a picture, together with the tag its container must carry.</summary>
    public static Image NewImageElement(ImageInline image, BitmapSource source, out ImageTag tag)
    {
        double w = Math.Max(1, FlowFormat.EmuToDip(image.Cx)), h = Math.Max(1, FlowFormat.EmuToDip(image.Cy));
        var img = new Image { Source = source, Width = w, Height = h, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
        tag = new ImageTag { Source = image, Width = w, Height = h };
        return img;
    }

    private WInline BuildImage(ImageInline image)
    {
        var source = LoadBitmap(image.Data);
        if (source != null)
        {
            var element = NewImageElement(image, source, out var tag);
            return new InlineUIContainer(element) { BaselineAlignment = BaselineAlignment.Bottom, Tag = tag };
        }
        // Undecodable picture: keep it, show a placeholder.
        var raw = new RawInline { Kind = RawKind.Object, Text = "" };
        if (image.RawRun != null) raw.Raw.Add(image.RawRun);
        return BuildRaw(raw, _doc.Styles.ResolveRun(null));
    }

    private WInline BuildRaw(RawInline raw, RunProps baseRun)
    {
        bool note = raw.Kind is RawKind.Footnote or RawKind.Endnote;
        string text = raw.Kind switch
        {
            RawKind.Object => "Object",
            RawKind.Math => raw.Text.Length > 0 ? raw.Text : "Formule",
            _ => raw.Text,
        };
        var props = raw.Props;
        double size = FlowFormat.SizeDip(props.Size);

        if (_print)
        {
            if (raw.Kind == RawKind.Object) return new Run("");
            var run = new Run(text);
            FlowFormat.ApplyRun(run, note ? props with { Vert = VertAlign.Superscript } : props, baseRun, false);
            if (raw.Kind == RawKind.Symbol && raw.Font != null) run.FontFamily = FlowFormat.Font(raw.Font);
            return run;
        }

        var block = new TextBlock
        {
            Text = text,
            FontFamily = FlowFormat.Font(raw.Kind == RawKind.Symbol && raw.Font != null ? raw.Font : props.Font),
            FontSize = size,
            FontWeight = props.Bold ? FontWeights.Bold : FontWeights.Normal,
            FontStyle = props.Italic ? FontStyles.Italic : FontStyles.Normal,
            Foreground = FlowFormat.Brush(props.Color),
        };
        // An embedded element rests with its bottom edge on the baseline; pull it down by the
        // font's descent so its text lines up with the text around it.
        var family = block.FontFamily;
        double descent = Math.Max(0, size * (FlowFormat.LineSpacingOf(family) - family.Baseline));
        block.Margin = new Thickness(0, 0, 0, -descent);
        var alignment = BaselineAlignment.Baseline;
        switch (raw.Kind)
        {
            case RawKind.Field:
                block.Background = FlowFormat.Brush("E3E6EA");
                block.ToolTip = "Veld – wordt door Word bijgewerkt";
                break;
            case RawKind.Footnote:
            case RawKind.Endnote:
                block.FontSize = size * FlowFormat.ScriptScale;
                block.Foreground = FlowFormat.Brush("0F766E");
                block.Margin = new Thickness(0, 0, 1, size * 0.35);
                block.ToolTip = raw.Kind == RawKind.Footnote ? "Voetnoot (blijft behouden)" : "Eindnoot (blijft behouden)";
                break;
            case RawKind.Object:
                block.Background = FlowFormat.Brush("EEF0F2");
                block.Foreground = FlowFormat.Brush("5F6368");
                block.FontSize = size * 0.85;
                block.Padding = new Thickness(6, 0, 6, 0);
                block.ToolTip = "Vorm, tekstvak of ander object – blijft behouden, niet bewerkbaar";
                break;
            case RawKind.Math:
                block.FontFamily = FlowFormat.Font("Cambria Math");
                block.Background = FlowFormat.Brush("F1F3F4");
                block.ToolTip = "Formule – blijft behouden, niet bewerkbaar";
                break;
        }
        return new InlineUIContainer(block) { Tag = raw, BaselineAlignment = alignment };
    }

    // ---- tables ----------------------------------------------------------------------------

    private Table BuildTable(TableBlock t)
    {
        var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 0) };
        var widths = t.Grid.Select(w => Math.Max(8, FlowFormat.Dip(w))).ToArray();
        foreach (var w in widths) table.Columns.Add(new TableColumn { Width = new GridLength(w) });
        var group = new TableRowGroup();
        table.RowGroups.Add(group);
        var styleId = ValidStyle(t.StyleId);

        int columns = widths.Length, rows = t.Rows.Count;
        var remaining = new int[columns + 1];
        for (int r = 0; r < rows; r++)
        {
            var row = t.Rows[r];
            var tr = new TableRow { Tag = row };
            group.Rows.Add(tr);
            int col = 0;
            foreach (var cell in row.Cells)
            {
                while (col < columns && remaining[col] > 0) col++;
                int span = Math.Max(1, cell.ColSpan);
                bool lastColumn = col + span >= columns, lastRow = r + Math.Max(1, cell.RowSpan) >= rows;
                var tc = new TableCell
                {
                    ColumnSpan = span,
                    RowSpan = Math.Max(1, cell.RowSpan),
                    Padding = CellPadding,
                    Tag = cell,
                    BorderThickness = new Thickness(
                        Edge(cell.Left), Edge(cell.Top), lastColumn ? Edge(cell.Right) : 0, lastRow ? Edge(cell.Bottom) : 0),
                    BorderBrush = FlowFormat.Brush(new[] { cell.Top, cell.Left, cell.Bottom, cell.Right }
                        .FirstOrDefault(b => b.Visible)?.Color ?? "000000"),
                };
                if (cell.Shade != null) tc.Background = FlowFormat.Brush(cell.Shade);
                AddBlocks(tc.Blocks, cell.Blocks, styleId);
                if (tc.Blocks.Count == 0) tc.Blocks.Add(new Paragraph());
                tr.Cells.Add(tc);
                for (int k = col; k < col + span && k < columns; k++) remaining[k] = Math.Max(1, cell.RowSpan);
                col += span;
            }
            for (int k = 0; k < columns; k++)
                if (remaining[k] > 0) remaining[k]--;
        }

        table.Tag = new TableTag { Source = t, Widths = widths };
        return table;
    }

    private static double Edge(BorderSide side) => side.Visible ? Math.Max(1, Math.Round(side.WidthPt * 96 / 72)) : 0;
}
