"""Generates the fictitious XFA samples used by the converter demo.

expense-claim-xfa.pdf
    A dynamic XFA form: the form exists only in the XFA template and data packets.
    The PDF page shows the placeholder message that viewers without XFA support
    display, and the AcroForm dictionary has no fields.

expense-claim-flattened.pdf
    The same form drawn as static page content (labels, boxes, no fields), as an XFA
    viewer would print it. Use it with the converter's --background option.

The template is hand-written for this demo. All names and amounts are invented.

Requirements: pip install reportlab pypdf
Usage:        python generate_samples.py [output-folder]
"""

import io
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

from pypdf import PdfReader, PdfWriter
from pypdf.generic import ArrayObject, BooleanObject, DictionaryObject, NameObject, StreamObject, TextStringObject
from reportlab.lib.pagesizes import letter
from reportlab.pdfgen import canvas


def caption(text, reserve=None, placement=None):
    attrs = ""
    if reserve:
        attrs += f' reserve="{reserve}"'
    if placement:
        attrs += f' placement="{placement}"'
    return f'<caption{attrs}><font typeface="Helvetica" size="9pt"/><value><text>{text}</text></value></caption>'


def draw(name, x, y, w, h, text, size="9pt", weight="normal"):
    return (f'<draw name="{name}" x="{x}" y="{y}" w="{w}" h="{h}"><ui><textEdit/></ui>'
            f'<value><text>{text}</text></value><font typeface="Helvetica" size="{size}" weight="{weight}"/></draw>')


def text_field(name, x, y, w, h, cap=None, reserve=None, placement=None, ui="<textEdit/>", extra=""):
    c = caption(cap, reserve, placement) if cap else ""
    return (f'<field name="{name}" x="{x}" y="{y}" w="{w}" h="{h}"{extra}><ui>{ui}</ui>'
            f'<font typeface="Helvetica" size="10pt"/>{c}</field>')


def choice_field(name, x, y, w, h, items, cap=None, reserve=None):
    c = caption(cap, reserve) if cap else ""
    item_xml = "".join(f"<text>{i}</text>" for i in items)
    return (f'<field name="{name}" x="{x}" y="{y}" w="{w}" h="{h}"><ui><choiceList/></ui>'
            f'<font typeface="Helvetica" size="10pt"/>{c}<items>{item_xml}</items></field>')


def check_field(name, x, y, w, h, cap, on_value, round_shape=False, off_value=None):
    shape = ' shape="round"' if round_shape else ""
    items = f"<integer>{on_value}</integer>" + (f"<integer>{off_value}</integer>" if off_value is not None else "")
    return (f'<field name="{name}" x="{x}" y="{y}" w="{w}" h="{h}"><ui><checkButton{shape} size="10pt"/></ui>'
            f'{caption(cap, f"{float(w[:-2]) - 0.3:.2f}in", "right")}<items>{items}</items></field>')


expense_rows = "".join(
    text_field(f"Description{i}", "0in", f"{0.6 + (i - 1) * 0.4:.1f}in", "4in", "0.3in")
    + choice_field(f"Category{i}", "4.2in", f"{0.6 + (i - 1) * 0.4:.1f}in", "1.5in", "0.3in",
                   ["Travel", "Lodging", "Meals", "Other"])
    + text_field(f"Amount{i}", "5.9in", f"{0.6 + (i - 1) * 0.4:.1f}in", "1.6in", "0.3in", ui="<numericEdit/>")
    for i in (1, 2, 3))

TEMPLATE = f"""<template xmlns="http://www.xfa.org/schema/xfa-template/3.3/">
<subform name="form1" layout="tb" locale="en_US">
<pageSet>
<pageArea name="Page1" id="Page1">
<contentArea x="0.5in" y="0.5in" w="7.5in" h="10in"/>
<medium stock="letter" short="8.5in" long="11in"/>
</pageArea>
</pageSet>
<subform name="ClaimForm" w="7.5in" h="10in">
{draw("Title", "0in", "0in", "7.5in", "0.45in", "Employee Expense Claim", "18pt", "bold")}
{draw("Subtitle", "0in", "0.45in", "7.5in", "0.3in", "Fictitious sample form for the XFA to AcroForm demo. All data is invented.")}
<subform name="Employee" x="0in" y="0.95in" w="7.5in" h="1.5in">
{draw("Heading", "0in", "0in", "7.5in", "0.3in", "1. Employee", "12pt", "bold")}
<field name="FullName" x="0in" y="0.35in" w="4.6in" h="0.3in"><ui><textEdit/></ui><font typeface="Helvetica" size="10pt"/>{caption("Full name", "1.2in")}<validate nullTest="error"/><assist><toolTip>Employee full name</toolTip></assist></field>
{text_field("EmployeeId", "4.8in", "0.35in", "2.7in", "0.3in", "Employee ID", "1in")}
{choice_field("Department", "0in", "0.8in", "4.6in", "0.3in", ["Finance", "Operations", "Sales", "Engineering"], "Department", "1.2in")}
{text_field("ClaimDate", "4.8in", "0.8in", "2.7in", "0.3in", "Claim date", "1in", ui="<dateTimeEdit/>")}
<field name="InternalRef" x="0in" y="1.2in" w="2in" h="0.3in" presence="hidden"><ui><textEdit/></ui></field>
</subform>
<subform name="Expenses" x="0in" y="2.6in" w="7.5in" h="2.3in">
{draw("Heading", "0in", "0in", "7.5in", "0.3in", "2. Expenses", "12pt", "bold")}
{draw("ColDescription", "0in", "0.35in", "4in", "0.25in", "Description", "9pt", "bold")}
{draw("ColCategory", "4.2in", "0.35in", "1.5in", "0.25in", "Category", "9pt", "bold")}
{draw("ColAmount", "5.9in", "0.35in", "1.6in", "0.25in", "Amount (USD)", "9pt", "bold")}
{expense_rows}
{text_field("Total", "4.2in", "1.85in", "3.3in", "0.3in", "Total (USD)", "1.7in", ui="<numericEdit/>", extra=' access="readOnly"')}
</subform>
<subform name="Payment" x="0in" y="5.05in" w="7.5in" h="1.3in">
{draw("Heading", "0in", "0in", "7.5in", "0.3in", "3. Payment", "12pt", "bold")}
<exclGroup name="Method" x="0in" y="0.35in" w="7.5in" h="0.3in">
{check_field("BankTransfer", "0in", "0in", "2in", "0.3in", "Bank transfer", 1, True)}
{check_field("Cheque", "2.2in", "0in", "1.6in", "0.3in", "Cheque", 2, True)}
{check_field("Payroll", "4in", "0in", "2.2in", "0.3in", "Next payroll", 3, True)}
</exclGroup>
{check_field("AdvanceReceived", "0in", "0.8in", "3in", "0.3in", "Cash advance received", 1, off_value=0)}
</subform>
<subform name="Notes" x="0in" y="6.5in" w="7.5in" h="1.5in">
{text_field("Comments", "0in", "0in", "7.5in", "1.3in", "Comments", "0.3in", "top", ui='<textEdit multiLine="1"/>')}
</subform>
<subform name="Approval" x="0in" y="8.2in" w="7.5in" h="1.2in">
{draw("Heading", "0in", "0in", "7.5in", "0.3in", "4. Approval", "12pt", "bold")}
{text_field("EmployeeSignature", "0in", "0.35in", "3.6in", "0.65in", "Employee signature", "0.25in", "top", ui="<signature/>")}
{text_field("ApprovedBy", "3.9in", "0.35in", "3.6in", "0.65in", "Approved by (manager)", "0.25in", "top")}
</subform>
</subform>
</subform>
</template>"""

DATASETS = """<xfa:datasets xmlns:xfa="http://www.xfa.org/schema/xfa-data/1.0/">
<xfa:data>
<form1><ClaimForm>
<Employee><FullName>Jordan Sample</FullName><EmployeeId>E-1024</EmployeeId><Department>Operations</Department><ClaimDate>2026-03-14</ClaimDate><InternalRef>SAMPLE-0001</InternalRef></Employee>
<Expenses>
<Description1>Train ticket, client workshop</Description1><Category1>Travel</Category1><Amount1>86.40</Amount1>
<Description2>Hotel, one night</Description2><Category2>Lodging</Category2><Amount2>142.00</Amount2>
<Description3/><Category3/><Amount3/>
<Total>228.40</Total>
</Expenses>
<Payment><Method>1</Method><AdvanceReceived>0</AdvanceReceived></Payment>
<Notes><Comments>Sample data. Receipts are attached to the original claim.</Comments></Notes>
<Approval><EmployeeSignature/><ApprovedBy/></Approval>
</ClaimForm></form1>
</xfa:data>
</xfa:datasets>"""

CONFIG = """<config xmlns="http://www.xfa.org/schema/xci/3.0/">
<present><pdf><version>1.7</version><interactive>1</interactive></pdf><xdp><packets>*</packets></xdp></present>
</config>"""

PLACEHOLDER = [
    "Please wait...",
    "",
    "This document is a dynamic XFA form. Its fields and layout are stored in XML",
    "packets that most PDF viewers, browsers and mobile apps do not read.",
    "If this message stays on screen, your viewer cannot display this type of form.",
]


def placeholder_pdf():
    buf = io.BytesIO()
    c = canvas.Canvas(buf, pagesize=letter)
    c.setTitle("Employee Expense Claim (sample XFA form)")
    c.setAuthor("Sample generator")
    y = 720
    for i, line in enumerate(PLACEHOLDER):
        c.setFont("Helvetica-Bold" if i == 0 else "Helvetica", 18 if i == 0 else 11)
        c.drawString(72, y, line)
        y -= 28 if i == 0 else 16
    c.showPage()
    c.save()
    return buf.getvalue()


def build_xfa_pdf(path):
    writer = PdfWriter(clone_from=PdfReader(io.BytesIO(placeholder_pdf())))
    packets = [
        ("preamble", '<xdp:xdp xmlns:xdp="http://ns.adobe.com/xdp/">'),
        ("config", CONFIG),
        ("template", TEMPLATE),
        ("datasets", DATASETS),
        ("postamble", "</xdp:xdp>"),
    ]
    xfa = ArrayObject()
    for name, xml in packets:
        stream = StreamObject()
        stream.set_data(xml.encode("utf-8"))
        stream = stream.flate_encode()
        xfa.append(TextStringObject(name))
        xfa.append(writer._add_object(stream))
    writer._root_object[NameObject("/AcroForm")] = DictionaryObject({
        NameObject("/Fields"): ArrayObject(),
        NameObject("/XFA"): xfa,
    })
    writer._root_object[NameObject("/NeedsRendering")] = BooleanObject(True)
    with open(path, "wb") as f:
        writer.write(f)


# ---------------------------------------------------------------------------------
# Flattened rendering: positioned layout only, which is all this template uses.
# ---------------------------------------------------------------------------------
NS = "{http://www.xfa.org/schema/xfa-template/3.3/}"


def pt(value, default=0.0):
    if not value:
        return default
    for unit, factor in (("in", 72.0), ("mm", 72 / 25.4), ("cm", 72 / 2.54), ("pt", 1.0)):
        if value.endswith(unit):
            return float(value[:-len(unit)]) * factor
    return float(value) * 72.0


def render_flattened(path):
    root = ET.fromstring(TEMPLATE)
    page_h = letter[1]
    c = canvas.Canvas(str(path), pagesize=letter)
    c.setTitle("Employee Expense Claim (flattened rendering)")
    c.setAuthor("Sample generator")
    content = root.find(f".//{NS}contentArea")
    ox, oy = pt(content.get("x")), pt(content.get("y"))

    def text_of(el):
        t = el.find(f"{NS}value/{NS}text")
        return t.text if t is not None and t.text else ""

    def font_of(el, default=9.0):
        f = el.find(f"{NS}font")
        size = pt(f.get("size"), default) if f is not None and f.get("size") else default
        bold = f is not None and f.get("weight") == "bold"
        return ("Helvetica-Bold" if bold else "Helvetica"), size

    def walk(el, x0, y0):
        for child in el:
            tag = child.tag.replace(NS, "")
            if tag in ("pageSet",):
                continue
            x = x0 + pt(child.get("x"))
            y = y0 + pt(child.get("y"))
            w, h = pt(child.get("w")), pt(child.get("h"))
            if tag in ("subform", "exclGroup"):
                walk(child, x, y)
            elif tag == "draw":
                name, size = font_of(child)
                c.setFont(name, size)
                c.drawString(x, page_h - y - size, text_of(child))
            elif tag == "field" and child.get("presence") != "hidden":
                cap = child.find(f"{NS}caption")
                ui = child.find(f"{NS}ui")[0].tag.replace(NS, "")
                bx, by, bw, bh = x, y, w, h
                if cap is not None:
                    reserve = pt(cap.get("reserve"))
                    placement = cap.get("placement", "left")
                    c.setFont("Helvetica", 9)
                    if placement == "top":
                        c.drawString(x, page_h - y - 10, text_of(cap))
                        by, bh = y + reserve, h - reserve
                    elif placement == "right":
                        bw = w - reserve
                        c.drawString(x + bw + 4, page_h - y - h / 2 - 3, text_of(cap))
                    else:
                        c.drawString(x, page_h - y - h / 2 - 3, text_of(cap))
                        bx, bw = x + reserve, w - reserve
                c.setStrokeColorRGB(0.45, 0.45, 0.45)
                if ui == "checkButton":
                    s = 10
                    cy = page_h - y - h / 2
                    if child.find(f"{NS}ui/{NS}checkButton").get("shape") == "round":
                        c.circle(bx + s / 2, cy, s / 2)
                    else:
                        c.rect(bx, cy - s / 2, s, s)
                else:
                    c.rect(bx, page_h - by - bh, bw, bh)
                c.setStrokeColorRGB(0, 0, 0)

    walk(root.find(f"{NS}subform"), ox, oy)
    c.showPage()
    c.save()


def main():
    out = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).parent
    out.mkdir(parents=True, exist_ok=True)
    build_xfa_pdf(out / "expense-claim-xfa.pdf")
    render_flattened(out / "expense-claim-flattened.pdf")
    print(f"Wrote {out / 'expense-claim-xfa.pdf'} and {out / 'expense-claim-flattened.pdf'}")


if __name__ == "__main__":
    main()
