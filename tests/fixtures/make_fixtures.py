"""Generates the .docx fixtures used by the tests (python-docx, pandoc and LibreOffice)."""
import io, os, struct, subprocess, zlib
from docx import Document
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK
from docx.shared import Pt, RGBColor, Inches, Cm
from docx.oxml import OxmlElement
from docx.oxml.ns import qn

here = os.path.dirname(os.path.abspath(__file__))


def png(width=64, height=40, rgb=(47, 84, 150)):
    raw = b"".join(b"\x00" + bytes(rgb) * width for _ in range(height))
    def chunk(tag, data):
        c = struct.pack(">I", len(data)) + tag + data
        return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)
    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", width, height, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw)) + chunk(b"IEND", b""))


def add_hyperlink(paragraph, url, text):
    r_id = paragraph.part.relate_to(url, "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink", is_external=True)
    link = OxmlElement("w:hyperlink")
    link.set(qn("r:id"), r_id)
    run = OxmlElement("w:r")
    rpr = OxmlElement("w:rPr")
    style = OxmlElement("w:rStyle")
    style.set(qn("w:val"), "Hyperlink")
    rpr.append(style)
    run.append(rpr)
    t = OxmlElement("w:t")
    t.text = text
    run.append(t)
    link.append(run)
    paragraph._p.append(link)


def add_field(paragraph, instr, result):
    def run(child):
        r = OxmlElement("w:r")
        r.append(child)
        paragraph._p.append(r)
    def fld(kind):
        f = OxmlElement("w:fldChar")
        f.set(qn("w:fldCharType"), kind)
        return f
    run(fld("begin"))
    it = OxmlElement("w:instrText")
    it.set(qn("xml:space"), "preserve")
    it.text = instr
    run(it)
    run(fld("separate"))
    t = OxmlElement("w:t")
    t.text = result
    run(t)
    run(fld("end"))


def rich():
    d = Document()
    d.core_properties.title = "Fixture"
    d.add_heading("Hoofdstuk één", level=1)
    p = d.add_paragraph("Gewone tekst met ")
    p.add_run("vet").bold = True
    p.add_run(", ")
    p.add_run("cursief").italic = True
    p.add_run(", ")
    p.add_run("onderstreept").underline = True
    p.add_run(" en ")
    r = p.add_run("rood 14pt")
    r.font.color.rgb = RGBColor(0xC0, 0x00, 0x00)
    r.font.size = Pt(14)
    r.font.name = "Arial"
    p.add_run(". H")
    p.add_run("2").font.subscript = True
    p.add_run("O en x")
    p.add_run("2").font.superscript = True
    p.add_run(" ")
    add_hyperlink(p, "https://example.com/pad?a=1&b=2", "een link")
    p.add_run(".")

    d.add_heading("Lijsten", level=2)
    d.add_paragraph("Eerste punt", style="List Bullet")
    d.add_paragraph("Tweede punt", style="List Bullet")
    d.add_paragraph("Genest punt", style="List Bullet 2")
    d.add_paragraph("Stap één", style="List Number")
    d.add_paragraph("Stap twee", style="List Number")

    c = d.add_paragraph("Gecentreerd met afstand")
    c.alignment = WD_ALIGN_PARAGRAPH.CENTER
    c.paragraph_format.space_before = Pt(12)
    c.paragraph_format.space_after = Pt(6)
    c.paragraph_format.line_spacing = 1.5
    i = d.add_paragraph("Ingesprongen alinea met eerste regel.")
    i.paragraph_format.left_indent = Cm(1)
    i.paragraph_format.first_line_indent = Cm(0.5)
    i.paragraph_format.keep_with_next = True

    d.add_heading("Tabel", level=2)
    t = d.add_table(rows=3, cols=3)
    t.style = "Table Grid"
    for r_i in range(3):
        for c_i in range(3):
            t.cell(r_i, c_i).text = f"r{r_i + 1}k{c_i + 1}"
    t.cell(0, 0).merge(t.cell(0, 1))
    t.cell(1, 2).merge(t.cell(2, 2))

    d.add_heading("Afbeelding", level=2)
    d.add_picture(io.BytesIO(png()), width=Inches(1.5))

    pb = d.add_paragraph("Voor het pagina-einde")
    pb.add_run().add_break(WD_BREAK.PAGE)
    pb.add_run("Na het pagina-einde")

    f = d.add_paragraph("Pagina ")
    add_field(f, " PAGE ", "2")
    f.add_run(" van het document. Tab\thier.")

    section = d.sections[0]
    section.header.paragraphs[0].text = "Koptekst van het document"
    section.footer.paragraphs[0].text = "Voettekst"
    d.save(os.path.join(here, "rich.docx"))


def pandoc():
    md = """# Titel uit pandoc

Een alinea met **vet**, *cursief* en een [link](https://pandoc.org).[^1]

[^1]: Dit is een voetnoot.

- punt a
- punt b
    1. genummerd één
    2. genummerd twee
- punt c

> Een citaat.

| Kolom A | Kolom B |
|---------|---------|
| 1       | 2       |
| 3       | 4       |

Formule: $E = mc^2$ in de tekst.
"""
    src = os.path.join(here, "_pandoc.md")
    with open(src, "w", encoding="utf-8") as fh:
        fh.write(md)
    subprocess.run(["pandoc", src, "-o", os.path.join(here, "pandoc.docx")], check=True)
    os.remove(src)


def libreoffice():
    html = """<html><body><h1>LibreOffice-document</h1>
<p>Tekst met <b>vet</b> en <span style="background: yellow">markering</span>.</p>
<ol><li>een</li><li>twee<ul><li>sub</li></ul></li></ol>
<table border="1"><tr><td>a</td><td>b</td></tr><tr><td colspan="2">breed</td></tr></table>
</body></html>"""
    src = os.path.join(here, "libreoffice.html")
    with open(src, "w", encoding="utf-8") as fh:
        fh.write(html)
    subprocess.run(["soffice", "--headless", "--convert-to", "docx:MS Word 2007 XML", "--outdir", here, src],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    os.remove(src)


if __name__ == "__main__":
    rich()
    pandoc()
    libreoffice()
    print("ok")
