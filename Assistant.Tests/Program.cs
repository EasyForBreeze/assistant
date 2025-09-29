using System;
using System.Collections.Generic;
using System.Net;
using System.Text;
var placeholderRow = "<tr><td>—</td><td>—</td><td>—</td></tr>";

var localEmpty = BuildLocalRolesTable(Array.Empty<string>());
Ensure(localEmpty.Contains(placeholderRow, StringComparison.Ordinal), "Local roles placeholder row missing.");
Ensure(CountDataRows(localEmpty) == 1, "Local roles table should contain a single data row for placeholders.");

var localWithData = BuildLocalRolesTable(new[] { "Admin & User" });
Ensure(!localWithData.Contains(placeholderRow, StringComparison.Ordinal), "Local roles placeholder row rendered with data.");
Ensure(localWithData.Contains("Admin &amp; User", StringComparison.Ordinal), "Local roles are not HTML encoded.");
Ensure(localWithData.Contains("<td></td>", StringComparison.Ordinal), "Local roles description cell missing.");
Ensure(AllRowsEndWithContour(localWithData), "Local roles contour column is not populated.");

var serviceEmpty = BuildServiceRolesTable(Array.Empty<(string ClientId, string Role)>());
Ensure(serviceEmpty.Contains(placeholderRow, StringComparison.Ordinal), "Service roles placeholder row missing.");
Ensure(CountDataRows(serviceEmpty) == 1, "Service roles table should contain a single data row for placeholders.");

var serviceWithData = BuildServiceRolesTable(new[] { ("client<1>", "role&1") });
Ensure(!serviceWithData.Contains(placeholderRow, StringComparison.Ordinal), "Service roles placeholder row rendered with data.");
Ensure(serviceWithData.Contains("client&lt;1&gt;", StringComparison.Ordinal), "Service role client not HTML encoded.");
Ensure(serviceWithData.Contains("role&amp;1", StringComparison.Ordinal), "Service role name not HTML encoded.");
Ensure(AllRowsEndWithContour(serviceWithData), "Service roles contour column is not populated.");

Console.WriteLine("All HTML generation checks passed.");

static string BuildLocalRolesTable(IReadOnlyList<string> roles)
{
    var sb = new StringBuilder();
    sb.Append("<p class=\"auto-cursor-target\"><br /></p>");
    sb.Append("<table class=\"wrapped\" data-mce-resize=\"false\"><colgroup><col /><col /><col /></colgroup><tbody>");
    sb.Append("<tr><th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Role Name</span></th>");
    sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Description</span></th>");
    sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Контур</span></th></tr>");

    if (roles.Count == 0)
    {
        AppendTableRow(sb, ("—", false), ("—", false), ("—", false));
    }
    else
    {
        foreach (var role in roles)
        {
            AppendTableRow(sb, (role, true), (string.Empty, false), ("TEST", false));
        }
    }

    sb.Append("</tbody></table>");
    return sb.ToString();
}

static string BuildServiceRolesTable(IReadOnlyList<(string ClientId, string Role)> roles)
{
    var sb = new StringBuilder();
    sb.Append("<p class=\"auto-cursor-target\"><br /></p>");
    sb.Append("<table class=\"wrapped\" data-mce-resize=\"false\"><colgroup><col /><col /><col /></colgroup><tbody>");
    sb.Append("<tr><th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Role Name</span></th>");
    sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Client</span></th>");
    sb.Append("<th scope=\"col\"><span style=\"color:var(--ds-text,#333333);\">Контур</span></th></tr>");

    if (roles.Count == 0)
    {
        AppendTableRow(sb, ("—", false), ("—", false), ("—", false));
    }
    else
    {
        foreach (var (clientId, role) in roles)
        {
            AppendTableRow(sb, (role, true), (clientId, true), ("TEST", false));
        }
    }

    sb.Append("</tbody></table>");
    return sb.ToString();
}

static void AppendTableRow(StringBuilder sb, params (string? Value, bool Encode)[] cells)
{
    sb.Append("<tr>");
    foreach (var (value, encode) in cells)
    {
        sb.Append("<td>");
        if (!string.IsNullOrEmpty(value))
        {
            sb.Append(encode ? WebUtility.HtmlEncode(value) : value);
        }

        sb.Append("</td>");
    }

    sb.Append("</tr>");
}

static void Ensure(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

static bool AllRowsEndWithContour(string html)
{
    foreach (var row in GetDataRows(html))
    {
        if (!row.EndsWith("<td>TEST</td></tr>", StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static IReadOnlyList<string> GetDataRows(string html)
{
    var headerEnd = html.IndexOf("</tr>", StringComparison.Ordinal);
    if (headerEnd < 0)
    {
        return Array.Empty<string>();
    }

    var rows = new List<string>();
    var current = headerEnd + "</tr>".Length;
    while (current < html.Length)
    {
        var rowStart = html.IndexOf("<tr>", current, StringComparison.Ordinal);
        if (rowStart < 0)
        {
            break;
        }

        var rowEnd = html.IndexOf("</tr>", rowStart, StringComparison.Ordinal);
        if (rowEnd < 0)
        {
            break;
        }

        rows.Add(html.Substring(rowStart, rowEnd - rowStart + "</tr>".Length));
        current = rowEnd + "</tr>".Length;
    }

    return rows;
}

static int CountDataRows(string html) => GetDataRows(html).Count;
