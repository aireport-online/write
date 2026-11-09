using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Write.Core;

/// <summary>One border edge of a table or table cell.</summary>
public sealed record BorderSide(bool Visible, double WidthPt, string Color)
{
    public static readonly BorderSide None = new(false, 0, "000000");
    public static readonly BorderSide Thin = new(true, 0.5, "000000");

    internal static BorderSide? Parse(XElement? e)
    {
        if (e == null) return null;
        var val = e.WA("val");
        if (val is null or "nil" or "none") return None;
        double width = (e.WInt("sz") ?? 4) / 8.0;
        var color = Colors.NormalizeHex(e.WA("color")) ?? "000000";
        return new BorderSide(true, Math.Max(0.25, width), color);
    }
}

/// <summary>The six border definitions a table can carry (w:tblBorders).</summary>
public sealed class TableBorders
{
    public BorderSide? Top, Left, Bottom, Right, InsideH, InsideV;

    internal static TableBorders? Parse(XElement? e)
    {
        if (e == null) return null;
        return new TableBorders
        {
            Top = BorderSide.Parse(e.W("top")),
            Left = BorderSide.Parse(e.W("left") ?? e.W("start")),
            Bottom = BorderSide.Parse(e.W("bottom")),
            Right = BorderSide.Parse(e.W("right") ?? e.W("end")),
            InsideH = BorderSide.Parse(e.W("insideH")),
            InsideV = BorderSide.Parse(e.W("insideV")),
        };
    }
}

public sealed class StyleDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>paragraph, character, table or numbering.</summary>
    public string Type { get; init; } = "paragraph";
    public string? BasedOn { get; init; }
    public string? Next { get; init; }
    public string? Link { get; init; }
    public bool IsDefault { get; init; }
    public bool Hidden { get; init; }
    public bool QuickFormat { get; init; }
    public int UiPriority { get; init; } = 99;
    internal ParaPartial PPr = new();
    internal RunPartial RPr = new();
    internal TableBorders? Borders;
    internal XElement Xml = null!;
}

/// <summary>The styles of a document (word/styles.xml) with inheritance resolution.</summary>
public sealed class StyleSheet
{
    private readonly Dictionary<string, StyleDef> _styles = new(StringComparer.Ordinal);
    private readonly List<StyleDef> _ordered = new();
    private ParaPartial _defaultPPr = new();
    private RunPartial _defaultRPr = new();
    private readonly Dictionary<(string?, string?), ParaPartial> _paraCache = new();
    private readonly Dictionary<(string?, string?, string?), RunPartial> _runCache = new();

    internal XDocument Doc { get; private set; } = null!;
    internal Theme? Theme { get; private set; }

    /// <summary>True when styles were added and the styles part must be written back.</summary>
    public bool Dirty { get; internal set; }

    public IReadOnlyList<StyleDef> Styles => _ordered;
    public string? DefaultParagraphStyleId { get; private set; }

    public static StyleSheet Parse(XDocument doc, Theme? theme)
    {
        var sheet = new StyleSheet { Doc = doc, Theme = theme };
        sheet.Index();
        return sheet;
    }

    private void Index()
    {
        _styles.Clear();
        _ordered.Clear();
        _paraCache.Clear();
        _runCache.Clear();
        DefaultParagraphStyleId = null;

        var root = Doc.Root;
        var defaults = root.W("docDefaults");
        _defaultRPr = RunPartial.Parse(defaults.W("rPrDefault").W("rPr"), Theme);
        _defaultPPr = ParaPartial.Parse(defaults.W("pPrDefault").W("pPr"));

        foreach (var e in root.Ws("style"))
        {
            var id = e.WA("styleId");
            if (string.IsNullOrEmpty(id) || _styles.ContainsKey(id)) continue;
            var type = e.WA("type") ?? "paragraph";
            var def = new StyleDef
            {
                Id = id,
                Name = e.W("name").WA("val") ?? id,
                Type = type,
                BasedOn = e.W("basedOn").WA("val"),
                Next = e.W("next").WA("val"),
                Link = e.W("link").WA("val"),
                IsDefault = e.WA("default") is "1" or "true" or "on",
                Hidden = e.W("semiHidden") != null || e.W("hidden") != null,
                QuickFormat = e.W("qFormat") != null,
                UiPriority = e.W("uiPriority").WInt("val") ?? 99,
                PPr = ParaPartial.Parse(e.W("pPr")),
                RPr = RunPartial.Parse(e.W("rPr"), Theme),
                Borders = TableBorders.Parse(e.W("tblPr").W("tblBorders")),
                Xml = e,
            };
            // A style's own pStyle/rStyle is meaningless; ignore it.
            def.PPr.StyleId = null;
            def.RPr.StyleId = null;
            _styles[id] = def;
            _ordered.Add(def);
            if (type == "paragraph" && def.IsDefault && DefaultParagraphStyleId == null) DefaultParagraphStyleId = id;
        }
    }

    public StyleDef? Get(string? id) => id != null && _styles.TryGetValue(id, out var s) ? s : null;

    public StyleDef? FindByName(string name, string type = "paragraph") =>
        _ordered.FirstOrDefault(s => s.Type == type && string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase));

    private IEnumerable<StyleDef> Chain(string? id)
    {
        // Returned base-first so that later entries override earlier ones.
        var chain = new List<StyleDef>();
        var seen = new HashSet<string>();
        while (id != null && _styles.TryGetValue(id, out var s) && seen.Add(id) && chain.Count < 32)
        {
            chain.Add(s);
            id = s.BasedOn;
        }
        chain.Reverse();
        return chain;
    }

    /// <summary>Paragraph formatting contributed by defaults, the table style and the paragraph style.</summary>
    internal ParaPartial ParaChain(string? paraStyleId, string? tableStyleId)
    {
        var key = (paraStyleId, tableStyleId);
        if (_paraCache.TryGetValue(key, out var cached)) return cached;
        var p = _defaultPPr.Clone();
        foreach (var s in Chain(tableStyleId)) p = p.Overlay(s.PPr);
        foreach (var s in Chain(paraStyleId ?? DefaultParagraphStyleId)) p = p.Overlay(s.PPr);
        _paraCache[key] = p;
        return p;
    }

    internal RunPartial RunChain(string? paraStyleId, string? charStyleId, string? tableStyleId)
    {
        var key = (paraStyleId, charStyleId, tableStyleId);
        if (_runCache.TryGetValue(key, out var cached)) return cached;
        var r = _defaultRPr.Clone();
        foreach (var s in Chain(tableStyleId)) r = r.Overlay(s.RPr);
        foreach (var s in Chain(paraStyleId ?? DefaultParagraphStyleId)) r = r.Overlay(s.RPr);
        if (charStyleId != null)
            foreach (var s in Chain(charStyleId)) r = r.Overlay(s.RPr);
        _runCache[key] = r;
        return r;
    }

    /// <summary>The formatting a paragraph gets from its style alone (no direct formatting).</summary>
    public ParaProps ResolvePara(string? paraStyleId, string? tableStyleId = null) =>
        ParaChain(paraStyleId, tableStyleId).ToProps(paraStyleId);

    /// <summary>The formatting a run gets from styles alone (no direct formatting).</summary>
    public RunProps ResolveRun(string? paraStyleId, string? charStyleId = null, string? tableStyleId = null) =>
        RunChain(paraStyleId, charStyleId, tableStyleId).ToProps(charStyleId);

    private static readonly Regex HeadingName = new(@"^heading (\d)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Outline level 1–9 when the style is a heading, otherwise null.</summary>
    public int? HeadingLevel(string? styleId)
    {
        if (styleId == null) return null;
        foreach (var s in Chain(styleId).Reverse())
        {
            var m = HeadingName.Match(s.Name);
            if (m.Success) return int.Parse(m.Groups[1].Value);
        }
        var lvl = ParaChain(styleId, null).OutlineLvl;
        return lvl is >= 0 and <= 8 ? lvl + 1 : null;
    }

    public string? HeadingStyleId(int level) => FindByName("heading " + level)?.Id;

    public TableBorders? TableStyleBorders(string? tableStyleId)
    {
        TableBorders? result = null;
        foreach (var s in Chain(tableStyleId))
            if (s.Borders != null) result = s.Borders;
        return result;
    }

    /// <summary>Numbering a paragraph style applies (w:numPr inside the style), if any.</summary>
    internal string? StyleNumId(string styleId) => Get(styleId)?.PPr.NumId;

    /// <summary>
    /// Makes sure a built-in style (by its invariant name, e.g. "heading 1") exists, copying it
    /// from the default template when the document lacks it. Returns the style id.
    /// </summary>
    public string? EnsureBuiltIn(string name, string type = "paragraph")
    {
        var existing = FindByName(name, type);
        if (existing != null) return existing.Id;
        var template = Template.Styles;
        var source = template.FindByName(name, type);
        if (source == null) return null;
        Import(template, source.Id, new HashSet<string>());
        Index();
        Dirty = true;
        return FindByName(name, type)?.Id;
    }

    private void Import(StyleSheet template, string? id, HashSet<string> visiting)
    {
        if (id == null || !visiting.Add(id)) return;
        var source = template.Get(id);
        if (source == null) return;
        if (_styles.ContainsKey(id) || Doc.Root!.Ws("style").Any(e => e.WA("styleId") == id)) return;
        Doc.Root!.Add(new XElement(source.Xml));
        Import(template, source.BasedOn, visiting);
        Import(template, source.Next, visiting);
        Import(template, source.Link, visiting);
    }

    /// <summary>Paragraph styles to offer in the user interface, most useful first.</summary>
    public IEnumerable<StyleDef> ParagraphStylesForUi() =>
        _ordered.Where(s => s.Type == "paragraph" && (!s.Hidden || s.IsDefault))
            .OrderBy(s => s.IsDefault ? 0 : 1)
            .ThenBy(s => s.UiPriority)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase);
}
