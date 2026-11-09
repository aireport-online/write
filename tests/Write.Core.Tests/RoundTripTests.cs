using System.Xml.Linq;
using Xunit;
using Xunit.Abstractions;
using static Write.Core.Tests.TestHelpers;

namespace Write.Core.Tests;

public class RoundTripTests
{
    private readonly ITestOutputHelper _out;
    public RoundTripTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Fixtures => new[]
    {
        new object[] { "rich.docx" }, new object[] { "pandoc.docx" }, new object[] { "libreoffice.docx" },
    };

    [Fact]
    public void BlankDocumentIsValid()
    {
        var doc = DocModel.CreateBlank();
        Assert.Single(doc.Blocks);
        var bytes = DocxWriter.Write(doc);
        Assert.Empty(Validate(bytes));
        var again = DocxReader.Read(bytes);
        Assert.Equal(11906, again.Page.Width);
        Assert.Equal("Calibri", again.Styles.ResolveRun(null).Font);
        Assert.Equal(22, again.Styles.ResolveRun(null).Size);
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void UnchangedDocumentSurvivesRoundTrip(string name)
    {
        var original = Fixture(name);
        var doc = DocxReader.Read(original);
        var written = DocxWriter.Write(doc);

        var problems = NewProblems(original, written);
        foreach (var p in problems) _out.WriteLine(p);
        Assert.Empty(problems);

        var again = DocxReader.Read(written);
        Assert.Equal(Describe(doc), Describe(again));

        // Writing a second time must give exactly the same body: nothing drifts.
        var second = DocxWriter.Write(again);
        Assert.Equal(Body(written).ToString(SaveOptions.DisableFormatting), Body(second).ToString(SaveOptions.DisableFormatting));
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void EveryOriginalPartIsKept(string name)
    {
        var original = Fixture(name);
        var written = DocxWriter.Write(DocxReader.Read(original));
        using var zip = new System.IO.Compression.ZipArchive(new MemoryStream(original));
        foreach (var entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/')) continue;
            Assert.True(HasPart(written, entry.FullName), "missing part " + entry.FullName);
        }
    }

    [Fact]
    public void RichFixtureIsUnderstood()
    {
        var doc = DocxReader.Read(Fixture("rich.docx"));
        _out.WriteLine(Describe(doc));
        var paras = Paragraphs(doc.Blocks).ToList();

        var h1 = paras.First(p => p.Text == "Hoofdstuk één");
        Assert.Equal(1, doc.Styles.HeadingLevel(h1.Props.StyleId));

        var body = paras.First(p => p.Text.StartsWith("Gewone tekst"));
        var runs = body.Inlines.OfType<TextRun>().ToList();
        Assert.True(runs.Single(r => r.Text == "vet").Props.Bold);
        Assert.True(runs.Single(r => r.Text == "cursief").Props.Italic);
        Assert.True(runs.Single(r => r.Text == "onderstreept").Props.Underline);
        var red = runs.Single(r => r.Text == "rood 14pt");
        Assert.Equal("C00000", red.Props.Color);
        Assert.Equal(28, red.Props.Size);
        Assert.Equal("Arial", red.Props.Font);
        Assert.Equal(VertAlign.Subscript, runs.First(r => r.Text == "2").Props.Vert);
        Assert.Equal(VertAlign.Superscript, runs.Last(r => r.Text == "2").Props.Vert);
        var link = runs.Single(r => r.Text == "een link");
        Assert.Equal("https://example.com/pad?a=1&b=2", link.Link);

        var bullets = paras.Where(p => p.List != null && p.List.Format.IsBullet()).ToList();
        Assert.Equal(3, bullets.Count);
        var numbers = paras.Where(p => p.List is { Format: ListFormat.Decimal }).ToList();
        Assert.Equal(new[] { 1, 2 }, numbers.Select(p => p.List!.Ordinal));

        var centered = paras.First(p => p.Text == "Gecentreerd met afstand");
        Assert.Equal(Align.Center, centered.Props.Align);
        Assert.Equal(240, centered.Props.Before);
        Assert.Equal(120, centered.Props.After);
        Assert.Equal(360, centered.Props.Line);

        var indented = paras.First(p => p.Text.StartsWith("Ingesprongen"));
        Assert.Equal(567, indented.Props.IndLeft);
        Assert.True(indented.Props.IndFirst > 0);

        var table = doc.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(3, table.Grid.Count);
        Assert.Equal(2, table.Rows[0].Cells[0].ColSpan);
        Assert.Equal(2, table.Rows[1].Cells[2].RowSpan);
        Assert.Equal(2, table.Rows[2].Cells.Count);
        Assert.True(table.Rows[0].Cells[0].Top.Visible);

        var image = paras.SelectMany(p => p.Inlines).OfType<ImageInline>().Single();
        Assert.Equal("png", image.Extension);
        Assert.Equal(1371600, image.Cx);

        var after = paras.First(p => p.Text == "Na het pagina-einde");
        Assert.True(after.BreakBefore);

        var field = paras.SelectMany(p => p.Inlines).OfType<RawInline>().Single(r => r.Kind == RawKind.Field);
        Assert.Equal("2", field.Text);
        Assert.Contains(paras, p => p.Text.Contains("Tab\thier"));
    }

    [Fact]
    public void PandocFixtureKeepsFootnoteAndMath()
    {
        var original = Fixture("pandoc.docx");
        var doc = DocxReader.Read(original);
        _out.WriteLine(Describe(doc));
        var inlines = Paragraphs(doc.Blocks).SelectMany(p => p.Inlines).ToList();
        Assert.Contains(inlines, i => i is RawInline { Kind: RawKind.Footnote });
        Assert.Contains(inlines, i => i is RawInline { Kind: RawKind.Math });
        Assert.Contains(inlines, i => i.Link == "https://pandoc.org");

        var written = DocxWriter.Write(doc);
        var body = Body(written);
        Assert.Single(body.Descendants(W + "footnoteReference"));
        Assert.Single(body.Descendants(XName.Get("oMath", "http://schemas.openxmlformats.org/officeDocument/2006/math")));
        Assert.Equal(Part(original, "word/footnotes.xml"), Part(written, "word/footnotes.xml"));
    }

    [Fact]
    public void HeaderAndFooterArePreserved()
    {
        var original = Fixture("rich.docx");
        var written = DocxWriter.Write(DocxReader.Read(original));
        Assert.Equal(Part(original, "word/header1.xml"), Part(written, "word/header1.xml"));
        Assert.Equal(Part(original, "word/footer1.xml"), Part(written, "word/footer1.xml"));
        var sect = Body(written).Element(W + "sectPr")!;
        Assert.NotNull(sect.Element(W + "headerReference"));
        Assert.NotNull(sect.Element(W + "footerReference"));
    }

    [Fact]
    public void UntouchedFormattingIsWrittenVerbatim()
    {
        var original = Fixture("rich.docx");
        var written = DocxWriter.Write(DocxReader.Read(original));
        var before = Body(original).Descendants(W + "rPr").Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList();
        var after = Body(written).Descendants(W + "rPr").Select(e => e.ToString(SaveOptions.DisableFormatting)).ToList();
        Assert.Equal(before, after);
        // keepNext is not something the editor models; it must still be there.
        Assert.Single(Body(written).Descendants(W + "keepNext"));
    }
}
