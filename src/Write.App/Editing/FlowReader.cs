using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using Write.Core;
using MBlock = Write.Core.Block;
using MInline = Write.Core.Inline;
using WBlock = System.Windows.Documents.Block;
using WInline = System.Windows.Documents.Inline;

namespace Write.Desktop.Editing;

/// <summary>
/// Reads the document in the editor back into a <see cref="DocModel"/>. Elements that still carry
/// their tag are matched with what was read from the file, so untouched formatting stays exact.
/// </summary>
internal sealed class FlowReader
{
    private readonly DocModel _doc;
    private readonly ParaProps _fallback;
    private readonly string? _hyperlinkStyle;
    private readonly HashSet<ParaTag> _seenParas = new();

    private static readonly XNamespace W = Ns.W;

    private sealed record ListContext(string? NumId, int Level, object? Group);

    public FlowReader(DocModel doc)
    {
        _doc = doc;
        _fallback = doc.Styles.ResolvePara(null);
        _hyperlinkStyle = doc.Styles.FindByName("Hyperlink", "character")?.Id;
    }

    public DocModel Read(FlowDocument flow, PageSetup page)
    {
        var blocks = new List<MBlock>();
        ReadBlocks(flow.Blocks, blocks, null);
        if (blocks.Count == 0 || blocks[^1] is not Para) blocks.Add(new Para { Props = _fallback });
        var model = _doc.WithBlocks(blocks);
        model.Page = page;
        return model;
    }

    /// <summary>Records what the editor shows right now as the reference for "unchanged".</summary>
    public static void TakeSnapshots(FlowDocument flow, DocModel doc)
    {
        var fallback = doc.Styles.ResolvePara(null);
        foreach (var para in FlowWalker.Paragraphs(flow.Blocks))
        {
            if (para.Tag is ParaTag tag) tag.Snapshot = FlowFormat.ReadPara(para, para.Parent is ListItem, fallback);
            foreach (var run in FlowWalker.Runs(para.Inlines))
                if (run.Tag is RunTag runTag) runTag.Snapshot = FlowFormat.ReadRun(run);
        }
    }

    private void ReadBlocks(IEnumerable<WBlock> source, List<MBlock> output, string? tableStyle)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case Paragraph p:
                    output.Add(ReadParagraph(p, null, tableStyle));
                    break;
                case List list:
                    ReadList(list, output, tableStyle, null);
                    break;
                case Table table:
                    output.Add(ReadTable(table));
                    break;
                case Section section:
                    ReadBlocks(section.Blocks, output, tableStyle);
                    break;
                case BlockUIContainer container:
                {
                    var image = ReadUiElement(container.Child, container.Tag, null);
                    if (image != null) output.Add(new Para { Props = _fallback, Inlines = { image } });
                    break;
                }
            }
        }
    }

    // ---- lists -----------------------------------------------------------------------------

    private void ReadList(List list, List<MBlock> output, string? tableStyle, ListContext? parent)
    {
        var format = FlowFormat.FromMarker(list.MarkerStyle);
        string? numId = null;
        object? group = null;
        int level;
        if (list.Tag is ListTag { NumId: not null } tag && tag.Marker == list.MarkerStyle)
        {
            numId = tag.NumId;
            level = tag.Level;
        }
        else
        {
            level = parent != null ? parent.Level + 1 : 0;
            if (parent?.NumId != null && _doc.Numbering.Level(parent.NumId, level)?.Format == format) numId = parent.NumId;
            else group = parent is { NumId: null, Group: not null } ? parent.Group : new object();
        }
        level = Math.Clamp(level, 0, 8);
        var context = new ListContext(numId, level, group);

        int ordinal = Math.Max(1, list.StartIndex);
        foreach (var item in list.ListItems)
        {
            bool numbered = false;
            foreach (var block in item.Blocks)
            {
                switch (block)
                {
                    case Paragraph p:
                        output.Add(ReadParagraph(p, new ListRef
                        {
                            NumId = numId, Level = level, Format = format, Ordinal = ordinal, Group = group, Continuation = numbered,
                        }, tableStyle));
                        numbered = true;
                        break;
                    case List nested:
                        ReadList(nested, output, tableStyle, context);
                        break;
                    default:
                        ReadBlocks(new[] { block }, output, tableStyle);
                        break;
                }
            }
            ordinal++;
        }
    }

    // ---- paragraphs ------------------------------------------------------------------------

    private static XElement? SectionOnly(XElement? pPr)
    {
        var sect = pPr?.Element(W + "sectPr");
        return sect == null ? null : new XElement(W + "pPr", new XElement(sect));
    }

    private static XElement? WithoutSection(XElement? pPr)
    {
        if (pPr?.Element(W + "sectPr") == null) return pPr;
        var copy = new XElement(pPr);
        copy.Element(W + "sectPr")!.Remove();
        return copy;
    }

    private Para ReadParagraph(Paragraph para, ListRef? list, string? tableStyle)
    {
        bool inList = list != null;
        var cur = FlowFormat.ReadPara(para, inList, _fallback);
        var result = new Para { List = list };
        string? styleId = null;

        if (para.Tag is ParaTag tag)
        {
            bool duplicate = !_seenParas.Add(tag);
            styleId = tag.StyleId != null && _doc.Styles.Get(tag.StyleId) != null ? tag.StyleId : null;
            var props = FlowFormat.Merge(cur, tag.Snapshot, tag.Precise) with { StyleId = styleId };

            bool breakSame = tag.Snapshot != null && cur.PageBreakBefore == tag.Snapshot.PageBreakBefore;
            if (breakSame)
            {
                result.BreakBefore = tag.BreakBr;
                props = props with { PageBreakBefore = tag.Precise.PageBreakBefore };
            }
            else
            {
                result.BreakBefore = cur.PageBreakBefore;
                props = props with { PageBreakBefore = false };
            }

            var source = tag.Source;
            bool dropSection = duplicate || tag.DropSection;
            if (tag.StyleApplied)
            {
                result.RawPPr = dropSection ? null : SectionOnly(source?.RawPPr);
                result.Loaded = null;
            }
            else if (source != null)
            {
                result.RawPPr = dropSection ? WithoutSection(source.RawPPr) : source.RawPPr;
                result.Loaded = source.Loaded;
            }
            if (source != null && !duplicate && !tag.DropBookmarks) result.Bookmarks = source.Bookmarks.ToList();
            result.Props = props;
        }
        else
        {
            result.BreakBefore = cur.PageBreakBefore;
            result.Props = cur with { PageBreakBefore = false };
        }

        ReadInlines(para.Inlines, result.Inlines, null, false, false);
        return result;
    }

    private void ReadInlines(IEnumerable<WInline> source, List<MInline> output, string? link, bool underline, bool strike)
    {
        foreach (var inline in source)
        {
            switch (inline)
            {
                case Run run:
                {
                    if (string.IsNullOrEmpty(run.Text)) break;
                    var cur = FlowFormat.ReadRun(run, underline, strike);
                    var text = new TextRun { Text = run.Text, Link = link };
                    if (run.Tag is RunTag tag)
                    {
                        text.Props = FlowFormat.Merge(cur, tag.Snapshot, tag.Precise);
                        text.RawRPr = tag.RawRPr;
                        text.Loaded = tag.Loaded;
                    }
                    else text.Props = cur with { StyleId = link != null ? _hyperlinkStyle : null };
                    output.Add(text);
                    break;
                }
                case Hyperlink hyperlink:
                {
                    var target = hyperlink.Tag as string ?? hyperlink.NavigateUri?.OriginalString;
                    ReadInlines(hyperlink.Inlines, output, string.IsNullOrEmpty(target) ? link : target,
                        underline || FlowFormat.Has(hyperlink.TextDecorations, TextDecorationLocation.Underline),
                        strike || FlowFormat.Has(hyperlink.TextDecorations, TextDecorationLocation.Strikethrough));
                    break;
                }
                case Span span:
                    ReadInlines(span.Inlines, output, link,
                        underline || FlowFormat.Has(span.TextDecorations, TextDecorationLocation.Underline),
                        strike || FlowFormat.Has(span.TextDecorations, TextDecorationLocation.Strikethrough));
                    break;
                case LineBreak br:
                    output.Add(new LineBreakInline { RawRPr = (br.Tag as LineBreakInline)?.RawRPr, Link = link });
                    break;
                case InlineUIContainer container:
                {
                    var element = ReadUiElement(container.Child, container.Tag, link);
                    if (element != null) output.Add(element);
                    break;
                }
            }
        }
    }

    private static MInline? ReadUiElement(UIElement? child, object? tag, string? link)
    {
        if (tag is RawInline raw)
        {
            raw.Link = link;
            return raw;
        }
        if (child is not Image image) return null;
        double width = !double.IsNaN(image.Width) ? image.Width : image.ActualWidth;
        double height = !double.IsNaN(image.Height) ? image.Height : image.ActualHeight;
        if (width <= 0 && image.Source is BitmapSource sized) { width = sized.Width; height = sized.Height; }

        if (tag is ImageTag imageTag)
        {
            var src = imageTag.Source;
            bool same = Math.Abs(width - imageTag.Width) < 0.25 && Math.Abs(height - imageTag.Height) < 0.25;
            return new ImageInline
            {
                Data = src.Data, Extension = src.Extension, Description = src.Description, RawRun = src.RawRun, RelId = src.RelId,
                LoadedCx = src.LoadedCx, LoadedCy = src.LoadedCy, Link = link,
                Cx = same ? src.Cx : FlowFormat.DipToEmu(width),
                Cy = same ? src.Cy : FlowFormat.DipToEmu(height),
            };
        }

        // A picture that was pasted into the editor: store it as PNG.
        if (image.Source is not BitmapSource bitmap || width <= 0 || height <= 0) return null;
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var ms = new MemoryStream();
            encoder.Save(ms);
            return new ImageInline
            {
                Data = ms.ToArray(), Extension = "png", Link = link,
                Cx = FlowFormat.DipToEmu(width), Cy = FlowFormat.DipToEmu(height),
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ---- tables ----------------------------------------------------------------------------

    private TableBlock ReadTable(Table table)
    {
        var tag = table.Tag as TableTag;
        var source = tag?.Source;
        var block = new TableBlock { RawTblPr = source?.RawTblPr };
        string? styleId = source != null ? source.StyleId : _doc.Styles.Get("TableGrid")?.Id;
        if (styleId != null && _doc.Styles.Get(styleId) == null) styleId = null;

        var widths = table.Columns.Select(c => c.Width.IsAbsolute ? c.Width.Value : double.NaN).ToArray();
        if (tag != null && source != null && widths.Length == tag.Widths.Length && widths.Length == source.Grid.Count &&
            widths.Zip(tag.Widths, (a, b) => Math.Abs(a - b) < 0.05).All(x => x))
        {
            block.Grid = source.Grid.ToList();
        }
        else if (widths.Length > 0)
        {
            double fallback = FlowFormat.Dip(_doc.Page.ContentWidth) / widths.Length;
            block.Grid = widths.Select(w => Math.Max(1, FlowFormat.Twips(double.IsNaN(w) ? fallback : w))).ToList();
        }

        foreach (var group in table.RowGroups)
        {
            foreach (var row in group.Rows)
            {
                var rowModel = new TableRowModel { RawTrPr = (row.Tag as TableRowModel)?.RawTrPr };
                foreach (var cell in row.Cells)
                {
                    var cellModel = new TableCellModel
                    {
                        ColSpan = Math.Max(1, cell.ColumnSpan),
                        RowSpan = Math.Max(1, cell.RowSpan),
                        RawTcPr = (cell.Tag as TableCellModel)?.RawTcPr,
                    };
                    ReadBlocks(cell.Blocks, cellModel.Blocks, styleId);
                    rowModel.Cells.Add(cellModel);
                }
                if (rowModel.Cells.Count > 0) block.Rows.Add(rowModel);
            }
        }
        return block;
    }
}

/// <summary>Enumerates the text elements of a document.</summary>
internal static class FlowWalker
{
    public static IEnumerable<Paragraph> Paragraphs(IEnumerable<WBlock> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    yield return p;
                    break;
                case List list:
                    foreach (var item in list.ListItems)
                    foreach (var p in Paragraphs(item.Blocks))
                        yield return p;
                    break;
                case Table table:
                    foreach (var group in table.RowGroups)
                    foreach (var row in group.Rows)
                    foreach (var cell in row.Cells)
                    foreach (var p in Paragraphs(cell.Blocks))
                        yield return p;
                    break;
                case Section section:
                    foreach (var p in Paragraphs(section.Blocks)) yield return p;
                    break;
            }
        }
    }

    public static IEnumerable<Run> Runs(IEnumerable<WInline> inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span)
                foreach (var inner in Runs(span.Inlines))
                    yield return inner;
        }
    }

    public static T? Ancestor<T>(DependencyObject? node) where T : class
    {
        while (node != null)
        {
            if (node is T match) return match;
            node = node is FrameworkContentElement fce ? fce.Parent : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>The paragraphs touched by a selection.</summary>
    public static List<Paragraph> SelectedParagraphs(TextSelection selection)
    {
        var result = new List<Paragraph>();
        var first = selection.Start.Paragraph;
        var last = selection.End.Paragraph;
        if (first == null && last == null) return result;
        first ??= last;
        last ??= first;
        if (ReferenceEquals(first, last))
        {
            result.Add(first!);
            return result;
        }
        var doc = Ancestor<FlowDocument>(first);
        if (doc == null)
        {
            result.Add(first!);
            return result;
        }
        bool inside = false;
        foreach (var p in Paragraphs(doc.Blocks))
        {
            if (ReferenceEquals(p, first)) inside = true;
            if (inside) result.Add(p);
            if (ReferenceEquals(p, last)) break;
        }
        return result;
    }
}
