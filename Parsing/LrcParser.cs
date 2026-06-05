using System.Globalization;
using Mystral.Models;

namespace Mystral.Parsing;

public static class LrcParser
{
    public static IReadOnlyList<LyricLine> Parse(string? syncedLyrics)
    {
        if (string.IsNullOrWhiteSpace(syncedLyrics))
        {
            return [];
        }

        var lines = new List<LyricLine>();
        foreach (var rawLine in syncedLyrics.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            ParseLine(rawLine, lines);
        }

        return lines
            .OrderBy(line => line.Time)
            .ThenBy(line => line.Text)
            .ToList();
    }

    private static void ParseLine(string rawLine, List<LyricLine> lines)
    {
        if (string.IsNullOrWhiteSpace(rawLine))
        {
            return;
        }

        var stamps = new List<TimeSpan>();
        var cursor = 0;

        while (cursor < rawLine.Length && rawLine[cursor] == '[')
        {
            var close = rawLine.IndexOf(']', cursor);
            if (close < 0)
            {
                break;
            }

            var token = rawLine.Substring(cursor + 1, close - cursor - 1);
            if (TryParseTimestamp(token, out var time))
            {
                stamps.Add(time);
            }

            cursor = close + 1;
        }

        if (stamps.Count == 0)
        {
            return;
        }

        var text = rawLine[cursor..].Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (var stamp in stamps)
        {
            lines.Add(new LyricLine(stamp, text));
        }
    }

    private static bool TryParseTimestamp(string token, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        var colon = token.IndexOf(':');
        if (colon <= 0 || colon >= token.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(token[..colon], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return false;
        }

        if (!double.TryParse(token[(colon + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return false;
        }

        time = TimeSpan.FromSeconds(minutes * 60 + seconds);
        return true;
    }
}
