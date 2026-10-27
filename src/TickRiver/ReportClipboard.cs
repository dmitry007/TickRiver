using System.Globalization;
using System.Net;
using System.Text;
using System.Windows;
using TickRiver.Core;

namespace TickRiver;

internal static class ReportClipboard
{
    internal static readonly (string Key, string Header, Func<ReportRow, string> Value)[] Columns = [
        ("Date", "Date", r => r.DateLabel),
        ("Description", "Description", r => r.Description),
        ("ProjectClient", "Project · Client", r => r.ProjectLabel),
        ("Duration", "Duration", r => r.Duration),
        ("DecimalHours", "Decimal h", r => r.DecimalHours.ToString("N6", CultureInfo.CurrentCulture)),
        ("Amount", "Amount", r => r.AmountLabel),
        ("Billing", "Billing", r => r.BillingLabel)
    ];

    internal static DataObject Create(IReadOnlyList<ReportRow> rows, IEnumerable<string> selected)
    {
        var keys = selected.ToHashSet(StringComparer.Ordinal);
        var columns = Columns.Where(c => keys.Contains(c.Key)).ToArray();
        if (columns.Length == 0) throw new ArgumentException("Select at least one column to copy.");
        if (rows.Count == 0) throw new ArgumentException("There are no detailed entries to copy.");
        var values = new[] { columns.Select(c => c.Header).ToArray() }
            .Concat(rows.Select(r => columns.Select(c => c.Value(r)).ToArray()));
        var html = new StringBuilder("<table style=\"border-collapse:collapse;font-family:Calibri,sans-serif;font-size:11pt\">");
        var text = new StringBuilder();
        var header = true;
        foreach (var cells in values)
        {
            html.Append("<tr>");
            for (var i = 0; i < cells.Length; i++)
            {
                var value = cells[i];
                var tag = header ? "th" : "td";
                html.Append('<').Append(tag).Append(" style=\"border:1px solid #cdd9e0;padding:4pt 6pt;text-align:left;white-space:pre-wrap\">")
                    .Append(WebUtility.HtmlEncode(value).Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "<br>"))
                    .Append("</").Append(tag).Append('>');
                if (i > 0) text.Append('\t');
                text.Append(value.IndexOfAny(['\t', '\r', '\n', '"']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value);
            }
            html.Append("</tr>"); text.Append("\r\n"); header = false;
        }
        html.Append("</table>");
        const string before = "<html><head><meta charset=\"utf-8\"></head><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        const string template = "Version:1.0\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        var startHtml = string.Format(CultureInfo.InvariantCulture, template, 0, 0, 0, 0).Length;
        var startFragment = startHtml + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(html.ToString());
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        var data = new DataObject();
        data.SetData(DataFormats.Html, string.Format(CultureInfo.InvariantCulture, template, startHtml, endHtml, startFragment, endFragment) + before + html + after, false);
        data.SetData(DataFormats.UnicodeText, text.ToString(), false);
        return data;
    }
}
