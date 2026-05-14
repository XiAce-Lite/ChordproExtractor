using System.Globalization;

namespace ChordproExtractor;

internal static class BpmInput
{
    /// <summary>ユーザー入力 BPM。空欄は false。20〜400 の範囲のみ true。</summary>
    internal static bool TryParseUserBpm(string? text, out double bpm)
    {
        bpm = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            return false;

        if (v is < 20 or > 400)
            return false;

        bpm = v;
        return true;
    }

    internal static string FormatBpmForDisplay(double bpm)
    {
        var s = bpm.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0');
        return s.TrimEnd('.');
    }
}
