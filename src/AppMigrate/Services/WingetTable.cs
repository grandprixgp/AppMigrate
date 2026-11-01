using System.Text;
using System.Text.RegularExpressions;

namespace AppMigrate.Services;

/// <summary>
/// Parses WinGet's fixed-width table output without relying on English column titles.
/// The header is the line above the dashed separator, and a column boundary is a header
/// word start that every data row also leaves blank in front of. Positions are measured in
/// terminal cells, so East Asian wide characters do not shift later columns.
/// </summary>
public sealed class WingetTable
{
    private static readonly Regex Ansi = new(@"\x1b\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    // Placeholder occupying the second cell of a wide character.
    private const char WidePad = '\0';

    private WingetTable(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows)
    {
        Headers = headers;
        Rows = rows;
    }

    public IReadOnlyList<string> Headers { get; }
    public IReadOnlyList<string[]> Rows { get; }
    public int ColumnCount => Headers.Count;

    public static WingetTable? Parse(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        string[] lines = Ansi.Replace(raw, "").Split('\n').Select(CleanLine).ToArray();

        int separator = Array.FindIndex(lines, IsSeparator);
        if (separator < 1) return null;

        string header = ToCells(lines[separator - 1]);
        var data = new List<string>();
        for (int i = separator + 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i]))
            {
                // Footer messages follow the table after a blank line.
                if (data.Count > 0) break;
                continue;
            }
            if (IsSeparator(lines[i])) continue;
            data.Add(ToCells(lines[i]));
        }

        List<int> starts = ColumnStarts(header, data);
        if (starts.Count < 2) return null;

        return new WingetTable(Slice(header, starts), data.Select(row => Slice(row, starts)).ToList());
    }

    private static string CleanLine(string line)
    {
        line = line.TrimEnd('\r');
        // Progress spinners redraw with a bare carriage return; keep only the final text.
        int cr = line.LastIndexOf('\r');
        return cr >= 0 ? line[(cr + 1)..] : line;
    }

    private static bool IsSeparator(string line)
    {
        string trimmed = line.Trim();
        return trimmed.Length >= 8 && trimmed.All(c => c == '-');
    }

    private static List<int> ColumnStarts(string header, List<string> rows)
    {
        var starts = new List<int> { 0 };
        for (int p = 1; p < header.Length; p++)
        {
            if (header[p] == ' ' || header[p - 1] != ' ') continue;
            // Words inside a multi-word title are rejected because some row runs through them.
            if (rows.All(row => row.Length <= p || row[p - 1] == ' ')) starts.Add(p);
        }
        return starts;
    }

    private static string[] Slice(string line, List<int> starts)
    {
        var cells = new string[starts.Count];
        for (int k = 0; k < starts.Count; k++)
        {
            int start = starts[k];
            int end = k + 1 < starts.Count ? starts[k + 1] : line.Length;
            cells[k] = start < line.Length
                ? line[start..Math.Min(end, line.Length)].Replace(WidePad.ToString(), "").Trim()
                : "";
        }
        return cells;
    }

    private static string ToCells(string line)
    {
        if (!line.Any(IsWide)) return line;
        var sb = new StringBuilder(line.Length + 16);
        foreach (char c in line)
        {
            sb.Append(c);
            if (IsWide(c)) sb.Append(WidePad);
        }
        return sb.ToString();
    }

    // East Asian Wide/Fullwidth ranges in the BMP. Surrogate pairs already take two chars.
    private static bool IsWide(char c)
        => c is >= 'ᄀ' and <= 'ᅟ'
            or >= '⺀' and <= '〾'
            or >= 'ぁ' and <= '㏿'
            or >= '㐀' and <= '䶿'
            or >= '一' and <= '鿿'
            or >= 'ꀀ' and <= '꓏'
            or >= '가' and <= '힣'
            or >= '豈' and <= '﫿'
            or >= '︰' and <= '﹏'
            or >= '＀' and <= '｠'
            or >= '￠' and <= '￦';
}
