using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;

namespace TickRiver.Core;

public static class ExcelExport
{
    private static readonly XNamespace _s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private static readonly XNamespace _r = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private static readonly XNamespace _p = "http://schemas.openxmlformats.org/package/2006/relationships";
    private static readonly XNamespace _c = "http://schemas.openxmlformats.org/drawingml/2006/chart";
    private static readonly XNamespace _a = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private static readonly XNamespace _d = "http://schemas.openxmlformats.org/drawingml/2006/spreadsheetDrawing";

    public static void Save(Report report, string path)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var zip = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var months = report.Days.GroupBy(d => new { d.Day.Year, d.Day.Month }).ToArray();
                XNamespace ct = "http://schemas.openxmlformats.org/package/2006/content-types";
                var types = new XElement(ct + "Types",
                    new XElement(ct + "Default", new XAttribute("Extension", "rels"), new XAttribute("ContentType", "application/vnd.openxmlformats-package.relationships+xml")),
                    new XElement(ct + "Default", new XAttribute("Extension", "xml"), new XAttribute("ContentType", "application/xml")));
                foreach (var part in new[] { ("workbook.xml", "sheet.main"), ("worksheets/sheet1.xml", "worksheet"),
                    ("worksheets/sheet2.xml", "worksheet"), ("styles.xml", "styles") })
                    types.Add(new XElement(ct + "Override", new XAttribute("PartName", "/xl/" + part.Item1),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml." + part.Item2 + "+xml")));
                types.Add(new XElement(ct + "Override", new XAttribute("PartName", "/xl/drawings/drawing1.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawing+xml")));
                for (var i = 1; i <= months.Length; i++) types.Add(new XElement(ct + "Override", new XAttribute("PartName", $"/xl/charts/chart{i}.xml"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.drawingml.chart+xml")));
                Write(zip, "[Content_Types].xml", types);
                Write(zip, "_rels/.rels", new XElement(_p + "Relationships", Rel("rId1", "officeDocument", "xl/workbook.xml")));
                Write(zip, "xl/workbook.xml", new XElement(_s + "workbook", new XAttribute(XNamespace.Xmlns + "r", _r),
                    new XElement(_s + "sheets", Sheet("Entries", 1), Sheet("Daily summary", 2))));
                Write(zip, "xl/_rels/workbook.xml.rels", new XElement(_p + "Relationships", Rel("rId1", "worksheet", "worksheets/sheet1.xml"),
                    Rel("rId2", "worksheet", "worksheets/sheet2.xml"), Rel("rId3", "styles", "styles.xml")));
                Write(zip, "xl/styles.xml", Styles());

                var entryRows = new XElement(_s + "sheetData");
                entryRows.Add(Row(1, Text(report.Title)));
                entryRows.Add(Row(2, Text(report.RangeLabel), Text("Total"), Number(report.Seconds / 86400m, 2), Number(report.Seconds / 3600m, 3)));
                entryRows.Add(Row(4, new[] { "Date", "Client", "Project", "Description", "Start", "End", "Duration (h:mm:ss)", "Decimal hours", "Currency", "Hourly rate", "Amount", "Billable", "Invoiced" }.Select(t => Text(t, 1)).ToArray()));
                var rowIndex = 5;
                foreach (var row in report.Rows)
                    entryRows.Add(Row(rowIndex++, Text(row.DateLabel), Text(row.Entry.ClientName), Text(row.Entry.ProjectName), Text(row.Description),
                        Text(row.Start.ToString("yyyy-MM-dd HH:mm:ss zzz")), Text(row.End.ToString("yyyy-MM-dd HH:mm:ss zzz")),
                        Number(row.Seconds / 86400m, 2), Number(row.DecimalHours, 3), Text(row.Entry.Currency), Number(row.Entry.Rate, 4),
                        Number(row.Amount, 4), Text(row.Entry.Billable ? "Yes" : "No"), Text(row.Entry.Invoiced ? "Yes" : "No")));
                Write(zip, "xl/worksheets/sheet1.xml", Worksheet(entryRows, new[] { 14d, 24, 26, 44, 30, 30, 24, 20, 12, 16, 18, 14, 14 }, true));

                var dailyRows = new XElement(_s + "sheetData", Row(1, Text(report.Title)), Row(2, Text(report.RangeLabel)),
                    Row(3, Text("Total hours"), Number(report.Seconds / 3600m, 3), Number(report.Seconds / 86400m, 2)),
                    Row(5, Text("Date", 1), Text("Decimal hours", 1), Text("Duration (h:mm:ss)", 1)));
                rowIndex = 6;
                foreach (var day in report.Days) dailyRows.Add(Row(rowIndex++, Text(day.Day.ToString("yyyy-MM-dd")), Number(day.Hours, 3), Number(day.Seconds / 86400m, 2)));
                var summary = Worksheet(dailyRows, new[] { 16d, 20, 24 }, false);
                summary.Add(new XElement(_s + "drawing", new XAttribute(_r + "id", "rId1")));
                Write(zip, "xl/worksheets/sheet2.xml", summary);
                Write(zip, "xl/worksheets/_rels/sheet2.xml.rels", new XElement(_p + "Relationships", Rel("rId1", "drawing", "../drawings/drawing1.xml")));
                var drawing = new XElement(_d + "wsDr", new XAttribute(XNamespace.Xmlns + "a", _a));
                var relationships = new XElement(_p + "Relationships");
                var firstDataRow = 6;
                for (var i = 0; i < months.Length; i++)
                {
                    var data = months[i].ToArray();
                    var id = i + 1;
                    Write(zip, $"xl/charts/chart{id}.xml", Chart(data, firstDataRow, data[0].Day.ToString("MMMM yyyy")));
                    firstDataRow += data.Length;
                    relationships.Add(Rel($"rId{id}", "chart", $"../charts/chart{id}.xml"));
                    drawing.Add(new XElement(_d + "twoCellAnchor", Marker("from", 4, i * 17 + 1), Marker("to", 17, i * 17 + 17),
                        new XElement(_d + "graphicFrame", new XAttribute("macro", ""),
                            new XElement(_d + "nvGraphicFramePr", new XElement(_d + "cNvPr", new XAttribute("id", id), new XAttribute("name", $"Daily hours {id}")), new XElement(_d + "cNvGraphicFramePr")),
                            new XElement(_d + "xfrm", new XElement(_a + "off", new XAttribute("x", 0), new XAttribute("y", 0)), new XElement(_a + "ext", new XAttribute("cx", 0), new XAttribute("cy", 0))),
                            new XElement(_a + "graphic", new XElement(_a + "graphicData", new XAttribute("uri", _c.NamespaceName),
                                new XElement(_c + "chart", new XAttribute(_r + "id", $"rId{id}"))))), new XElement(_d + "clientData")));
                }
                Write(zip, "xl/drawings/drawing1.xml", drawing);
                Write(zip, "xl/drawings/_rels/drawing1.xml.rels", relationships);
            }
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static XElement Text(string value, int style = 0) => new(_s + "c", new XAttribute("t", "inlineStr"), new XAttribute("s", style),
        new XElement(_s + "is", new XElement(_s + "t", new XAttribute(XNamespace.Xml + "space", "preserve"),
            string.Concat(value.EnumerateRunes().Where(r => r.Value >= 0x10000 || System.Xml.XmlConvert.IsXmlChar((char)r.Value)).Select(r => r.ToString())))));
    private static XElement Number(decimal value, int style) => new(_s + "c", new XAttribute("s", style), new XElement(_s + "v", value.ToString(CultureInfo.InvariantCulture)));
    private static XElement Row(int index, params XElement[] cells)
    {
        for (var i = 0; i < cells.Length; i++) cells[i].SetAttributeValue("r", Column(i) + index);
        return new XElement(_s + "row", new XAttribute("r", index), cells);
    }
    private static string Column(int index) => index < 26 ? ((char)('A' + index)).ToString() : "A" + (char)('A' + index - 26);
    private static XElement Sheet(string name, int id) => new(_s + "sheet", new XAttribute("name", name), new XAttribute("sheetId", id), new XAttribute(_r + "id", $"rId{id}"));
    private static XElement Rel(string id, string type, string target) => new(_p + "Relationship", new XAttribute("Id", id),
        new XAttribute("Type", _r.NamespaceName + "/" + type), new XAttribute("Target", target));
    private static void Write(ZipArchive zip, string name, XElement xml)
    {
        using var stream = zip.CreateEntry(name).Open(); new XDocument(new XDeclaration("1.0", "utf-8", "yes"), xml).Save(stream);
    }
    private static XElement Worksheet(XElement rows, double[] widths, bool filter) => new(_s + "worksheet", new XAttribute(XNamespace.Xmlns + "r", _r),
        new XElement(_s + "sheetViews", new XElement(_s + "sheetView", new XAttribute("workbookViewId", 0),
            new XElement(_s + "pane", new XAttribute("ySplit", filter ? 4 : 5), new XAttribute("topLeftCell", filter ? "A5" : "A6"), new XAttribute("activePane", "bottomLeft"), new XAttribute("state", "frozen")))),
        new XElement(_s + "cols", widths.Select((w, i) => new XElement(_s + "col", new XAttribute("min", i + 1), new XAttribute("max", i + 1), new XAttribute("width", w), new XAttribute("customWidth", 1)))),
        rows, filter ? new XElement(_s + "autoFilter", new XAttribute("ref", $"A4:M{Math.Max(4, rows.Elements().Last().Attribute("r") is { } attr ? int.Parse(attr.Value) : 4)}")) : null);
    private static XElement Styles() => new(_s + "styleSheet",
        new XElement(_s + "numFmts", new XAttribute("count", 3),
            new XElement(_s + "numFmt", new XAttribute("numFmtId", 164), new XAttribute("formatCode", "[h]:mm:ss")),
            new XElement(_s + "numFmt", new XAttribute("numFmtId", 165), new XAttribute("formatCode", "0.000000")),
            new XElement(_s + "numFmt", new XAttribute("numFmtId", 166), new XAttribute("formatCode", "#,##0.00"))),
        new XElement(_s + "fonts", new XAttribute("count", 2),
            new XElement(_s + "font", new XElement(_s + "sz", new XAttribute("val", 11)), new XElement(_s + "name", new XAttribute("val", "Calibri"))),
            new XElement(_s + "font", new XElement(_s + "b"), new XElement(_s + "sz", new XAttribute("val", 11)), new XElement(_s + "name", new XAttribute("val", "Calibri")))),
        new XElement(_s + "fills", new XAttribute("count", 2), new XElement(_s + "fill", new XElement(_s + "patternFill", new XAttribute("patternType", "none"))), new XElement(_s + "fill", new XElement(_s + "patternFill", new XAttribute("patternType", "gray125")))),
        new XElement(_s + "borders", new XAttribute("count", 1), new XElement(_s + "border", new XElement(_s + "left"), new XElement(_s + "right"), new XElement(_s + "top"), new XElement(_s + "bottom"), new XElement(_s + "diagonal"))),
        new XElement(_s + "cellStyleXfs", new XAttribute("count", 1), Xf(0, 0)),
        new XElement(_s + "cellXfs", new XAttribute("count", 5), Xf(0, 0), Xf(0, 1), Xf(164, 0), Xf(165, 0), Xf(166, 0)),
        new XElement(_s + "cellStyles", new XAttribute("count", 1), new XElement(_s + "cellStyle", new XAttribute("name", "Normal"), new XAttribute("xfId", 0), new XAttribute("builtinId", 0))));
    private static XElement Xf(int number, int font) => new(_s + "xf", new XAttribute("numFmtId", number), new XAttribute("fontId", font), new XAttribute("fillId", 0), new XAttribute("borderId", 0));
    private static XElement Marker(string name, int column, int row) => new(_d + name, new XElement(_d + "col", column), new XElement(_d + "colOff", 0), new XElement(_d + "row", row), new XElement(_d + "rowOff", 0));
    private static XElement Val(string name, object value) => new(_c + name, new XAttribute("val", value));
    private static XElement Chart(DailyHours[] days, int row, string title)
    {
        var series = new XElement(_c + "ser", Val("idx", 0), Val("order", 0), new XElement(_c + "tx", new XElement(_c + "v", "Hours worked")),
            new XElement(_c + "spPr", new XElement(_a + "solidFill", new XElement(_a + "srgbClr", new XAttribute("val", "009F9A")))),
            new XElement(_c + "cat", new XElement(_c + "strRef", new XElement(_c + "f", $"'Daily summary'!$A${row}:$A${row + days.Length - 1}"),
                new XElement(_c + "strCache", Val("ptCount", days.Length), days.Select((d, i) => new XElement(_c + "pt", new XAttribute("idx", i), new XElement(_c + "v", d.Day.ToString("yyyy-MM-dd"))))))),
            new XElement(_c + "val", new XElement(_c + "numRef", new XElement(_c + "f", $"'Daily summary'!$B${row}:$B${row + days.Length - 1}"),
                new XElement(_c + "numCache", new XElement(_c + "formatCode", "0.00"), Val("ptCount", days.Length), days.Select((d, i) => new XElement(_c + "pt", new XAttribute("idx", i), new XElement(_c + "v", d.Hours.ToString(CultureInfo.InvariantCulture))))))));
        return new XElement(_c + "chartSpace", new XAttribute(XNamespace.Xmlns + "c", _c), new XAttribute(XNamespace.Xmlns + "a", _a),
            Val("lang", "en-US"), new XElement(_c + "chart",
                new XElement(_c + "title", new XElement(_c + "tx", new XElement(_c + "rich", new XElement(_a + "bodyPr"), new XElement(_a + "lstStyle"),
                    new XElement(_a + "p", new XElement(_a + "r", new XElement(_a + "t", title + " — hours worked"))))), Val("overlay", 0)),
                new XElement(_c + "plotArea", new XElement(_c + "layout"),
                    new XElement(_c + "barChart", Val("barDir", "col"), Val("grouping", "clustered"), series, Val("gapWidth", 45), Val("axId", 100), Val("axId", 200)),
                    new XElement(_c + "catAx", Val("axId", 100), new XElement(_c + "scaling", Val("orientation", "minMax")), Val("axPos", "b"), Val("tickLblPos", "nextTo"), Val("crossAx", 200), Val("crosses", "autoZero"), Val("auto", 1), Val("lblAlgn", "ctr"), Val("lblOffset", 100)),
                    new XElement(_c + "valAx", Val("axId", 200), new XElement(_c + "scaling", Val("orientation", "minMax"), Val("min", 0)), Val("axPos", "l"), new XElement(_c + "majorGridlines"),
                        new XElement(_c + "numFmt", new XAttribute("formatCode", "0.0\" h\""), new XAttribute("sourceLinked", 0)), Val("tickLblPos", "nextTo"), Val("crossAx", 100), Val("crosses", "autoZero"), Val("crossBetween", "between"))),
                Val("plotVisOnly", 1), Val("dispBlanksAs", "zero")));
    }
}
