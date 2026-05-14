using System.Globalization;
using System.IO;
using System.Text;

namespace ChordproExtractor;

/// <summary>
/// Chordpro 先頭の {title:} / {subtitle:} / {c:} / {key:} ブロックを組み立てる。
/// </summary>
internal static class ChordproPreamble
{
    private const string CLineSuffix = "　4/4拍子　-：4分音符 =：8分音符}";

    /// <summary>
    /// MP3 のみタグ参照。WAV 等はファイル名をタイトルにし、アーティストは「不明」。
    /// </summary>
    internal static (string Title, string Artist) ReadTitleAndArtist(string audioPath)
    {
        var fallbackTitle = Path.GetFileNameWithoutExtension(audioPath);
        if (string.IsNullOrWhiteSpace(fallbackTitle))
            fallbackTitle = "Untitled";

        var ext = Path.GetExtension(audioPath);
        if (!ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase))
            return (SanitizeDirective(fallbackTitle), "不明");

        try
        {
            using var tf = global::TagLib.File.Create(audioPath);
            var tag = tf.Tag;
            var title = string.IsNullOrWhiteSpace(tag.Title) ? fallbackTitle : tag.Title.Trim();
            var artist = tag.FirstPerformer;
            if (string.IsNullOrWhiteSpace(artist))
                artist = tag.JoinedPerformers;
            if (string.IsNullOrWhiteSpace(artist))
                artist = tag.JoinedAlbumArtists;
            if (string.IsNullOrWhiteSpace(artist))
                artist = "不明";

            return (SanitizeDirective(title), SanitizeDirective(artist.Trim()));
        }
        catch
        {
            return (SanitizeDirective(fallbackTitle), "不明");
        }
    }

    internal static string Build(string audioPath, double bpmValue, string? inferredKeyDisplay)
    {
        var (title, artist) = ReadTitleAndArtist(audioPath);
        var bpmPart = FormatBpmForHeader(bpmValue);

        var sb = new StringBuilder();
        sb.Append("{title:").Append(title).AppendLine("}");
        sb.Append("{subtitle:歌：").Append(artist).AppendLine("}");
        sb.Append("{c:BPM=").Append(bpmPart).Append(CLineSuffix).AppendLine();
        if (!string.IsNullOrWhiteSpace(inferredKeyDisplay))
            sb.Append("{key:").Append(SanitizeDirective(inferredKeyDisplay.Trim())).Append('}').AppendLine();

        sb.AppendLine();
        return sb.ToString();
    }

    private static string FormatBpmForHeader(double bpm)
    {
        if (double.IsNaN(bpm) || double.IsInfinity(bpm) || bpm <= 0)
            return "?";

        return bpm.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0').TrimEnd('.');
    }

    /// <summary>ディレクティブ値内の改行・波括弧を潰す。</summary>
    private static string SanitizeDirective(string s)
    {
        if (string.IsNullOrEmpty(s))
            return string.Empty;

        return s.Replace('\r', ' ')
            .Replace('\n', ' ')
            .Replace('{', '（')
            .Replace('}', '）')
            .Trim();
    }
}
