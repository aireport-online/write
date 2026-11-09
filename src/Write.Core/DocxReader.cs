using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Write.Core;

/// <summary>Reads a .docx file into a <see cref="DocModel"/>.</summary>
public static class DocxReader
{
    public static DocModel Read(byte[] data)
    {
        var pkg = DocxPackage.Load(data);
        var rootRels = Rels.Parse(pkg.GetXml("_rels/.rels"));
        string? docPart = null;
        var officeDoc = rootRels.Items.FirstOrDefault(r => r.Type == RelTypes.OfficeDocument)
                        ?? rootRels.Items.FirstOrDefault(r => r.Type.EndsWith("/officeDocument", StringComparison.Ordinal));
        if (officeDoc != null) docPart = DocxPackage.ResolveTarget("", officeDoc.Target);
        if (docPart == null || !pkg.Has(docPart))
            docPart = pkg.Has("word/document.xml") ? "word/document.xml" : null;
        if (docPart == null)
            throw new DocxFormatException("Dit bestand bevat geen Word-document.");

        var docXml = pkg.GetXml(docPart)!;
        var root = docXml.Root!;
        if (root.Name != Ns.W + "document")
        {
            if (root.Name.LocalName == "document" && root.Name.NamespaceName.Contains("purl.oclc.org"))
                throw new DocxFormatException("Dit document is opgeslagen als 'Strict Open XML'. Sla het in Word op als gewoon Word-document (.docx) en probeer het opnieuw.");
            throw new DocxFormatException("Dit bestand bevat geen Word-document.");
        }
        var body = root.W("body") ?? throw new DocxFormatException("Het document heeft geen inhoud (w:body ontbreekt).");

        var relsPath = DocxPackage.RelsPathFor(docPart);
        var docRels = Rels.Parse(pkg.GetXml(relsPath));

        Theme? theme = null;
        if (docRels.ByType(RelTypes.Theme) is { External: false } themeRel)
            theme = Theme.Parse(TryXml(pkg, DocxPackage.ResolveTarget(docPart, themeRel.Target)));

        // Styles: every document gets a styles part so that styles can be applied later.
        string stylesPart;
        XDocument? stylesXml = null;
        if (docRels.ByType(RelTypes.Styles) is { External: false } stylesRel)
        {
            stylesPart = DocxPackage.ResolveTarget(docPart, stylesRel.Target);
            stylesXml = TryXml(pkg, stylesPart);
        }
        else stylesPart = DocxPackage.ResolveTarget(docPart, "styles.xml");
        if (stylesXml?.Root == null || stylesXml.Root.Name != Ns.W + "styles")
        {
            stylesXml = X.Parse(Encoding.UTF8.GetBytes(Template.StylesXml));
            pkg.SetXml(stylesPart, stylesXml);
            pkg.EnsureOverrideContentType(stylesPart, Template.StylesContentType);
            if (docRels.ByType(RelTypes.Styles) == null)
            {
                docRels.Add(RelTypes.Styles, "styles.xml");
                pkg.SetXml(relsPath, docRels.ToXml());
            }
        }
        var styles = StyleSheet.Parse(stylesXml, theme);

        XDocument? numberingXml = null;
        if (docRels.ByType(RelTypes.Numbering) is { External: false } numRel)
            numberingXml = TryXml(pkg, DocxPackage.ResolveTarget(docPart, numRel.Target));
        var numbering = NumberingInfo.Parse(numberingXml, styles.StyleNumId);

        var sectPr = body.W("sectPr");
        var page = ReadPage(sectPr);

        var ctx = new Context(pkg, docPart, docRels, styles, numbering, theme);
        var blocks = new List<Block>();
        ctx.ReadBlocks(body.Elements(), null, blocks);
        if (blocks.Count == 0 || blocks[^1] is not Para)
        {
            var props = styles.ResolvePara(null);
            blocks.Add(new Para { Props = props, Loaded = null });
        }

        var rawRoot = new XElement(root.Name, root.Attributes(),
            root.Elements().Where(e => e.Name != Ns.W + "body").Select(e => new XElement(e)));

        return new DocModel
        {
            Blocks = blocks,
            Page = page,
            LoadedPage = page,
            Styles = styles,
            Numbering = numbering,
            Package = pkg,
            DocPart = docPart,
            StylesPart = stylesPart,
            RawSectPr = sectPr != null ? new XElement(sectPr) : null,
            RawRoot = rawRoot,
        };
    }

    private static XDocument? TryXml(DocxPackage pkg, string path)
    {
        try { return pkg.GetXml(path); }
        catch (DocxFormatException) { return null; }
    }

    private static PageSetup ReadPage(XElement? sectPr)
    {
        var size = sectPr.W("pgSz");
        var mar = sectPr.W("pgMar");
        int w = size.WInt("w") ?? 12240, h = size.WInt("h") ?? 15840;
        if (w < 1440 || h < 1440) { w = 12240; h = 15840; }
        return new PageSetup
        {
            Width = w,
            Height = h,
            MarginTop = Math.Abs(mar.WInt("top") ?? 1440),
            MarginRight = Math.Max(0, mar.WInt("right") ?? 1440),
            MarginBottom = Math.Abs(mar.WInt("bottom") ?? 1440),
            MarginLeft = Math.Max(0, mar.WInt("left") ?? 1440),
        };
    }

    /// <summary>Marks the position of a manual page break while a paragraph is being read.</summary>
    private sealed class PageBreakMarker : Inline { }

    private sealed record InlineCtx(string? ParaStyle, string? TableStyle, string? Link, List<string> Bookmarks);

    private sealed class Context
    {
        private readonly DocxPackage _pkg;
        private readonly string _docPart;
        private readonly Rels _rels;
        private readonly StyleSheet _styles;
        private readonly NumberingInfo _numbering;
        private readonly Theme? _theme;
        private readonly Dictionary<string, int[]> _counters = new();
        private int _noteCounter;

        public Context(DocxPackage pkg, string docPart, Rels rels, StyleSheet styles, NumberingInfo numbering, Theme? theme)
        {
            _pkg = pkg; _docPart = docPart; _rels = rels; _styles = styles; _numbering = numbering; _theme = theme;
        }

        public void ReadBlocks(IEnumerable<XElement> elements, string? tableStyle, List<Block> output)
        {
            foreach (var e in elements)
            {
                if (e.Name.Namespace != Ns.W) continue;
                switch (e.Name.LocalName)
                {
                    case "p":
                        ReadParagraph(e, tableStyle, output);
                        break;
                    case "tbl":
                        output.Add(ReadTable(e));
                        break;
                    case "sdt":
                        ReadBlocks(e.W("sdtContent")?.Elements() ?? Enumerable.Empty<XElement>(), tableStyle, output);
                        break;
                    case "customXml":
                    case "ins":
                    case "moveTo":
                        ReadBlocks(e.Elements(), tableStyle, output);
                        break;
                }
            }
        }

        // ---- paragraphs --------------------------------------------------------------------

        private void ReadParagraph(XElement p, string? tableStyle, List<Block> output)
        {
            var pPr = p.W("pPr");
            var direct = ParaPartial.Parse(pPr);
            var styleId = direct.StyleId;
            if (styleId != null && _styles.Get(styleId) == null) styleId = null;
            var chain = _styles.ParaChain(styleId, tableStyle);
            var merged = chain.Overlay(direct);
            var props = merged.ToProps(styleId);

            // List membership (headings keep their numbering as raw properties instead).
            ListRef? list = null;
            var numId = direct.NumId ?? chain.NumId;
            int ilvl = Math.Clamp(direct.Ilvl ?? chain.Ilvl ?? 0, 0, 8);
            bool isHeading = _styles.HeadingLevel(styleId) != null;
            if (!isHeading && numId != null && numId != "0" && _numbering.Level(numId, ilvl) is { } level)
            {
                list = new ListRef { NumId = numId, Level = ilvl, Format = level.Format, Ordinal = NextOrdinal(numId, ilvl, level.Start) };
                // Indentation of list items comes from the numbering level, not from the paragraph.
                props = props with { IndLeft = 0, IndFirst = 0 };
            }

            XElement? raw = null;
            if (pPr != null)
            {
                raw = new XElement(pPr);
                raw.W("pPrChange")?.Remove();
                raw.W("rPr").W("rPrChange")?.Remove();
                if (list != null) raw.W("numPr")?.Remove();
            }

            var bookmarks = new List<string>();
            var inlines = new List<Inline>();
            ReadInlines(p.Elements().ToList(), new InlineCtx(styleId, tableStyle, null, bookmarks), inlines);

            // Split at manual page breaks: the part after a break becomes its own paragraph.
            var segments = new List<List<Inline>> { new() };
            foreach (var inline in inlines)
            {
                if (inline is PageBreakMarker) segments.Add(new List<Inline>());
                else segments[^1].Add(inline);
            }

            for (int i = 0; i < segments.Count; i++)
            {
                bool first = i == 0, last = i == segments.Count - 1;
                if (first && !last && segments[i].Count == 0) continue; // nothing before the first break
                XElement? segRaw = raw != null ? new XElement(raw) : null;
                if (!last) segRaw?.W("sectPr")?.Remove(); // a section break belongs to the paragraph's end
                bool firstEmitted = first || (i == 1 && segments[0].Count == 0);
                var para = new Para
                {
                    Props = props,
                    Loaded = props,
                    RawPPr = segRaw,
                    Inlines = segments[i],
                    List = list,
                    BreakBefore = !first,
                    Bookmarks = firstEmitted ? bookmarks : new List<string>(),
                };
                output.Add(para);
            }
        }

        private int NextOrdinal(string numId, int ilvl, int start)
        {
            if (!_counters.TryGetValue(numId, out var c))
            {
                c = new int[9];
                Array.Fill(c, int.MinValue);
                _counters[numId] = c;
            }
            c[ilvl] = c[ilvl] == int.MinValue ? start : c[ilvl] + 1;
            for (int i = ilvl + 1; i < c.Length; i++) c[i] = int.MinValue;
            return c[ilvl];
        }

        // ---- inline content ----------------------------------------------------------------

        private void ReadInlines(IReadOnlyList<XElement> nodes, InlineCtx ctx, List<Inline> output)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                var n = nodes[i];
                if (n.Name.Namespace == Ns.M)
                {
                    if (n.Name.LocalName is "oMath" or "oMathPara")
                    {
                        var text = string.Concat(n.Descendants(Ns.M + "t").Select(t => t.Value));
                        output.Add(new RawInline
                        {
                            Kind = RawKind.Math, Raw = { new XElement(n) }, Text = text, Link = ctx.Link,
                            Props = _styles.ResolveRun(ctx.ParaStyle, null, ctx.TableStyle),
                        });
                    }
                    continue;
                }
                if (n.Name.Namespace != Ns.W) continue;

                switch (n.Name.LocalName)
                {
                    case "r":
                        if (FieldChars(n).FirstOrDefault() == "begin" && TryReadField(nodes, i, ctx, output, out int end))
                        {
                            i = end;
                            break;
                        }
                        ReadRun(n, ctx, output);
                        break;

                    case "hyperlink":
                    {
                        string? href = null;
                        if ((string?)n.Attribute(Ns.R + "id") is { } rid && _rels.ById(rid) is { } rel) href = rel.Target;
                        if (n.WA("anchor") is { Length: > 0 } anchor) href = (href ?? "") + "#" + anchor;
                        ReadInlines(n.Elements().ToList(), ctx with { Link = href ?? ctx.Link }, output);
                        break;
                    }

                    case "fldSimple":
                    {
                        var instr = n.WA("instr") ?? "";
                        var href = ParseHyperlinkInstruction(instr);
                        if (href != null)
                        {
                            ReadInlines(n.Elements().ToList(), ctx with { Link = href }, output);
                            break;
                        }
                        var firstRun = n.Descendants(Ns.W + "r").FirstOrDefault();
                        output.Add(new RawInline
                        {
                            Kind = RawKind.Field, Raw = { new XElement(n) }, Link = ctx.Link,
                            Text = FieldText(string.Concat(n.Descendants(Ns.W + "t").Select(t => t.Value)), instr),
                            Props = RunPropsOf(firstRun?.W("rPr"), ctx),
                        });
                        break;
                    }

                    case "ins":
                    case "moveTo":
                    case "smartTag":
                    case "customXml":
                    case "dir":
                    case "bdo":
                        ReadInlines(n.Elements().ToList(), ctx, output);
                        break;

                    case "sdt":
                        ReadInlines(n.W("sdtContent")?.Elements().ToList() ?? new List<XElement>(), ctx, output);
                        break;

                    case "bookmarkStart":
                    {
                        var name = n.WA("name");
                        if (!string.IsNullOrEmpty(name) && name != "_GoBack" && !ctx.Bookmarks.Contains(name))
                            ctx.Bookmarks.Add(name);
                        break;
                    }
                }
            }
        }

        private static IEnumerable<string> FieldChars(XElement run) =>
            run.Ws("fldChar").Select(f => f.WA("fldCharType") ?? "");

        /// <summary>
        /// Handles a complex field that starts at nodes[start] and ends within the same paragraph.
        /// Hyperlink fields become links; any other field is kept as an opaque inline.
        /// </summary>
        private bool TryReadField(IReadOnlyList<XElement> nodes, int start, InlineCtx ctx, List<Inline> output, out int end)
        {
            end = -1;
            int depth = 0, separate = -1;
            for (int j = start; j < nodes.Count && end < 0; j++)
            {
                if (!nodes[j].IsW("r")) continue;
                foreach (var type in FieldChars(nodes[j]))
                {
                    if (type == "begin") depth++;
                    else if (type == "separate" && depth == 1 && separate < 0) separate = j;
                    else if (type == "end" && --depth == 0) { end = j; break; }
                }
            }
            if (end < 0) return false;

            int instrEnd = separate >= 0 ? separate : end;
            var instr = new StringBuilder();
            for (int j = start; j <= instrEnd; j++)
                foreach (var t in nodes[j].DescendantsAndSelf(Ns.W + "instrText")) instr.Append(t.Value);

            var href = ParseHyperlinkInstruction(instr.ToString());
            if (href != null && separate >= 0)
            {
                var result = new List<XElement>();
                for (int j = separate + 1; j < end; j++) result.Add(nodes[j]);
                ReadInlines(result, ctx with { Link = href }, output);
                return true;
            }

            var text = new StringBuilder();
            XElement? firstResultRun = null;
            if (separate >= 0)
            {
                for (int j = separate + 1; j < end; j++)
                {
                    foreach (var r in nodes[j].DescendantsAndSelf(Ns.W + "r"))
                    {
                        firstResultRun ??= r;
                        foreach (var c in r.Elements())
                        {
                            if (c.IsW("t")) text.Append(c.Value);
                            else if (c.IsW("tab")) text.Append('\t');
                        }
                    }
                }
            }

            var raw = new RawInline
            {
                Kind = RawKind.Field,
                Text = FieldText(text.ToString(), instr.ToString()),
                Link = ctx.Link,
                Props = RunPropsOf((firstResultRun ?? nodes[start]).W("rPr"), ctx),
            };
            for (int j = start; j <= end; j++) raw.Raw.Add(new XElement(nodes[j]));
            output.Add(raw);
            return true;
        }

        private static string FieldText(string result, string instruction)
        {
            if (result.Length > 0) return result;
            var code = instruction.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            return code == null ? "" : "{" + code + "}";
        }

        private static readonly Regex InstrToken = new(@"\\[A-Za-z]|""[^""]*""|\S+", RegexOptions.CultureInvariant);

        /// <summary>Returns the link target when the field instruction is a HYPERLINK field.</summary>
        internal static string? ParseHyperlinkInstruction(string instruction)
        {
            var tokens = InstrToken.Matches(instruction).Select(m => m.Value).ToList();
            if (tokens.Count == 0 || !tokens[0].Equals("HYPERLINK", StringComparison.OrdinalIgnoreCase)) return null;
            string? url = null, anchor = null;
            for (int i = 1; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Length == 2 && t[0] == '\\')
                {
                    char sw = char.ToLowerInvariant(t[1]);
                    if (sw == 'l' && i + 1 < tokens.Count) anchor = tokens[++i].Trim('"');
                    else if (sw is 'o' or 't' && i + 1 < tokens.Count) i++;
                }
                else url ??= t.Trim('"');
            }
            if (url == null && anchor == null) return null;
            return (url ?? "") + (anchor != null ? "#" + anchor : "");
        }

        private RunProps RunPropsOf(XElement? rPr, InlineCtx ctx)
        {
            var direct = RunPartial.Parse(rPr, _theme);
            var charStyle = direct.StyleId != null && _styles.Get(direct.StyleId) != null ? direct.StyleId : null;
            return _styles.RunChain(ctx.ParaStyle, charStyle, ctx.TableStyle).Overlay(direct).ToProps(charStyle);
        }

        private static XElement? CleanRPr(XElement? rPr)
        {
            if (rPr == null) return null;
            var raw = new XElement(rPr);
            raw.W("rPrChange")?.Remove();
            return raw;
        }

        private void ReadRun(XElement run, InlineCtx ctx, List<Inline> output)
        {
            var rPr = run.W("rPr");
            var props = RunPropsOf(rPr, ctx);
            var raw = CleanRPr(rPr);
            var text = new StringBuilder();

            void Flush()
            {
                if (text.Length == 0) return;
                output.Add(new TextRun { Text = text.ToString(), Props = props, Loaded = props, RawRPr = raw, Link = ctx.Link });
                text.Clear();
            }

            XElement RawRun(XElement content) =>
                raw != null ? new XElement(Ns.W + "r", new XElement(raw), new XElement(content)) : new XElement(Ns.W + "r", new XElement(content));

            foreach (var c in run.Elements())
            {
                if (c.Name.Namespace == Ns.Mc && c.Name.LocalName == "AlternateContent")
                {
                    Flush();
                    output.Add(new RawInline { Kind = RawKind.Object, Raw = { RawRun(c) }, Text = "", Props = props, Link = ctx.Link });
                    continue;
                }
                if (c.Name.Namespace != Ns.W) continue;
                switch (c.Name.LocalName)
                {
                    case "t":
                        text.Append(c.Value);
                        break;
                    case "tab":
                        text.Append('\t');
                        break;
                    case "noBreakHyphen":
                        text.Append('\u2011');
                        break;
                    case "softHyphen":
                        text.Append('\u00AD');
                        break;
                    case "br":
                    case "cr":
                    {
                        var type = c.WA("type");
                        Flush();
                        if (type == "page") output.Add(new PageBreakMarker());
                        else if (type != "column") output.Add(new LineBreakInline { RawRPr = raw, Link = ctx.Link });
                        break;
                    }
                    case "sym":
                    {
                        Flush();
                        var font = c.WA("font");
                        string glyph = "";
                        if (int.TryParse(c.WA("char"), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code) && code is > 0 and < 0xFFFF)
                            glyph = ((char)code).ToString();
                        output.Add(new RawInline { Kind = RawKind.Symbol, Raw = { RawRun(c) }, Text = glyph, Font = font, Props = props, Link = ctx.Link });
                        break;
                    }
                    case "drawing":
                    case "pict":
                    case "object":
                        Flush();
                        output.Add(ReadGraphic(c, RawRun(c), props, ctx));
                        break;
                    case "footnoteReference":
                    case "endnoteReference":
                    {
                        Flush();
                        _noteCounter++;
                        output.Add(new RawInline
                        {
                            Kind = c.Name.LocalName == "footnoteReference" ? RawKind.Footnote : RawKind.Endnote,
                            Raw = { RawRun(c) }, Text = _noteCounter.ToString(CultureInfo.InvariantCulture), Props = props, Link = ctx.Link,
                        });
                        break;
                    }
                }
            }
            Flush();
        }

        private static readonly HashSet<string> RasterExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            "png", "jpg", "jpeg", "gif", "bmp", "tif", "tiff",
        };

        private Inline ReadGraphic(XElement graphic, XElement rawRun, RunProps props, InlineCtx ctx)
        {
            string? embed = null;
            long cx = 0, cy = 0;
            string? description = null;

            if (graphic.IsW("drawing"))
            {
                var data = graphic.Descendants(Ns.A + "graphicData").FirstOrDefault();
                var blip = data?.Descendants(Ns.A + "blip").FirstOrDefault();
                if (data != null && (string?)data.Attribute("uri") == Ns.PictureUri && blip != null)
                    embed = (string?)blip.Attribute(Ns.R + "embed");
                var extent = graphic.Descendants(Ns.WP + "extent").FirstOrDefault();
                cx = X.ParseLong((string?)extent?.Attribute("cx")) ?? 0;
                cy = X.ParseLong((string?)extent?.Attribute("cy")) ?? 0;
                description = (string?)graphic.Descendants(Ns.WP + "docPr").FirstOrDefault()?.Attribute("descr");
            }
            else if (graphic.IsW("pict"))
            {
                var imageData = graphic.Descendants(Ns.V + "imagedata").FirstOrDefault();
                embed = (string?)imageData?.Attribute(Ns.R + "id");
                var style = (string?)imageData?.Parent?.Attribute("style");
                (cx, cy) = ParseVmlSize(style);
            }

            if (embed != null && cx > 0 && cy > 0 && _rels.ById(embed) is { External: false } rel)
            {
                var part = DocxPackage.ResolveTarget(_docPart, rel.Target);
                var ext = Path.GetExtension(part).TrimStart('.').ToLowerInvariant();
                var bytes = _pkg.Get(part);
                if (bytes != null && bytes.Length > 0 && RasterExtensions.Contains(ext))
                {
                    return new ImageInline
                    {
                        Data = bytes, Extension = ext, Cx = cx, Cy = cy, LoadedCx = cx, LoadedCy = cy,
                        RawRun = rawRun, RelId = embed, Description = description, Link = ctx.Link,
                    };
                }
            }
            return new RawInline { Kind = RawKind.Object, Raw = { rawRun }, Text = "", Props = props, Link = ctx.Link };
        }

        private static readonly Regex VmlLength = new(@"(?<name>width|height)\s*:\s*(?<num>[\d.]+)\s*(?<unit>pt|in|cm|mm|px)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static (long cx, long cy) ParseVmlSize(string? style)
        {
            long cx = 0, cy = 0;
            if (style == null) return (0, 0);
            foreach (Match m in VmlLength.Matches(style))
            {
                if (!double.TryParse(m.Groups["num"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) continue;
                double points = m.Groups["unit"].Value.ToLowerInvariant() switch
                {
                    "in" => v * 72, "cm" => v / 2.54 * 72, "mm" => v / 25.4 * 72, "px" => v * 0.75, _ => v,
                };
                long emu = (long)Math.Round(points * 12700);
                if (m.Groups["name"].Value.Equals("width", StringComparison.OrdinalIgnoreCase)) cx = emu; else cy = emu;
            }
            return (cx, cy);
        }

        // ---- tables ------------------------------------------------------------------------

        private static IEnumerable<XElement> Unwrap(XElement parent, string name)
        {
            foreach (var e in parent.Elements())
            {
                if (e.IsW(name)) yield return e;
                else if (e.IsW("sdt") && e.W("sdtContent") is { } content)
                    foreach (var inner in Unwrap(content, name)) yield return inner;
                else if (e.IsW("customXml") || e.IsW("ins") || e.IsW("moveTo"))
                    foreach (var inner in Unwrap(e, name)) yield return inner;
            }
        }

        private TableBlock ReadTable(XElement tbl)
        {
            var tblPr = tbl.W("tblPr");
            var table = new TableBlock { RawTblPr = tblPr != null ? new XElement(tblPr) : null };
            table.RawTblPr?.W("tblPrChange")?.Remove();
            var styleId = table.StyleId;
            if (styleId != null && _styles.Get(styleId) == null) styleId = null;

            foreach (var col in tbl.W("tblGrid").Ws("gridCol"))
                table.Grid.Add(Math.Max(0, col.WInt("w") ?? 0));

            var borders = TableBorders.Parse(tblPr.W("tblBorders")) ?? _styles.TableStyleBorders(styleId);

            // Cells that are continued by vertical merges in later rows, by grid column.
            var open = new Dictionary<int, TableCellModel>();
            var positions = new List<(TableCellModel Cell, int Row, int Col, TableBorders? Own)>();
            int rowIndex = 0, columnCount = table.Grid.Count;

            foreach (var tr in Unwrap(tbl, "tr"))
            {
                var trPr = tr.W("trPr");
                var row = new TableRowModel { RawTrPr = trPr != null ? new XElement(trPr) : null };
                int col = 0;
                int gridBefore = trPr.W("gridBefore").WInt("val") ?? 0;
                if (row.RawTrPr != null)
                {
                    foreach (var name in new[] { "gridBefore", "gridAfter", "wBefore", "wAfter", "trPrChange", "ins", "del" })
                        row.RawTrPr.W(name)?.Remove();
                }
                if (gridBefore > 0)
                {
                    var filler = new TableCellModel
                    {
                        ColSpan = gridBefore, Top = BorderSide.None, Left = BorderSide.None, Bottom = BorderSide.None, Right = BorderSide.None,
                        Blocks = { new Para { Props = _styles.ResolvePara(null, styleId) } },
                    };
                    row.Cells.Add(filler);
                    col += gridBefore;
                }

                foreach (var tc in Unwrap(tr, "tc"))
                {
                    var tcPr = tc.W("tcPr");
                    int span = Math.Max(1, tcPr.W("gridSpan").WInt("val") ?? 1);
                    var vMerge = tcPr.W("vMerge");
                    bool continues = vMerge != null && vMerge.WA("val") is null or "continue";
                    if (vMerge != null && continues && open.TryGetValue(col, out var origin))
                    {
                        origin.RowSpan++;
                        col += span;
                        continue;
                    }

                    var cell = new TableCellModel { ColSpan = span, RawTcPr = tcPr != null ? new XElement(tcPr) : null };
                    if (cell.RawTcPr != null)
                        foreach (var name in new[] { "tcW", "gridSpan", "vMerge", "hMerge", "tcPrChange", "cellIns", "cellDel", "cellMerge" })
                            cell.RawTcPr.W(name)?.Remove();
                    var fill = Colors.NormalizeHex(tcPr.W("shd").WA("fill"));
                    cell.Shade = fill;
                    ReadBlocks(tc.Elements(), styleId, cell.Blocks);
                    if (cell.Blocks.Count == 0) cell.Blocks.Add(new Para { Props = _styles.ResolvePara(null, styleId) });

                    if (vMerge != null && !continues) open[col] = cell;
                    else open.Remove(col);

                    positions.Add((cell, rowIndex, col, TableBorders.Parse(tcPr.W("tcBorders"))));
                    row.Cells.Add(cell);
                    col += span;
                }
                columnCount = Math.Max(columnCount, col);
                table.Rows.Add(row);
                rowIndex++;
            }

            // Make the grid consistent with the widest row.
            while (table.Grid.Count < columnCount) table.Grid.Add(0);
            if (table.Grid.Any(w => w <= 0))
            {
                int known = table.Grid.Where(w => w > 0).DefaultIfEmpty(0).Sum();
                int missing = table.Grid.Count(w => w <= 0);
                int fallback = Math.Max(360, (9000 - known) / Math.Max(1, missing));
                for (int i = 0; i < table.Grid.Count; i++)
                    if (table.Grid[i] <= 0) table.Grid[i] = fallback;
            }

            int rowCount = table.Rows.Count;
            foreach (var (cell, r, c, own) in positions)
            {
                bool top = r == 0, left = c == 0;
                bool bottom = r + cell.RowSpan >= rowCount, right = c + cell.ColSpan >= columnCount;
                cell.Top = own?.Top ?? (top ? borders?.Top : borders?.InsideH) ?? BorderSide.None;
                cell.Bottom = own?.Bottom ?? (bottom ? borders?.Bottom : borders?.InsideH) ?? BorderSide.None;
                cell.Left = own?.Left ?? (left ? borders?.Left : borders?.InsideV) ?? BorderSide.None;
                cell.Right = own?.Right ?? (right ? borders?.Right : borders?.InsideV) ?? BorderSide.None;
            }
            return table;
        }
    }
}
