using System.Globalization;
using System.Text.RegularExpressions;

namespace ChordproExtractor;

/// <summary>Sonic Annotator の CSV 標準出力の解析。</summary>
internal static class SonicCsvParser
{
    private static readonly Regex TempoCsvFloatRegex = new(
        @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    private static readonly Regex CsvQuotedTimeChordTailRegex = new(
        @",\s*([\d.Ee+-]+)\s*,\s*""([^""]*)""\s*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromMilliseconds(250));

    internal static List<double> ParseAndNormalizeBarStartTimes(IReadOnlyList<string> csvLines)
    {
        var raw = new List<double>();
        foreach (var line in csvLines)
        {
            if (TryParseCsvLeadingTimeSeconds(line, out var sec) && sec >= 0 && !double.IsNaN(sec) &&
                !double.IsInfinity(sec))
                raw.Add(sec);
        }

        raw.Sort();
        const double eps = 1e-3;
        var uniq = new List<double>();
        foreach (var t in raw)
        {
            if (uniq.Count == 0 || Math.Abs(uniq[^1] - t) > eps)
                uniq.Add(t);
        }

        return uniq;
    }

    internal static bool TryParseCsvLeadingTimeSeconds(string line, out double sec)
    {
        sec = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
            return false;

        var quoted = CsvQuotedTimeChordTailRegex.Match(trimmed);
        if (quoted.Success)
            return double.TryParse(
                quoted.Groups[1].Value.Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out sec);

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma > 0 &&
            double.TryParse(
                trimmed[..comma].Trim().Trim('"'),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out sec))
            return true;

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out sec);
    }

    internal static ChordPoint? ParseCsvChordLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;

        var quoted = CsvQuotedTimeChordTailRegex.Match(trimmed);
        if (quoted.Success)
        {
            if (!double.TryParse(
                    quoted.Groups[1].Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var sec))
                return null;

            var label = quoted.Groups[2].Value;
            if (string.IsNullOrEmpty(label))
                return null;

            return new ChordPoint(sec, label);
        }

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0 || comma >= trimmed.Length - 1)
            return null;

        var timeStr = trimmed[..comma].Trim().Trim('"');
        if (!double.TryParse(timeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out var sec2))
            return null;

        var chordPart = trimmed[(comma + 1)..].Trim().Trim('"');
        if (string.IsNullOrEmpty(chordPart))
            return null;

        return new ChordPoint(sec2, chordPart);
    }

    /// <summary>CSV 行に含まれる 30〜320 の数値候補から代表 BPM（中央値）を取る。</summary>
    internal static double? ParseGlobalTempoBpmFromCsvLines(IReadOnlyList<string> lines)
    {
        var values = new List<double>();
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            var t = line.Trim();
            if (t.StartsWith('#'))
                continue;

            double? lastCandidate = null;
            foreach (Match m in TempoCsvFloatRegex.Matches(t))
            {
                if (!double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    continue;
                if (v is >= 30 and <= 320)
                    lastCandidate = v;
            }

            if (lastCandidate.HasValue)
                values.Add(lastCandidate.Value);
        }

        if (values.Count == 0)
            return null;

        values.Sort();
        return values[values.Count / 2];
    }
}
