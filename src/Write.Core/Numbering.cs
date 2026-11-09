using System.Xml.Linq;

namespace Write.Core;

public enum ListFormat
{
    Disc, Circle, Square, Decimal, LowerLetter, UpperLetter, LowerRoman, UpperRoman, None,
}

public static class ListFormats
{
    public static bool IsBullet(this ListFormat f) => f is ListFormat.Disc or ListFormat.Circle or ListFormat.Square or ListFormat.None;
}

/// <summary>One level of a numbering definition.</summary>
public sealed record NumLevel(ListFormat Format, int Start, int IndLeft, int Hanging);

/// <summary>Read-only view of word/numbering.xml.</summary>
public sealed class NumberingInfo
{
    private readonly Dictionary<string, Dictionary<int, NumLevel>> _abstracts = new();
    private readonly Dictionary<string, (string AbstractId, Dictionary<int, int> StartOverrides)> _nums = new();

    public static NumberingInfo Parse(XDocument? doc, Func<string, string?>? styleNumId = null)
    {
        var info = new NumberingInfo();
        var root = doc?.Root;
        if (root == null) return info;

        var links = new Dictionary<string, string>();
        foreach (var a in root.Ws("abstractNum"))
        {
            var id = a.WA("abstractNumId");
            if (id == null) continue;
            var levels = new Dictionary<int, NumLevel>();
            foreach (var lvl in a.Ws("lvl"))
            {
                int ilvl = lvl.WInt("ilvl") ?? 0;
                levels[ilvl] = ParseLevel(lvl, ilvl);
            }
            info._abstracts[id] = levels;
            if (levels.Count == 0 && a.W("numStyleLink").WA("val") is { } link) links[id] = link;
        }

        foreach (var n in root.Ws("num"))
        {
            var id = n.WA("numId");
            var abs = n.W("abstractNumId").WA("val");
            if (id == null || abs == null) continue;
            var overrides = new Dictionary<int, int>();
            foreach (var o in n.Ws("lvlOverride"))
            {
                int ilvl = o.WInt("ilvl") ?? 0;
                if (o.W("startOverride").WInt("val") is { } start) overrides[ilvl] = start;
            }
            info._nums[id] = (abs, overrides);
        }

        // An abstractNum that only links to a numbering style takes its levels from that style's list.
        foreach (var (absId, styleId) in links)
        {
            var numId = styleNumId?.Invoke(styleId);
            if (numId != null && info._nums.TryGetValue(numId, out var num) &&
                info._abstracts.TryGetValue(num.AbstractId, out var levels) && levels.Count > 0)
                info._abstracts[absId] = levels;
        }
        return info;
    }

    private static NumLevel ParseLevel(XElement lvl, int ilvl)
    {
        var fmt = lvl.W("numFmt").WA("val") ?? "decimal";
        var text = lvl.W("lvlText").WA("val") ?? "";
        var format = fmt switch
        {
            "bullet" => BulletFormat(text),
            "none" => ListFormat.None,
            "lowerLetter" => ListFormat.LowerLetter,
            "upperLetter" => ListFormat.UpperLetter,
            "lowerRoman" => ListFormat.LowerRoman,
            "upperRoman" => ListFormat.UpperRoman,
            _ => ListFormat.Decimal,
        };
        var ind = lvl.W("pPr").W("ind");
        int left = ind.WInt("left") ?? ind.WInt("start") ?? 720 * (ilvl + 1);
        int hanging = ind.WInt("hanging") ?? 360;
        return new NumLevel(format, lvl.W("start").WInt("val") ?? 1, left, hanging);
    }

    private static ListFormat BulletFormat(string text)
    {
        if (text.Length == 0) return ListFormat.Disc;
        return text[0] switch
        {
            'o' or '○' or '◦' => ListFormat.Circle,
            '' or '§' or '▪' or '■' or '' or '' => ListFormat.Square,
            _ => ListFormat.Disc,
        };
    }

    public bool HasNum(string? numId) => numId != null && _nums.ContainsKey(numId);

    public NumLevel? Level(string? numId, int ilvl)
    {
        if (numId == null || !_nums.TryGetValue(numId, out var num)) return null;
        if (!_abstracts.TryGetValue(num.AbstractId, out var levels)) return null;
        if (!levels.TryGetValue(ilvl, out var level))
        {
            if (levels.Count > 0) return null;
            level = new NumLevel(ListFormat.Decimal, 1, 720 * (ilvl + 1), 360);
        }
        if (num.StartOverrides.TryGetValue(ilvl, out var start)) level = level with { Start = start };
        return level;
    }
}

/// <summary>Adds new list definitions to word/numbering.xml when saving.</summary>
internal sealed class NumberingBuilder
{
    private readonly XDocument _doc;
    private readonly Dictionary<string, string> _abstractBySignature = new();
    private int _nextAbstract;
    private int _nextNum;

    public bool Dirty { get; private set; }
    public XDocument Document => _doc;

    public NumberingBuilder(XDocument? existing)
    {
        _doc = existing ?? new XDocument(new XElement(Ns.W + "numbering", new XAttribute(XNamespace.Xmlns + "w", Ns.W)));
        var root = _doc.Root!;
        _nextAbstract = root.Ws("abstractNum").Select(a => a.WInt("abstractNumId") ?? 0).DefaultIfEmpty(-1).Max() + 1;
        _nextNum = root.Ws("num").Select(n => n.WInt("numId") ?? 0).DefaultIfEmpty(0).Max() + 1;
    }

    /// <summary>Creates a new list (w:num) whose levels use the given formats and returns its numId.</summary>
    public string NewNum(IReadOnlyList<ListFormat> formats, int? start)
    {
        var signature = string.Join(',', formats);
        var root = _doc.Root!;
        if (!_abstractBySignature.TryGetValue(signature, out var abstractId))
        {
            abstractId = X.Inv(_nextAbstract++);
            var abs = BuildAbstract(abstractId, formats);
            var firstNum = root.Ws("num").FirstOrDefault();
            if (firstNum != null) firstNum.AddBeforeSelf(abs);
            else
            {
                var cleanup = root.W("numIdMacAtCleanup");
                if (cleanup != null) cleanup.AddBeforeSelf(abs);
                else root.Add(abs);
            }
            _abstractBySignature[signature] = abstractId;
        }

        var numId = X.Inv(_nextNum++);
        var num = new XElement(Ns.W + "num", new XAttribute(Ns.W + "numId", numId),
            new XElement(Ns.W + "abstractNumId", new XAttribute(Ns.W + "val", abstractId)));
        if (start is { } s && s != 1)
            num.Add(new XElement(Ns.W + "lvlOverride", new XAttribute(Ns.W + "ilvl", "0"),
                new XElement(Ns.W + "startOverride", new XAttribute(Ns.W + "val", X.Inv(s)))));
        var tail = root.W("numIdMacAtCleanup");
        if (tail != null) tail.AddBeforeSelf(num);
        else root.Add(num);
        Dirty = true;
        return numId;
    }

    private static XElement BuildAbstract(string id, IReadOnlyList<ListFormat> formats)
    {
        var abs = new XElement(Ns.W + "abstractNum", new XAttribute(Ns.W + "abstractNumId", id),
            new XElement(Ns.W + "multiLevelType", new XAttribute(Ns.W + "val", "hybridMultilevel")));
        for (int i = 0; i < 9; i++)
        {
            var format = i < formats.Count ? formats[i] : DefaultFormat(formats.Count > 0 ? formats[0] : ListFormat.Disc, i);
            abs.Add(BuildLevel(i, format));
        }
        return abs;
    }

    private static ListFormat DefaultFormat(ListFormat first, int level)
    {
        if (first.IsBullet())
            return (level % 3) switch { 0 => ListFormat.Disc, 1 => ListFormat.Circle, _ => ListFormat.Square };
        return (level % 3) switch { 0 => ListFormat.Decimal, 1 => ListFormat.LowerLetter, _ => ListFormat.LowerRoman };
    }

    private static XElement BuildLevel(int ilvl, ListFormat format)
    {
        XNamespace w = Ns.W;
        string numFmt, text;
        string? font = null;
        switch (format)
        {
            case ListFormat.Disc: numFmt = "bullet"; text = ""; font = "Symbol"; break;
            case ListFormat.Circle: numFmt = "bullet"; text = "o"; font = "Courier New"; break;
            case ListFormat.Square: numFmt = "bullet"; text = ""; font = "Wingdings"; break;
            case ListFormat.None: numFmt = "none"; text = ""; break;
            case ListFormat.LowerLetter: numFmt = "lowerLetter"; text = $"%{ilvl + 1}."; break;
            case ListFormat.UpperLetter: numFmt = "upperLetter"; text = $"%{ilvl + 1}."; break;
            case ListFormat.LowerRoman: numFmt = "lowerRoman"; text = $"%{ilvl + 1}."; break;
            case ListFormat.UpperRoman: numFmt = "upperRoman"; text = $"%{ilvl + 1}."; break;
            default: numFmt = "decimal"; text = $"%{ilvl + 1}."; break;
        }

        var lvl = new XElement(w + "lvl", new XAttribute(w + "ilvl", X.Inv(ilvl)),
            new XElement(w + "start", new XAttribute(w + "val", "1")),
            new XElement(w + "numFmt", new XAttribute(w + "val", numFmt)),
            new XElement(w + "lvlText", new XAttribute(w + "val", text)),
            new XElement(w + "lvlJc", new XAttribute(w + "val", "left")),
            new XElement(w + "pPr",
                new XElement(w + "ind",
                    new XAttribute(w + "left", X.Inv(720 * (ilvl + 1))),
                    new XAttribute(w + "hanging", "360"))));
        if (font != null)
            lvl.Add(new XElement(w + "rPr",
                new XElement(w + "rFonts",
                    new XAttribute(w + "ascii", font), new XAttribute(w + "hAnsi", font),
                    new XAttribute(w + "cs", font), new XAttribute(w + "hint", "default"))));
        return lvl;
    }
}
