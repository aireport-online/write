using System.Security.Cryptography;
using System.Xml.Linq;

namespace Write.Core;

/// <summary>
/// Writes a <see cref="DocModel"/> back to .docx. The original package is the starting point:
/// only the document body (and, where needed, relationships, numbering and styles) is replaced.
/// Formatting that was not changed since reading is written back from the original XML.
/// </summary>
public static class DocxWriter
{
    public static byte[] Write(DocModel doc) => new Session(doc).Run();

    private static readonly string[] PPrOrder =
    {
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr",
        "suppressLineNumbers", "pBdr", "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap",
        "overflowPunct", "topLinePunct", "autoSpaceDE", "autoSpaceDN", "bidi", "adjustRightInd",
        "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents", "suppressOverlap", "jc",
        "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle", "rPr",
        "sectPr", "pPrChange",
    };

    private static readonly string[] RPrOrder =
    {
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline",
        "shadow", "emboss", "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing",
        "w", "kern", "position", "sz", "szCs", "highlight", "u", "effect", "bdr", "shd", "fitText",
        "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout", "specVanish", "oMath",
    };

    private static readonly string[] TcPrOrder =
    {
        "cnfStyle", "tcW", "gridSpan", "hMerge", "vMerge", "tcBorders", "shd", "noWrap", "tcMar",
        "textDirection", "tcFitText", "vAlign", "hideMark",
    };

    private static void Sort(List<XElement> children, string[] order)
    {
        int Rank(XElement e)
        {
            if (e.Name.Namespace != Ns.W) return order.Length + 1;
            int i = Array.IndexOf(order, e.Name.LocalName);
            return i < 0 ? order.Length : i;
        }
        var sorted = children.Select((e, i) => (e, i)).OrderBy(t => Rank(t.e)).ThenBy(t => t.i).Select(t => t.e).ToList();
        children.Clear();
        children.AddRange(sorted);
    }

    private static XElement El(string name, params object?[] content) => new(Ns.W + name, content);
    private static XAttribute At(string name, string value) => new(Ns.W + name, value);
    private static XElement Val(string name, string value) => El(name, At("val", value));

    private sealed class Session
    {
        private readonly DocModel _doc;
        private readonly DocxPackage _pkg;
        private readonly Rels _rels;
        private readonly string _relsPath;
        private readonly StyleSheet _styles;
        private NumberingBuilder? _numbering;
        private string? _numberingPart;
        private readonly Dictionary<object, string> _groupNums = new();
        private readonly Dictionary<object, List<ListFormat>> _groupFormats = new();
        private readonly Dictionary<object, int> _groupStart = new();
        private readonly HashSet<string> _bookmarks = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _docPrIds = new();
        private readonly HashSet<XElement> _uniqueRaw = new();
        private readonly Dictionary<string, string> _mediaByHash = new();
        private int _bookmarkId;
        private int _drawingId = 2_000_000;

        public Session(DocModel doc)
        {
            _doc = doc;
            _pkg = doc.Package.Clone();
            _relsPath = DocxPackage.RelsPathFor(doc.DocPart);
            _rels = Rels.Parse(_pkg.GetXml(_relsPath));
            _styles = doc.Styles;
        }

        public byte[] Run()
        {
            CollectListGroups(_doc.Blocks);

            var body = El("body");
            body.Add(WriteBlocks(_doc.Blocks, null, inCell: false));
            body.Add(BuildSectPr());

            XElement root;
            if (_doc.RawRoot != null)
            {
                root = new XElement(_doc.RawRoot.Name, _doc.RawRoot.Attributes());
                bool placed = false;
                foreach (var child in _doc.RawRoot.Elements())
                {
                    // w:background precedes the body; anything else follows it.
                    if (!placed && !child.IsW("background")) { root.Add(body); placed = true; }
                    root.Add(new XElement(child));
                }
                if (!placed) root.Add(body);
            }
            else root = new XElement(Ns.W + "document", body);
            EnsureNamespace(root, "w", Ns.W);
            EnsureNamespace(root, "r", Ns.R);

            _pkg.SetXml(_doc.DocPart, new XDocument(root));
            _pkg.SetXml(_relsPath, _rels.ToXml());
            if (_numbering is { Dirty: true }) _pkg.SetXml(_numberingPart!, _numbering.Document);
            if (_styles.Dirty) _pkg.SetXml(_doc.StylesPart, new XDocument(_styles.Doc));
            TouchCoreProperties();
            return _pkg.Save();
        }

        private static void EnsureNamespace(XElement root, string prefix, XNamespace ns)
        {
            if (root.Attributes().Any(a => a.IsNamespaceDeclaration && a.Value == ns.NamespaceName && a.Name.LocalName != "xmlns")) return;
            if (root.Attributes().Any(a => a.IsNamespaceDeclaration && a.Name.LocalName == prefix)) return;
            root.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        }

        private void TouchCoreProperties()
        {
            const string path = "docProps/core.xml";
            try
            {
                var core = _pkg.GetXml(path);
                var modified = core?.Root?.Element(Ns.DcTerms + "modified");
                if (modified == null) return;
                modified.Value = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
                _pkg.SetXml(path, core!);
            }
            catch (DocxFormatException) { }
        }

        // ---- section -----------------------------------------------------------------------

        private XElement BuildSectPr()
        {
            var page = _doc.Page;
            XElement sect;
            if (_doc.RawSectPr != null)
            {
                sect = new XElement(_doc.RawSectPr);
                sect.W("sectPrChange")?.Remove();
                if (page == _doc.LoadedPage) return sect;
            }
            else sect = El("sectPr");

            var size = sect.W("pgSz");
            if (size == null)
            {
                size = El("pgSz");
                var anchor = sect.Elements().LastOrDefault(e => e.IsW("headerReference") || e.IsW("footerReference") ||
                                                               e.IsW("footnotePr") || e.IsW("endnotePr") || e.IsW("type"));
                if (anchor != null) anchor.AddAfterSelf(size); else sect.AddFirst(size);
            }
            size.SetAttributeValue(Ns.W + "w", X.Inv(page.Width));
            size.SetAttributeValue(Ns.W + "h", X.Inv(page.Height));
            size.SetAttributeValue(Ns.W + "orient", page.Landscape ? "landscape" : null);

            var mar = sect.W("pgMar");
            if (mar == null)
            {
                mar = El("pgMar", At("header", "708"), At("footer", "708"), At("gutter", "0"));
                size.AddAfterSelf(mar);
            }
            mar.SetAttributeValue(Ns.W + "top", X.Inv(page.MarginTop));
            mar.SetAttributeValue(Ns.W + "right", X.Inv(page.MarginRight));
            mar.SetAttributeValue(Ns.W + "bottom", X.Inv(page.MarginBottom));
            mar.SetAttributeValue(Ns.W + "left", X.Inv(page.MarginLeft));
            return sect;
        }

        // ---- lists -------------------------------------------------------------------------

        /// <summary>Gathers the level formats of every list created in the editor.</summary>
        private void CollectListGroups(IEnumerable<Block> blocks)
        {
            foreach (var block in blocks)
            {
                if (block is Para { List: { Continuation: false } list } && !UsesOriginalNum(list))
                {
                    var key = list.Group ?? this;
                    if (!_groupFormats.TryGetValue(key, out var formats)) _groupFormats[key] = formats = new List<ListFormat>();
                    while (formats.Count <= list.Level) formats.Add((ListFormat)(-1));
                    if ((int)formats[list.Level] == -1) formats[list.Level] = list.Format;
                    if (list.Level == 0 && !_groupStart.ContainsKey(key)) _groupStart[key] = list.Ordinal;
                }
                else if (block is TableBlock table)
                {
                    foreach (var row in table.Rows)
                    foreach (var cell in row.Cells)
                        CollectListGroups(cell.Blocks);
                }
            }
        }

        private bool UsesOriginalNum(ListRef list) =>
            list.NumId != null && _doc.Numbering.Level(list.NumId, list.Level) is { } level && level.Format == list.Format;

        private string NumIdFor(ListRef list)
        {
            if (UsesOriginalNum(list)) return list.NumId!;
            var key = list.Group ?? this;
            if (_groupNums.TryGetValue(key, out var id)) return id;

            var formats = _groupFormats.TryGetValue(key, out var f) ? f.ToList() : new List<ListFormat> { list.Format };
            // Levels that were never used take a sensible default based on the first level.
            var first = formats.FirstOrDefault(x => (int)x != -1, list.Format);
            for (int i = 0; i < formats.Count; i++)
            {
                if ((int)formats[i] != -1) continue;
                formats[i] = first.IsBullet()
                    ? (i % 3) switch { 0 => ListFormat.Disc, 1 => ListFormat.Circle, _ => ListFormat.Square }
                    : (i % 3) switch { 0 => ListFormat.Decimal, 1 => ListFormat.LowerLetter, _ => ListFormat.LowerRoman };
            }

            EnsureNumbering();
            int? start = _groupStart.TryGetValue(key, out var s) && !formats[0].IsBullet() ? s : null;
            id = _numbering!.NewNum(formats, start);
            _groupNums[key] = id;
            return id;
        }

        private void EnsureNumbering()
        {
            if (_numbering != null) return;
            XDocument? existing = null;
            if (_rels.ByType(RelTypes.Numbering) is { External: false } rel)
            {
                _numberingPart = DocxPackage.ResolveTarget(_doc.DocPart, rel.Target);
                try { existing = _pkg.GetXml(_numberingPart); } catch (DocxFormatException) { }
                if (existing?.Root == null || existing.Root.Name != Ns.W + "numbering") existing = null;
            }
            else
            {
                _numberingPart = DocxPackage.ResolveTarget(_doc.DocPart, "numbering.xml");
                _rels.Add(RelTypes.Numbering, "numbering.xml");
            }
            _pkg.EnsureOverrideContentType(_numberingPart, Template.NumberingContentType);
            _numbering = new NumberingBuilder(existing);
        }

        // ---- blocks ------------------------------------------------------------------------

        private List<XElement> WriteBlocks(IReadOnlyList<Block> blocks, string? tableStyle, bool inCell)
        {
            var result = new List<XElement>();
            foreach (var block in blocks)
            {
                if (block is Para p) result.Add(WritePara(p, tableStyle));
                else if (block is TableBlock t) result.Add(WriteTable(t));
            }
            // A table cell must end with a paragraph.
            if (inCell && (result.Count == 0 || !result[^1].IsW("p"))) result.Add(El("p"));
            return result;
        }

        private XElement WritePara(Para para, string? tableStyle)
        {
            var styleId = para.Props.StyleId;
            if (styleId != null && _styles.Get(styleId) == null) styleId = null;
            var p = El("p");
            var pPr = BuildPPr(para, styleId, tableStyle);
            if (pPr != null) p.Add(pPr);

            foreach (var name in para.Bookmarks)
            {
                if (!_bookmarks.Add(name)) continue;
                var id = X.Inv(_bookmarkId++);
                p.Add(El("bookmarkStart", At("id", id), At("name", name)));
                p.Add(El("bookmarkEnd", At("id", id)));
            }

            if (para.BreakBefore) p.Add(El("r", El("br", At("type", "page"))));

            XElement? link = null;
            string? linkTarget = null;
            foreach (var inline in para.Inlines)
            {
                var nodes = WriteInline(inline, styleId, tableStyle);
                if (nodes.Count == 0) continue;
                if (inline.Link != linkTarget)
                {
                    link = null;
                    linkTarget = inline.Link;
                    if (!string.IsNullOrEmpty(linkTarget))
                    {
                        link = BuildHyperlink(linkTarget);
                        p.Add(link);
                    }
                }
                (link ?? p).Add(nodes);
            }
            return p;
        }

        private XElement BuildHyperlink(string target)
        {
            var link = El("hyperlink");
            int hash = target.IndexOf('#');
            if (hash == 0)
            {
                link.Add(At("anchor", target[1..]));
            }
            else
            {
                string url = target;
                var rel = _rels.Find(RelTypes.Hyperlink, url, true);
                var id = rel?.Id ?? _rels.Add(RelTypes.Hyperlink, url, external: true);
                link.Add(new XAttribute(Ns.R + "id", id));
            }
            link.Add(At("history", "1"));
            return link;
        }

        private XElement? BuildPPr(Para para, string? styleId, string? tableStyle)
        {
            var cur = para.Props with { StyleId = styleId };
            var loaded = para.Loaded;
            var style = _styles.ResolvePara(styleId, tableStyle);
            var children = para.RawPPr?.Elements().Select(e => new XElement(e)).ToList() ?? new List<XElement>();
            children.RemoveAll(e => e.IsW("pPrChange"));
            bool inList = para.List != null;

            // Keeps the original elements when the value is untouched; otherwise writes the
            // current value, unless the paragraph style already provides it.
            void Group(string[] names, bool unchanged, bool fromStyle, Func<IEnumerable<XElement>> generate)
            {
                bool rawHas = children.Any(c => c.Name.Namespace == Ns.W && Array.IndexOf(names, c.Name.LocalName) >= 0);
                if (rawHas && loaded != null && unchanged) return;
                children.RemoveAll(c => c.Name.Namespace == Ns.W && Array.IndexOf(names, c.Name.LocalName) >= 0);
                if (!fromStyle) children.AddRange(generate());
            }

            Group(new[] { "pStyle" }, loaded?.StyleId == cur.StyleId, cur.StyleId == null,
                () => new[] { Val("pStyle", cur.StyleId!) });

            Group(new[] { "jc" }, loaded?.Align == cur.Align, style.Align == cur.Align,
                () => new[] { Val("jc", cur.Align switch { Align.Center => "center", Align.Right => "right", Align.Justify => "both", _ => "left" }) });

            if (!inList)
            {
                Group(new[] { "ind" },
                    loaded != null && (loaded.IndLeft, loaded.IndRight, loaded.IndFirst) == (cur.IndLeft, cur.IndRight, cur.IndFirst),
                    (style.IndLeft, style.IndRight, style.IndFirst) == (cur.IndLeft, cur.IndRight, cur.IndFirst),
                    () =>
                    {
                        var ind = El("ind", At("left", X.Inv(cur.IndLeft)), At("right", X.Inv(cur.IndRight)));
                        if (cur.IndFirst < 0) ind.Add(At("hanging", X.Inv(-cur.IndFirst)));
                        else ind.Add(At("firstLine", X.Inv(cur.IndFirst)));
                        return new[] { ind };
                    });
            }
            else if (para.List!.Continuation)
            {
                children.RemoveAll(c => c.IsW("ind"));
                int left = _doc.Numbering.Level(para.List.NumId, para.List.Level)?.IndLeft ?? 720 * (para.List.Level + 1);
                children.Add(El("ind", At("left", X.Inv(left))));
            }

            Group(new[] { "spacing" },
                loaded != null && (loaded.Before, loaded.After, loaded.Line, loaded.LineRule) == (cur.Before, cur.After, cur.Line, cur.LineRule),
                (style.Before, style.After, style.Line, style.LineRule) == (cur.Before, cur.After, cur.Line, cur.LineRule),
                () => new[]
                {
                    El("spacing", At("before", X.Inv(cur.Before)), At("after", X.Inv(cur.After)), At("line", X.Inv(cur.Line)),
                        At("lineRule", cur.LineRule switch { LineRule.Exact => "exact", LineRule.AtLeast => "atLeast", _ => "auto" })),
                });

            Group(new[] { "pageBreakBefore" }, loaded?.PageBreakBefore == cur.PageBreakBefore, style.PageBreakBefore == cur.PageBreakBefore,
                () => new[] { cur.PageBreakBefore ? El("pageBreakBefore") : Val("pageBreakBefore", "0") });

            if (inList && !para.List!.Continuation)
            {
                children.RemoveAll(c => c.IsW("numPr"));
                children.Add(El("numPr", Val("ilvl", X.Inv(para.List.Level)), Val("numId", NumIdFor(para.List))));
            }

            if (children.Count == 0) return null;
            Sort(children, PPrOrder);
            return El("pPr", children);
        }

        // ---- inline content ----------------------------------------------------------------

        private List<XElement> WriteInline(Inline inline, string? paraStyle, string? tableStyle)
        {
            var result = new List<XElement>();
            switch (inline)
            {
                case TextRun run:
                {
                    if (run.Text.Length == 0) break;
                    var r = El("r");
                    var rPr = BuildRPr(run.Props, run.Loaded, run.RawRPr, paraStyle, tableStyle);
                    if (rPr != null) r.Add(rPr);
                    AddText(r, run.Text);
                    result.Add(r);
                    break;
                }
                case LineBreakInline br:
                {
                    var r = El("r");
                    if (br.RawRPr != null && br.RawRPr.HasElements) r.Add(new XElement(br.RawRPr));
                    r.Add(El("br"));
                    result.Add(r);
                    break;
                }
                case ImageInline image:
                    result.Add(WriteImage(image));
                    break;
                case RawInline raw:
                    foreach (var e in raw.Raw)
                    {
                        // A note reference may occur only once in a document.
                        if (raw.Kind is RawKind.Footnote or RawKind.Endnote && !_uniqueRaw.Add(e)) continue;
                        var copy = new XElement(e);
                        FixDrawingIds(copy);
                        result.Add(copy);
                    }
                    break;
            }
            return result;
        }

        private static void AddText(XElement r, string text)
        {
            text = X.CleanText(text);
            int start = 0;
            void Flush(int end)
            {
                if (end > start)
                    r.Add(El("t", new XAttribute(XNamespace.Xml + "space", "preserve"), text[start..end]));
            }
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\t') { Flush(i); r.Add(El("tab")); start = i + 1; }
                else if (c == '\n') { Flush(i); r.Add(El("br")); start = i + 1; }
                else if (c == '\r') { Flush(i); start = i + 1; }
            }
            Flush(text.Length);
        }

        private XElement? BuildRPr(RunProps cur, RunProps? loaded, XElement? raw, string? paraStyle, string? tableStyle)
        {
            if (cur.StyleId != null && _styles.Get(cur.StyleId) == null) cur = cur with { StyleId = null };
            var style = _styles.ResolveRun(paraStyle, cur.StyleId, tableStyle);
            var children = raw?.Elements().Select(e => new XElement(e)).ToList() ?? new List<XElement>();
            children.RemoveAll(e => e.IsW("rPrChange"));

            void Group(string[] names, bool unchanged, bool fromStyle, Func<IEnumerable<XElement>> generate)
            {
                bool rawHas = children.Any(c => c.Name.Namespace == Ns.W && Array.IndexOf(names, c.Name.LocalName) >= 0);
                if (rawHas && loaded != null && unchanged) return;
                children.RemoveAll(c => c.Name.Namespace == Ns.W && Array.IndexOf(names, c.Name.LocalName) >= 0);
                if (!fromStyle) children.AddRange(generate());
            }

            static IEnumerable<XElement> Toggle(bool on, params string[] names) =>
                names.Select(n => on ? El(n) : Val(n, "0"));

            Group(new[] { "rStyle" }, loaded?.StyleId == cur.StyleId, cur.StyleId == null,
                () => new[] { Val("rStyle", cur.StyleId!) });

            Group(new[] { "rFonts" }, loaded?.Font == cur.Font, style.Font == cur.Font,
                () => new[] { El("rFonts", At("ascii", cur.Font), At("hAnsi", cur.Font), At("eastAsia", cur.Font), At("cs", cur.Font)) });

            Group(new[] { "b", "bCs" }, loaded?.Bold == cur.Bold, style.Bold == cur.Bold, () => Toggle(cur.Bold, "b", "bCs"));
            Group(new[] { "i", "iCs" }, loaded?.Italic == cur.Italic, style.Italic == cur.Italic, () => Toggle(cur.Italic, "i", "iCs"));
            Group(new[] { "strike", "dstrike" }, loaded?.Strike == cur.Strike, style.Strike == cur.Strike, () => Toggle(cur.Strike, "strike"));

            Group(new[] { "color" }, loaded?.Color == cur.Color, style.Color == cur.Color,
                () => new[] { Val("color", cur.Color) });

            Group(new[] { "sz", "szCs" }, loaded?.Size == cur.Size, style.Size == cur.Size,
                () => new[] { Val("sz", X.Inv(cur.Size)), Val("szCs", X.Inv(cur.Size)) });

            Group(new[] { "highlight", "shd" }, loaded != null && loaded.Highlight == cur.Highlight, style.Highlight == cur.Highlight,
                () =>
                {
                    if (cur.Highlight == null) return new[] { Val("highlight", "none") };
                    var name = Colors.HexToHighlight(cur.Highlight);
                    return name != null
                        ? new[] { Val("highlight", name) }
                        : new[] { El("shd", At("val", "clear"), At("color", "auto"), At("fill", cur.Highlight)) };
                });

            Group(new[] { "u" }, loaded?.Underline == cur.Underline, style.Underline == cur.Underline,
                () => new[] { Val("u", cur.Underline ? "single" : "none") });

            Group(new[] { "vertAlign" }, loaded?.Vert == cur.Vert, style.Vert == cur.Vert,
                () => new[] { Val("vertAlign", cur.Vert switch { VertAlign.Superscript => "superscript", VertAlign.Subscript => "subscript", _ => "baseline" }) });

            if (children.Count == 0) return null;
            Sort(children, RPrOrder);
            return El("rPr", children);
        }

        // ---- images ------------------------------------------------------------------------

        private void FixDrawingIds(XElement e)
        {
            foreach (var docPr in e.DescendantsAndSelf(Ns.WP + "docPr"))
            {
                var id = (string?)docPr.Attribute("id");
                if (id == null || !_docPrIds.Add(id))
                {
                    var fresh = X.Inv(_drawingId++);
                    docPr.SetAttributeValue("id", fresh);
                    _docPrIds.Add(fresh);
                }
            }
        }

        private XElement WriteImage(ImageInline image)
        {
            if (image.RawRun != null && image.Cx == image.LoadedCx && image.Cy == image.LoadedCy)
            {
                var copy = new XElement(image.RawRun);
                FixDrawingIds(copy);
                return copy;
            }

            string relId;
            if (image.RelId != null && _rels.ById(image.RelId) is { External: false, Type: RelTypes.Image } rel &&
                _pkg.Has(DocxPackage.ResolveTarget(_doc.DocPart, rel.Target)))
                relId = image.RelId;
            else relId = AddMedia(image);

            var id = X.Inv(_drawingId++);
            _docPrIds.Add(id);
            string cx = X.Inv(Math.Max(1, image.Cx)), cy = X.Inv(Math.Max(1, image.Cy));
            XNamespace wp = Ns.WP, a = Ns.A, pic = Ns.Pic;

            var docPr = new XElement(wp + "docPr", new XAttribute("id", id), new XAttribute("name", "Afbeelding " + id));
            if (!string.IsNullOrEmpty(image.Description)) docPr.Add(new XAttribute("descr", image.Description));

            var inline = new XElement(wp + "inline",
                new XAttribute(XNamespace.Xmlns + "wp", wp.NamespaceName),
                new XAttribute("distT", "0"), new XAttribute("distB", "0"), new XAttribute("distL", "0"), new XAttribute("distR", "0"),
                new XElement(wp + "extent", new XAttribute("cx", cx), new XAttribute("cy", cy)),
                new XElement(wp + "effectExtent", new XAttribute("l", "0"), new XAttribute("t", "0"), new XAttribute("r", "0"), new XAttribute("b", "0")),
                docPr,
                new XElement(wp + "cNvGraphicFramePr",
                    new XElement(a + "graphicFrameLocks", new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName), new XAttribute("noChangeAspect", "1"))),
                new XElement(a + "graphic", new XAttribute(XNamespace.Xmlns + "a", a.NamespaceName),
                    new XElement(a + "graphicData", new XAttribute("uri", Ns.PictureUri),
                        new XElement(pic + "pic", new XAttribute(XNamespace.Xmlns + "pic", pic.NamespaceName),
                            new XElement(pic + "nvPicPr",
                                new XElement(pic + "cNvPr", new XAttribute("id", "0"), new XAttribute("name", "Afbeelding " + id)),
                                new XElement(pic + "cNvPicPr")),
                            new XElement(pic + "blipFill",
                                new XElement(a + "blip", new XAttribute(Ns.R + "embed", relId)),
                                new XElement(a + "stretch", new XElement(a + "fillRect"))),
                            new XElement(pic + "spPr",
                                new XElement(a + "xfrm",
                                    new XElement(a + "off", new XAttribute("x", "0"), new XAttribute("y", "0")),
                                    new XElement(a + "ext", new XAttribute("cx", cx), new XAttribute("cy", cy))),
                                new XElement(a + "prstGeom", new XAttribute("prst", "rect"), new XElement(a + "avLst")))))));

            return El("r", El("drawing", inline));
        }

        private string AddMedia(ImageInline image)
        {
            var hash = Convert.ToHexString(SHA256.HashData(image.Data));
            if (_mediaByHash.TryGetValue(hash, out var existing)) return existing;

            var ext = image.Extension.TrimStart('.').ToLowerInvariant();
            if (ext.Length == 0) ext = "png";
            string target, part;
            int n = 1;
            do
            {
                target = $"media/image{n++}.{ext}";
                part = DocxPackage.ResolveTarget(_doc.DocPart, target);
            } while (_pkg.Has(part));

            _pkg.Set(part, image.Data);
            _pkg.EnsureDefaultContentType(ext, ext switch
            {
                "jpg" or "jpeg" => "image/jpeg",
                "gif" => "image/gif",
                "bmp" => "image/bmp",
                "tif" or "tiff" => "image/tiff",
                _ => "image/png",
            });
            var id = _rels.Add(RelTypes.Image, target);
            _mediaByHash[hash] = id;
            return id;
        }

        // ---- tables ------------------------------------------------------------------------

        private XElement WriteTable(TableBlock table)
        {
            var tbl = El("tbl");
            string? styleId;
            if (table.RawTblPr != null)
            {
                tbl.Add(new XElement(table.RawTblPr));
                styleId = table.StyleId;
                if (styleId != null && _styles.Get(styleId) == null) styleId = null;
            }
            else
            {
                styleId = _styles.Get("TableGrid")?.Id ?? _styles.EnsureBuiltIn("Table Grid", "table");
                var tblPr = El("tblPr");
                if (styleId != null) tblPr.Add(Val("tblStyle", styleId));
                tblPr.Add(El("tblW", At("w", "0"), At("type", "auto")));
                tblPr.Add(El("tblLook", At("val", "04A0"), At("firstRow", "1"), At("lastRow", "0"), At("firstColumn", "1"),
                    At("lastColumn", "0"), At("noHBand", "0"), At("noVBand", "1")));
                tbl.Add(tblPr);
            }

            int columns = 0;
            foreach (var row in table.Rows) columns = Math.Max(columns, row.Cells.Sum(c => Math.Max(1, c.ColSpan)));
            // Row spans can push cells further right than the row's own cells suggest.
            columns = Math.Max(columns, MeasureColumns(table));
            var grid = table.Grid.ToList();
            if (grid.Count != columns || grid.Any(w => w <= 0))
            {
                int width = Math.Max(360, _doc.Page.ContentWidth / Math.Max(1, columns));
                grid = Enumerable.Repeat(width, columns).ToList();
            }
            tbl.Add(El("tblGrid", grid.Select(w => El("gridCol", At("w", X.Inv(w))))));

            // remaining[col] = rows still covered by a vertically merged cell that starts at this column
            var remaining = new int[columns + 1];
            var spanOf = new int[columns + 1];
            var prOf = new XElement?[columns + 1];

            foreach (var row in table.Rows)
            {
                var tr = El("tr");
                if (row.RawTrPr != null && row.RawTrPr.HasElements) tr.Add(new XElement(row.RawTrPr));
                int col = 0;

                void Continuations()
                {
                    while (col < columns && remaining[col] > 0)
                    {
                        int span = spanOf[col];
                        tr.Add(El("tc", BuildTcPr(prOf[col], grid, col, span, "continue"), El("p")));
                        remaining[col]--;
                        col += span;
                    }
                }

                foreach (var cell in row.Cells)
                {
                    Continuations();
                    if (col >= columns) break;
                    int span = Math.Clamp(cell.ColSpan, 1, columns - col);
                    bool merged = cell.RowSpan > 1;
                    var tc = El("tc", BuildTcPr(cell.RawTcPr, grid, col, span, merged ? "restart" : null));
                    tc.Add(WriteBlocks(cell.Blocks, styleId, inCell: true));
                    tr.Add(tc);
                    if (merged)
                    {
                        remaining[col] = cell.RowSpan - 1;
                        spanOf[col] = span;
                        prOf[col] = cell.RawTcPr;
                    }
                    col += span;
                }
                Continuations();
                if (!tr.Elements(Ns.W + "tc").Any()) tr.Add(El("tc", BuildTcPr(null, grid, 0, 1, null), El("p")));
                tbl.Add(tr);
            }
            return tbl;
        }

        private static int MeasureColumns(TableBlock table)
        {
            int max = 0;
            var remaining = new List<int>();
            var spans = new List<int>();
            foreach (var row in table.Rows)
            {
                int col = 0;
                void Skip()
                {
                    while (col < remaining.Count && remaining[col] > 0) { remaining[col]--; col += Math.Max(1, spans[col]); }
                }
                foreach (var cell in row.Cells)
                {
                    Skip();
                    int span = Math.Max(1, cell.ColSpan);
                    while (remaining.Count <= col) { remaining.Add(0); spans.Add(1); }
                    if (cell.RowSpan > 1) { remaining[col] = cell.RowSpan - 1; spans[col] = span; }
                    col += span;
                }
                Skip();
                max = Math.Max(max, col);
            }
            return max;
        }

        private static XElement BuildTcPr(XElement? raw, List<int> grid, int col, int span, string? vMerge)
        {
            var children = raw?.Elements().Select(e => new XElement(e)).ToList() ?? new List<XElement>();
            children.RemoveAll(e => e.IsW("tcW") || e.IsW("gridSpan") || e.IsW("vMerge") || e.IsW("hMerge"));
            int width = 0;
            for (int i = col; i < col + span && i < grid.Count; i++) width += grid[i];
            children.Add(El("tcW", At("w", X.Inv(width)), At("type", "dxa")));
            if (span > 1) children.Add(Val("gridSpan", X.Inv(span)));
            if (vMerge == "restart") children.Add(Val("vMerge", "restart"));
            else if (vMerge == "continue") children.Add(El("vMerge"));
            Sort(children, TcPrOrder);
            return El("tcPr", children);
        }
    }
}
