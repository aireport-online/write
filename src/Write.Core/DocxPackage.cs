using System.IO.Compression;
using System.Xml.Linq;

namespace Write.Core;

/// <summary>
/// An OPC (zip) package held fully in memory. Every part of the original file is kept,
/// so parts this application does not understand survive a load/save cycle untouched.
/// </summary>
public sealed class DocxPackage
{
    private readonly Dictionary<string, byte[]> _parts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = new();

    public const string ContentTypesPath = "[Content_Types].xml";

    public IReadOnlyList<string> Paths => _order;

    public static DocxPackage Load(byte[] data)
    {
        var pkg = new DocxPackage();
        using var ms = new MemoryStream(data, writable: false);
        ZipArchive zip;
        try
        {
            zip = new ZipArchive(ms, ZipArchiveMode.Read);
        }
        catch (InvalidDataException ex)
        {
            throw new DocxFormatException("Het bestand is geen geldig .docx-bestand (geen zip-archief).", ex);
        }
        using (zip)
        {
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (name.Length == 0 || name.EndsWith('/')) continue;
                using var es = entry.Open();
                using var buffer = new MemoryStream();
                es.CopyTo(buffer);
                pkg.Set(name, buffer.ToArray());
            }
        }
        return pkg;
    }

    public bool Has(string path) => _parts.ContainsKey(Normalize(path));

    public byte[]? Get(string path) => _parts.TryGetValue(Normalize(path), out var d) ? d : null;

    public void Set(string path, byte[] data)
    {
        path = Normalize(path);
        if (!_parts.ContainsKey(path)) _order.Add(path);
        _parts[path] = data;
    }

    public XDocument? GetXml(string path)
    {
        var data = Get(path);
        if (data == null) return null;
        try
        {
            return X.Parse(data);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new DocxFormatException($"Onderdeel '{path}' bevat ongeldige XML.", ex);
        }
    }

    public void SetXml(string path, XDocument doc) => Set(path, X.Serialize(doc));

    public DocxPackage Clone()
    {
        var c = new DocxPackage();
        foreach (var p in _order) c.Set(p, _parts[p]);
        return c;
    }

    public byte[] Save()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            // [Content_Types].xml conventionally comes first.
            var ordered = _order.OrderBy(p => p.Equals(ContentTypesPath, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
            foreach (var path in ordered)
            {
                var level = IsCompressedMedia(path) ? CompressionLevel.NoCompression : CompressionLevel.Optimal;
                var entry = zip.CreateEntry(path, level);
                using var es = entry.Open();
                var data = _parts[path];
                es.Write(data, 0, data.Length);
            }
        }
        return ms.ToArray();
    }

    private static bool IsCompressedMedia(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".png" or ".jpg" or ".jpeg" or ".gif";
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    /// <summary>Path of the relationships part that belongs to <paramref name="part"/>.</summary>
    public static string RelsPathFor(string part)
    {
        part = Normalize(part);
        int slash = part.LastIndexOf('/');
        var dir = slash >= 0 ? part[..(slash + 1)] : "";
        var file = slash >= 0 ? part[(slash + 1)..] : part;
        return dir + "_rels/" + file + ".rels";
    }

    /// <summary>Resolves a relationship target relative to the part that owns the relationship.</summary>
    public static string ResolveTarget(string basePart, string target)
    {
        if (target.StartsWith('/')) return target.TrimStart('/');
        basePart = Normalize(basePart);
        int slash = basePart.LastIndexOf('/');
        var segments = new List<string>();
        if (slash >= 0) segments.AddRange(basePart[..slash].Split('/'));
        foreach (var seg in target.Split('/'))
        {
            if (seg == "." || seg.Length == 0) continue;
            if (seg == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
            }
            else segments.Add(seg);
        }
        return string.Join('/', segments);
    }

    // ---- content types -------------------------------------------------------------------

    public void EnsureDefaultContentType(string extension, string contentType)
    {
        var doc = GetXml(ContentTypesPath) ?? new XDocument(new XElement(Ns.Ct + "Types"));
        var root = doc.Root!;
        extension = extension.TrimStart('.');
        if (root.Elements(Ns.Ct + "Default").Any(e =>
                string.Equals((string?)e.Attribute("Extension"), extension, StringComparison.OrdinalIgnoreCase)))
            return;
        root.AddFirst(new XElement(Ns.Ct + "Default",
            new XAttribute("Extension", extension), new XAttribute("ContentType", contentType)));
        SetXml(ContentTypesPath, doc);
    }

    public void EnsureOverrideContentType(string part, string contentType)
    {
        var doc = GetXml(ContentTypesPath) ?? new XDocument(new XElement(Ns.Ct + "Types"));
        var root = doc.Root!;
        var partName = "/" + Normalize(part);
        if (root.Elements(Ns.Ct + "Override").Any(e =>
                string.Equals((string?)e.Attribute("PartName"), partName, StringComparison.OrdinalIgnoreCase)))
            return;
        root.Add(new XElement(Ns.Ct + "Override",
            new XAttribute("PartName", partName), new XAttribute("ContentType", contentType)));
        SetXml(ContentTypesPath, doc);
    }
}

/// <summary>The relationships of one package part.</summary>
public sealed class Rels
{
    public sealed record Rel(string Id, string Type, string Target, bool External);

    private readonly List<Rel> _items = new();
    public IReadOnlyList<Rel> Items => _items;
    public bool Dirty { get; private set; }

    public static Rels Parse(XDocument? doc)
    {
        var rels = new Rels();
        if (doc?.Root == null) return rels;
        foreach (var e in doc.Root.Elements(Ns.Rel + "Relationship"))
        {
            var id = (string?)e.Attribute("Id");
            var type = (string?)e.Attribute("Type");
            var target = (string?)e.Attribute("Target");
            if (id == null || type == null || target == null) continue;
            bool external = string.Equals((string?)e.Attribute("TargetMode"), "External", StringComparison.OrdinalIgnoreCase);
            rels._items.Add(new Rel(id, type, target, external));
        }
        return rels;
    }

    public Rel? ById(string? id) => id == null ? null : _items.FirstOrDefault(r => r.Id == id);

    public Rel? ByType(string type) => _items.FirstOrDefault(r => r.Type == type);

    public Rel? Find(string type, string target, bool external) =>
        _items.FirstOrDefault(r => r.Type == type && r.Target == target && r.External == external);

    public string Add(string type, string target, bool external = false)
    {
        int n = _items.Count + 1;
        string id;
        do id = "rId" + n++; while (_items.Any(r => r.Id == id));
        _items.Add(new Rel(id, type, target, external));
        Dirty = true;
        return id;
    }

    public XDocument ToXml()
    {
        var root = new XElement(Ns.Rel + "Relationships");
        foreach (var r in _items)
        {
            var e = new XElement(Ns.Rel + "Relationship",
                new XAttribute("Id", r.Id), new XAttribute("Type", r.Type), new XAttribute("Target", r.Target));
            if (r.External) e.Add(new XAttribute("TargetMode", "External"));
            root.Add(e);
        }
        return new XDocument(root);
    }
}

public sealed class DocxFormatException : Exception
{
    public DocxFormatException(string message, Exception? inner = null) : base(message, inner) { }
}
