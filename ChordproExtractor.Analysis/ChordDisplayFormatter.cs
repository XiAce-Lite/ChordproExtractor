using System.Globalization;

namespace ChordproExtractor;

internal static class ChordDisplayFormatter
{
    internal static string CleanChordSymbol(string raw)
    {
        var s = raw.Trim();
        return string.IsNullOrEmpty(s) ? s : s.Replace(":", "", StringComparison.Ordinal);
    }

    internal static string? FormatChordForDisplay(string raw)
    {
        var s = CleanChordSymbol(raw);
        if (string.IsNullOrWhiteSpace(s))
            return null;

        if (s.Equals("N", StringComparison.OrdinalIgnoreCase))
            return null;

        return s.Replace("maj7", "M7", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Chordino の先頭付近からルートを抜き出して {key:} 用の短い表記を推定する（厳密な調性推定ではない）。</summary>
    internal static string? TryGuessKeyFromChords(IReadOnlyList<ChordPoint> sorted)
    {
        foreach (var p in sorted)
        {
            var disp = FormatChordForDisplay(p.RawLabel);
            if (string.IsNullOrWhiteSpace(disp))
                continue;

            var slash = disp.IndexOf('/');
            var head = slash >= 0 ? disp.AsSpan(0, slash) : disp.AsSpan();
            var key = ExtractChordRootKeyForHeader(head);
            if (!string.IsNullOrWhiteSpace(key))
                return key;
        }

        return null;
    }

    internal static string? ExtractChordRootKeyForHeader(ReadOnlySpan<char> chordSansSlash)
    {
        if (chordSansSlash.IsEmpty)
            return null;

        var c0 = char.ToUpperInvariant(chordSansSlash[0]);
        if (c0 is < 'A' or > 'G')
            return null;

        var i = 1;
        if (i < chordSansSlash.Length && (chordSansSlash[i] == '#' || chordSansSlash[i] == 'b'))
        {
            if (chordSansSlash[i] == 'b' && i + 1 < chordSansSlash.Length && chordSansSlash[i + 1] == 'b')
                i += 2;
            else
                i++;
        }

        var root = chordSansSlash[..i].ToString();
        root = char.ToUpperInvariant(root[0]) + root[1..];

        if (i < chordSansSlash.Length && chordSansSlash[i] == 'm')
        {
            if (chordSansSlash.Length >= i + 3 &&
                chordSansSlash.Slice(i, 3).Equals("maj", StringComparison.OrdinalIgnoreCase))
                return root;

            return root + "m";
        }

        return root;
    }
}
