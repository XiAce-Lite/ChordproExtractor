using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ChordproExtractor;

/// <summary>
/// Demucs 出力フォルダ（%TEMP%/ChordproExtractor/demucs_*）の再利用と、一定期間経過フォルダの削除。
/// </summary>
internal static class DemucsWorkCache
{
    internal const string SourceMetaFileName = "chordpro_demucs_source.txt";
    internal static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(28);

    internal static void EnsureWorkRootExists()
    {
        Directory.CreateDirectory(ChordproPaths.DemucsWorkRoot);
    }

    /// <summary>
    /// 同一ソース（フルパス・サイズ・最終更新時刻）かつ有効な stems があるキャッシュフォルダを返す。
    /// </summary>
    internal static string? TryFindReusableWorkDir(string sourceAudioPath)
    {
        var root = ChordproPaths.DemucsWorkRoot;
        if (!Directory.Exists(root))
            return null;

        if (!TryGetSourceFingerprint(sourceAudioPath, out var norm, out var len, out var ticks))
            return null;

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "demucs_*", SearchOption.TopDirectoryOnly))
            {
                if (!TryReadSourceMeta(dir, out var metaPath, out var metaLen, out var metaTicks))
                    continue;

                if (!PathsEqualNormalized(metaPath, norm))
                    continue;
                if (metaLen != len || metaTicks != ticks)
                    continue;

                if (!TryResolveDemucsStemPaths(dir, out _, out _))
                    continue;

                return dir;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Demucs キャッシュ探索に失敗: {ex}");
        }

        return null;
    }

    /// <summary>
    /// 直下に stems がある場合と、htdemucs/曲名/ のようなネストの場合の両方に対応。
    /// </summary>
    internal static bool TryResolveDemucsStemPaths(string demucsRoot, out string vocalsPath, out string noVocalsPath)
    {
        vocalsPath = string.Empty;
        noVocalsPath = string.Empty;

        var directV = Path.Combine(demucsRoot, "vocals.wav");
        var directNv = Path.Combine(demucsRoot, "no_vocals.wav");
        if (File.Exists(directV) && File.Exists(directNv))
        {
            vocalsPath = directV;
            noVocalsPath = directNv;
            return true;
        }

        try
        {
            foreach (var v in Directory.GetFiles(demucsRoot, "vocals.wav", SearchOption.AllDirectories))
            {
                var dir = Path.GetDirectoryName(v);
                if (string.IsNullOrEmpty(dir))
                    continue;

                var nv = Path.Combine(dir, "no_vocals.wav");
                if (File.Exists(nv))
                {
                    vocalsPath = v;
                    noVocalsPath = nv;
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }

        return false;
    }

    internal static void WriteSourceMeta(string workDir, string sourceAudioPath)
    {
        if (!TryGetSourceFingerprint(sourceAudioPath, out var norm, out var len, out var ticks))
            return;

        var path = Path.Combine(workDir, SourceMetaFileName);
        var body = string.Join(
            "\n",
            norm,
            len.ToString(CultureInfo.InvariantCulture),
            ticks.ToString(CultureInfo.InvariantCulture));
        File.WriteAllText(path, body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    internal static void TouchWorkDir(string workDir)
    {
        try
        {
            Directory.SetLastWriteTimeUtc(workDir, DateTime.UtcNow);
            var meta = Path.Combine(workDir, SourceMetaFileName);
            if (File.Exists(meta))
                File.SetLastWriteTimeUtc(meta, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Demucs キャッシュの Touch に失敗 ({workDir}): {ex}");
        }
    }

    /// <summary>
    /// 最終更新から <paramref name="maxAge"/> を超えた demucs_* フォルダを削除する（起動時メンテ用）。
    /// </summary>
    internal static void DeleteExpiredWorkDirs(TimeSpan maxAge, Action<string>? log = null)
    {
        var root = ChordproPaths.DemucsWorkRoot;
        if (!Directory.Exists(root))
            return;

        var cutoff = DateTime.UtcNow - maxAge;
        foreach (var dir in Directory.EnumerateDirectories(root, "demucs_*", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var last = Directory.GetLastWriteTimeUtc(dir);
                if (last >= cutoff)
                    continue;

                Directory.Delete(dir, recursive: true);
                log?.Invoke($"削除（期限切れ）: {dir}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Demucs 期限切れフォルダの削除に失敗 ({dir}): {ex}");
                log?.Invoke($"削除失敗: {dir} — {ex.Message}");
            }
        }
    }

    private static bool TryGetSourceFingerprint(string sourceAudioPath, out string normalizedPath, out long length,
        out long lastWriteTicks)
    {
        normalizedPath = string.Empty;
        length = 0;
        lastWriteTicks = 0;

        try
        {
            var full = Path.GetFullPath(sourceAudioPath);
            if (!File.Exists(full))
                return false;

            var fi = new FileInfo(full);
            normalizedPath = full;
            length = fi.Length;
            lastWriteTicks = fi.LastWriteTimeUtc.Ticks;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryReadSourceMeta(string workDir, out string path, out long length, out long lastWriteTicks)
    {
        path = string.Empty;
        length = 0;
        lastWriteTicks = 0;

        var metaPath = Path.Combine(workDir, SourceMetaFileName);
        if (!File.Exists(metaPath))
            return false;

        try
        {
            var lines = File.ReadAllLines(metaPath);
            if (lines.Length < 3)
                return false;

            path = lines[0].Trim();
            if (!long.TryParse(lines[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out length))
                return false;
            if (!long.TryParse(lines[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out lastWriteTicks))
                return false;

            return path.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool PathsEqualNormalized(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
            return false;

        return string.Equals(
            Path.GetFullPath(a.Trim()),
            Path.GetFullPath(b.Trim()),
            StringComparison.OrdinalIgnoreCase);
    }
}
