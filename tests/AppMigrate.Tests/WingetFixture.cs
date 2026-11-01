using System.Text;

namespace AppMigrate.Tests;

/// <summary>Builds tables laid out the way winget prints them: padded columns, dashed separator, CRLF.</summary>
internal static class WingetFixture
{
    public static string Table(string[] headers, params string[][] rows)
    {
        var widths = new int[headers.Length];
        for (int c = 0; c < headers.Length; c++)
        {
            widths[c] = Math.Max(Cells(headers[c]), rows.Select(r => Cells(r[c])).DefaultIfEmpty(0).Max());
        }

        var sb = new StringBuilder();
        sb.Append(Line(headers, widths)).Append("\r\n");
        sb.Append(new string('-', widths.Sum() + widths.Length - 1)).Append("\r\n");
        foreach (string[] row in rows) sb.Append(Line(row, widths)).Append("\r\n");
        return sb.ToString();
    }

    private static string Line(string[] values, int[] widths)
    {
        var sb = new StringBuilder();
        for (int c = 0; c < values.Length; c++)
        {
            sb.Append(values[c]);
            if (c < values.Length - 1) sb.Append(' ', widths[c] - Cells(values[c]) + 1);
        }
        return sb.ToString();
    }

    // CJK characters take two terminal cells.
    private static int Cells(string value)
        => value.Sum(ch => ch is >= '぀' and <= '鿿' or >= '가' and <= '힣' or >= '＀' and <= '｠' ? 2 : 1);
}
