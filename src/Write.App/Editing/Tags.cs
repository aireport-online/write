using System.Windows;
using System.Xml.Linq;
using Write.Core;

namespace Write.Desktop.Editing;

/// <summary>
/// Remembers where a paragraph in the editor came from, so that everything that was not
/// touched can be written back exactly as it was read.
/// </summary>
internal sealed class ParaTag
{
    /// <summary>The paragraph as read from the file (null for paragraphs styled in the editor from scratch).</summary>
    public Para? Source;
    public string? StyleId;
    /// <summary>A style was applied in the editor: the original direct formatting no longer applies.</summary>
    public bool StyleApplied;
    /// <summary>The exact formatting the paragraph had when <see cref="Snapshot"/> was taken.</summary>
    public ParaProps Precise = new();
    /// <summary>What reading the editor paragraph returned at that moment.</summary>
    public ParaProps? Snapshot;
    /// <summary>The paragraph's sectPr/bookmarks must not be written (they belong to its other half).</summary>
    public bool DropSection, DropBookmarks;
    /// <summary>The page break before this paragraph is a manual break (w:br), not a paragraph property.</summary>
    public bool BreakBr;

    public ParaTag Clone() => (ParaTag)MemberwiseClone();
}

internal sealed class RunTag
{
    public RunProps? Loaded;
    public XElement? RawRPr;
    public RunProps Precise = new();
    public RunProps? Snapshot;
}

internal sealed class ListTag
{
    public string? NumId;
    public int Level;
    public TextMarkerStyle Marker;
}

internal sealed class TableTag
{
    public TableBlock Source = null!;
    public double[] Widths = Array.Empty<double>();
}

internal sealed class ImageTag
{
    public ImageInline Source = null!;
    public double Width, Height;
}
