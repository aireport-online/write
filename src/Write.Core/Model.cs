using System.Xml.Linq;

namespace Write.Core;

/// <summary>Page size and margins of the document, in twips.</summary>
public sealed record PageSetup
{
    public int Width { get; init; } = 11906;
    public int Height { get; init; } = 16838;
    public int MarginTop { get; init; } = 1417;
    public int MarginRight { get; init; } = 1417;
    public int MarginBottom { get; init; } = 1417;
    public int MarginLeft { get; init; } = 1417;

    public bool Landscape => Width > Height;
    public int ContentWidth => Math.Max(1440, Width - MarginLeft - MarginRight);
}

/// <summary>
/// An editable document: the body as a tree of blocks, plus the package it came from so
/// that everything this model does not represent can be written back unchanged.
/// </summary>
public sealed class DocModel
{
    public List<Block> Blocks { get; set; } = new();
    public PageSetup Page { get; set; } = new();
    /// <summary>Page setup as it was when the document was read.</summary>
    public PageSetup LoadedPage { get; init; } = new();
    public StyleSheet Styles { get; init; } = null!;
    public NumberingInfo Numbering { get; init; } = new();

    internal DocxPackage Package { get; init; } = null!;
    internal string DocPart { get; init; } = "word/document.xml";
    internal string StylesPart { get; init; } = "word/styles.xml";
    internal XElement? RawSectPr { get; init; }
    /// <summary>The original w:document element without its body (attributes and other children).</summary>
    internal XElement? RawRoot { get; init; }

    public static DocModel CreateBlank() => DocxReader.Read(Template.CreatePackage().Save());

    /// <summary>A copy that shares the package and styles but has its own block list.</summary>
    public DocModel WithBlocks(List<Block> blocks) => new()
    {
        Blocks = blocks,
        Page = Page,
        LoadedPage = LoadedPage,
        Styles = Styles,
        Numbering = Numbering,
        Package = Package,
        DocPart = DocPart,
        StylesPart = StylesPart,
        RawSectPr = RawSectPr,
        RawRoot = RawRoot,
    };
}

public abstract class Block { }

/// <summary>List membership of a paragraph.</summary>
public sealed class ListRef
{
    /// <summary>The numbering instance in the source document; null for a list created in the editor.</summary>
    public string? NumId { get; init; }
    public int Level { get; init; }
    public ListFormat Format { get; init; }
    /// <summary>The number this item carries (ordered lists), as far as it could be determined.</summary>
    public int Ordinal { get; init; } = 1;
    /// <summary>Identity of the list a new (NumId == null) item belongs to; items sharing it number together.</summary>
    public object? Group { get; init; }
    /// <summary>True for a further paragraph inside a list item: indented but not numbered.</summary>
    public bool Continuation { get; init; }
}

public sealed class Para : Block
{
    /// <summary>Current effective formatting, including <see cref="ParaProps.StyleId"/>.</summary>
    public ParaProps Props { get; set; } = new();
    /// <summary>Effective formatting when read from the file; null for paragraphs created since.</summary>
    public ParaProps? Loaded { get; set; }
    /// <summary>The original w:pPr, kept so that unmodified formatting is written back verbatim.</summary>
    public XElement? RawPPr { get; set; }
    public List<Inline> Inlines { get; set; } = new();
    public ListRef? List { get; set; }
    /// <summary>The paragraph starts with a manual page break (w:br w:type="page").</summary>
    public bool BreakBefore { get; set; }
    public List<string> Bookmarks { get; set; } = new();

    public string Text => string.Concat(Inlines.Select(i => i.PlainText));
}

public sealed class TableBlock : Block
{
    public XElement? RawTblPr { get; set; }
    /// <summary>Column widths in twips.</summary>
    public List<int> Grid { get; set; } = new();
    public List<TableRowModel> Rows { get; set; } = new();

    public string? StyleId => RawTblPr.W("tblStyle").WA("val");
}

public sealed class TableRowModel
{
    public XElement? RawTrPr { get; set; }
    public List<TableCellModel> Cells { get; set; } = new();
}

public sealed class TableCellModel
{
    public XElement? RawTcPr { get; set; }
    public int ColSpan { get; set; } = 1;
    public int RowSpan { get; set; } = 1;
    public List<Block> Blocks { get; set; } = new();

    // Display information derived from table, style and cell properties.
    public string? Shade { get; set; }
    public BorderSide Top { get; set; } = BorderSide.Thin;
    public BorderSide Left { get; set; } = BorderSide.Thin;
    public BorderSide Bottom { get; set; } = BorderSide.Thin;
    public BorderSide Right { get; set; } = BorderSide.Thin;
}

public abstract class Inline
{
    /// <summary>Hyperlink target: a URL, or "#bookmark" for a link inside the document.</summary>
    public string? Link { get; set; }
    public virtual string PlainText => "";
}

public sealed class TextRun : Inline
{
    public string Text { get; set; } = "";
    public RunProps Props { get; set; } = new();
    public RunProps? Loaded { get; set; }
    public XElement? RawRPr { get; set; }
    public override string PlainText => Text;
}

public sealed class LineBreakInline : Inline
{
    public XElement? RawRPr { get; set; }
    public override string PlainText => "\n";
}

public sealed class ImageInline : Inline
{
    public byte[] Data { get; set; } = Array.Empty<byte>();
    /// <summary>File extension without dot, lower case (png, jpeg, gif, …).</summary>
    public string Extension { get; set; } = "png";
    /// <summary>Displayed size in EMU.</summary>
    public long Cx { get; set; }
    public long Cy { get; set; }
    public long LoadedCx { get; set; }
    public long LoadedCy { get; set; }
    /// <summary>The original run holding the drawing; written back verbatim while the size is unchanged.</summary>
    public XElement? RawRun { get; set; }
    /// <summary>Relationship id of the image part in the source document.</summary>
    public string? RelId { get; set; }
    public string? Description { get; set; }
}

public enum RawKind { Field, Footnote, Endnote, Object, Math, Symbol }

/// <summary>
/// Inline content that is not editable here (fields, footnote references, shapes, equations…).
/// It is shown as a placeholder and written back exactly as it was read.
/// </summary>
public sealed class RawInline : Inline
{
    public RawKind Kind { get; set; }
    public List<XElement> Raw { get; set; } = new();
    /// <summary>Text to show in place of the content.</summary>
    public string Text { get; set; } = "";
    public string? Font { get; set; }
    public RunProps Props { get; set; } = new();
    public override string PlainText => Text;
}
