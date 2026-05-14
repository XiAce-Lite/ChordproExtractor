using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NAudio.Wave;

namespace ChordproExtractor;

public partial class MainWindow : Window
{
    /// <summary>QM Bar and Beat Tracker の「小節線」出力（公式プラグイン名は barbeattracker）。</summary>
    private const string VampQmBarBeatTrackerBars = "vamp:qm-vamp-plugins:qm-barbeattracker:bars";

    /// <summary>QM Segmenter の境界出力（sonic-annotator の出力キーは小文字の segmentation）。</summary>
    private const string VampQmSegmenterSegmentation = "vamp:qm-vamp-plugins:qm-segmenter:segmentation";

    /// <summary>QM Segmenter の境界イベント（時刻と、その境界から始まるセグメント型 ID）。</summary>
    private readonly record struct SegmentBoundaryEvent(double TimeSec, int SegmentTypeId);

    private string? _selectedAudioPath;
    private bool _isProcessing;
    private CancellationTokenSource? _cts;

    private readonly record struct ChordPoint(double Seconds, string RawLabel);

    /// <summary>直近の解析成功時のコード列（マージ時の {key:} 推定用）。</summary>
    private List<ChordPoint>? _lastChordParseResult;

    /// <summary>直近の解析で確定した BPM（マージ時プリアンブル用フォールバック）。</summary>
    private double _lastSuccessfulBpm;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private static void TryDeleteFileIfExists(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"一時 WAV の削除に失敗 ({path}): {ex}");
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "音声ファイル (*.mp3;*.wav)|*.mp3;*.wav|すべてのファイル (*.*)|*.*",
            Title = "音声ファイルを選択"
        };

        if (dlg.ShowDialog() == true)
            SetAudioPath(dlg.FileName);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length > 0)
        {
            e.Effects = DragDropEffects.Copy;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }

        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length > 0)
        {
            SetAudioPath(paths[0]);
        }

        e.Handled = true;
    }

    private void SetAudioPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            StatusLabel.Text = "ファイルが存在しません。";
            return;
        }

        var ext = Path.GetExtension(path);
        if (!ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".wav", StringComparison.OrdinalIgnoreCase))
        {
            StatusLabel.Text = "MP3 または WAV を選択してください。";
            return;
        }

        var previousPath = _selectedAudioPath;
        _selectedAudioPath = path;
        AudioPathTextBox.Text = path;
        if (!string.Equals(previousPath, path, StringComparison.OrdinalIgnoreCase))
            BpmTextBox.Clear();

        UpdateDurationLabel();
        StatusLabel.Text = "準備完了。解析で BPM とコード行を生成し、歌詞を貼ってからマージしてください。";
    }

    private void UpdateDurationLabel()
    {
        if (string.IsNullOrEmpty(_selectedAudioPath) || !File.Exists(_selectedAudioPath))
        {
            DurationLabel.Text = "長さ: —";
            return;
        }

        try
        {
            using var reader = new AudioFileReader(_selectedAudioPath);
            DurationLabel.Text = $"長さ: {reader.TotalTime:hh\\:mm\\:ss\\.fff}";
        }
        catch (Exception ex)
        {
            DurationLabel.Text = "長さ: 取得できませんでした";
            Debug.WriteLine(ex);
        }
    }

    private async void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isProcessing)
            return;

        if (string.IsNullOrEmpty(_selectedAudioPath) || !File.Exists(_selectedAudioPath))
        {
            StatusLabel.Text = "先に音声ファイルを選択してください。";
            return;
        }

        var baseDir = AppContext.BaseDirectory;
        var demucsWrapper = Path.GetFullPath(Path.Combine(baseDir, "demucs_wrapper.py"));
        var demucsExe = Path.GetFullPath(Path.Combine(baseDir, "tools", "demucs-worker.exe"));
        if (!File.Exists(demucsWrapper) && !File.Exists(demucsExe))
        {
            StatusLabel.Text =
                "Demucs 用に demucs_wrapper.py（出力にコピー）または tools/demucs-worker.exe のどちらかが必要です。";
            return;
        }

        var toolPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "sonic-annotator.exe"));
        if (!File.Exists(toolPath))
        {
            StatusLabel.Text = $"Sonic Annotator が見つかりません: {toolPath}";
            return;
        }

        var bpmInput = BpmTextBox.Text.Trim();
        if (bpmInput.Length > 0 && !TryParseUserBpm(bpmInput, out _))
        {
            MessageBox.Show(
                this,
                "BPM は空欄（自動検出）か、20〜400 の数値で入力してください。",
                "BPM の入力",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _isProcessing = true;
        ConvertButton.IsEnabled = false;
        MergeLyricsAndChordsButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SaveButton.IsEnabled = false;
        BpmTextBox.IsEnabled = false;
        _lastChordParseResult = null;
        _lastSuccessfulBpm = 0;
        ChordLinesTextBox.Clear();
        ChordproOutputTextBox.Clear();
        MainProgressBar.IsIndeterminate = true;
        MainProgressBar.Value = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        string? demucsWorkDir = null;
        try
        {
            DemucsWorkCache.EnsureWorkRootExists();

            double bpmValue;
            if (TryParseUserBpm(BpmTextBox.Text, out var manualBpm))
            {
                bpmValue = manualBpm;
            }
            else
            {
                StatusLabel.Text = "BPM を自動検出しています…";
                var (bpmExit, bpmStderr, autoBpm) =
                    await EstimateBpmAsync(toolPath, _selectedAudioPath!, token).ConfigureAwait(true);

                if (bpmExit != 0)
                {
                    StatusLabel.Text = $"BPM 自動検出が失敗しました（終了コード {bpmExit}）。";
                    var errBody = BuildProcessErrorBody(null, bpmStderr);
                    if (!string.IsNullOrEmpty(errBody))
                        ChordLinesTextBox.Text = errBody;

                    return;
                }

                if (!autoBpm.HasValue)
                {
                    StatusLabel.Text =
                        "BPM を結果から読み取れませんでした。qm-tempotracker の出力を確認するか、BPM を手動入力してください。";
                    return;
                }

                bpmValue = autoBpm.Value;
                var displayBpm = FormatBpmForDisplay(bpmValue);
                try
                {
                    await Dispatcher.InvokeAsync(() => BpmTextBox.Text = displayBpm);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"BPM 表示の更新に失敗: {ex}");
                }
            }

            demucsWorkDir = DemucsWorkCache.TryFindReusableWorkDir(_selectedAudioPath!);
            var reusedDemucsCache = demucsWorkDir != null;
            if (demucsWorkDir != null)
            {
                DemucsWorkCache.TouchWorkDir(demucsWorkDir);
                StatusLabel.Text = "Demucs の分離結果をキャッシュから再利用しています…";
            }
            else
            {
                demucsWorkDir = Path.Combine(
                    ChordproPaths.DemucsWorkRoot,
                    "demucs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(demucsWorkDir);

                StatusLabel.Text = "Demucs でボーカル／伴奏を分離しています…";
                var (demucsExit, demucsStderr, demucsStdout) =
                    await RunDemucsSeparationAsync(
                        baseDir,
                        demucsWrapper,
                        demucsExe,
                        _selectedAudioPath,
                        demucsWorkDir,
                        token).ConfigureAwait(true);

                if (demucsExit != 0)
                {
                    if (demucsExit == -1)
                    {
                        StatusLabel.Text = "Demucs を起動できませんでした。";
                        if (!string.IsNullOrWhiteSpace(demucsStderr))
                            ChordLinesTextBox.Text = demucsStderr.Trim();
                        return;
                    }

                    var hint = DescribeWindowsExitCode(demucsExit);
                    StatusLabel.Text =
                        $"Demucs が失敗しました（終了コード {demucsExit}）。{hint}";

                    var errBody = BuildProcessErrorBody(demucsStdout, demucsStderr);
                    if (!string.IsNullOrEmpty(errBody))
                        ChordLinesTextBox.Text = errBody;

                    return;
                }

                if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out _, out _))
                {
                    StatusLabel.Text =
                        "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。出力フォルダ構成を確認してください。";
                    ChordLinesTextBox.Text = "探索ルート: " + demucsWorkDir;
                    return;
                }

                DemucsWorkCache.WriteSourceMeta(demucsWorkDir, _selectedAudioPath!);
            }

            if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out _, out var noVocalsWav))
            {
                StatusLabel.Text =
                    "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。キャッシュが壊れている可能性があります。";
                ChordLinesTextBox.Text = "探索ルート: " + demucsWorkDir;
                return;
            }

            StatusLabel.Text = "Chordino・Bar Tracker・Segmenter を並列実行しています…";
            var chordTask = RunSonicAnnotatorAsync(toolPath, noVocalsWav, token);
            var barTask = RunSonicVampCsvLinesAsync(toolPath, noVocalsWav, VampQmBarBeatTrackerBars, token);
            var segTask = RunSonicVampCsvLinesAsync(toolPath, noVocalsWav, VampQmSegmenterSegmentation, token);
            await Task.WhenAll(chordTask, barTask, segTask).ConfigureAwait(true);

            var (exitCode, stderr, chords) = await chordTask.ConfigureAwait(true);
            var (barExit, _, barLines) = await barTask.ConfigureAwait(true);
            var (segExit, _, segLines) = await segTask.ConfigureAwait(true);

            if (exitCode != 0)
            {
                var hint = DescribeWindowsExitCode(exitCode);
                StatusLabel.Text =
                    $"Chordino が失敗しました（終了コード {exitCode}）。{hint}";

                if (!string.IsNullOrWhiteSpace(stderr))
                    ChordLinesTextBox.Text = "[標準エラー出力]" + Environment.NewLine + stderr.Trim();

                return;
            }

            var warnParts = new List<string>();
            if (barExit != 0)
                warnParts.Add($"Bar Tracker 失敗（終了コード {barExit}）。4 小節改行は小節データなしでスキップ。");
            if (segExit != 0)
                warnParts.Add($"Segmenter 失敗（終了コード {segExit}）。セクション見出しはスキップ。");

            chords.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

            List<SegmentBoundaryEvent> segmentEvents;
            try
            {
                segmentEvents = ParseSegmentBoundaryEvents(segLines);
            }
            catch
            {
                segmentEvents = [];
                warnParts.Add("Segmenter の境界解析で例外が発生したため、セクション見出しをスキップしました。");
            }

            var barStarts = ParseAndNormalizeBarStartTimes(barLines);

            if (barExit == 0 && barStarts.Count == 0 && barLines.Count > 0)
                warnParts.Add("Bar Tracker の CSV から小節時刻を解釈できませんでした。");
            if (segExit == 0 && segmentEvents.Count == 0 && segLines.Count > 0)
                warnParts.Add("Segmenter の CSV から境界・セグメント ID を解釈できませんでした。");

            StatusLabel.Text = "コード行を組み立てています…";
            IReadOnlyList<SegmentBoundaryEvent>? segmentForSectionHeaders =
                segExit == 0 && segLines.Count > 0 && segmentEvents.Count > 0 ? segmentEvents : null;
            ChordLinesTextBox.Text =
                BuildChordGridFromChordsWithBarsAndSegments(chords, barStarts, segmentForSectionHeaders);
            _lastChordParseResult = [.. chords];
            _lastSuccessfulBpm = bpmValue;

            var okMsg = reusedDemucsCache
                ? "Demucs（キャッシュ再利用）+ Chordino + 小節／構造解析が完了しました。歌詞を貼りマージしてください。"
                : "Demucs + Chordino + 小節／構造解析が完了しました。歌詞を貼りマージしてください。";
            StatusLabel.Text = warnParts.Count > 0 ? okMsg + " " + string.Join(" ", warnParts) : okMsg;
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "キャンセルされました。";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"エラー: {ex.Message}";
            ChordLinesTextBox.Text = ex.ToString();
        }
        finally
        {
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = MainProgressBar.Maximum;
            ConvertButton.IsEnabled = true;
            MergeLyricsAndChordsButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            SaveButton.IsEnabled = true;
            BpmTextBox.IsEnabled = true;
            _isProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void MergeLyricsAndChordsButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_selectedAudioPath))
        {
            StatusLabel.Text = "マージ用の曲情報がありません。先に音声を選んで解析してください。";
            return;
        }

        var lyricLines = SplitLines(LyricsTextBox.Text);
        var chordLines = SplitLines(ChordLinesTextBox.Text);
        var mergedBody = MergeLyricsAndChordBlocksBySectionHeaders(lyricLines, chordLines);

        var bpmForPreamble = TryParseUserBpm(BpmTextBox.Text, out var manualBpm)
            ? manualBpm
            : (_lastSuccessfulBpm > 0 ? _lastSuccessfulBpm : double.NaN);

        var keyGuess = _lastChordParseResult != null ? TryGuessKeyFromChords(_lastChordParseResult) : null;
        var preamble = ChordproPreamble.Build(_selectedAudioPath, bpmForPreamble, keyGuess);
        ChordproOutputTextBox.Text = preamble + mergedBody;
        StatusLabel.Text = "マージが完了しました。右の内容を確認して保存できます。";
    }

    private static string[] SplitLines(string text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        return text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
    }

    private sealed class LyricChordSectionBlock
    {
        public string? Header;
        public List<string> ContentLines { get; } = [];
    }

    /// <summary>
    /// 見出し行でブロック分割し、同一見出し同士をペアにする。
    /// 各段落ではコード譜の全行から和音トークンを集約し、歌詞行の文字数比でトークンを按分したうえで
    /// <see cref="MergeLyricLineWithChordLine"/> によりインライン化する（C# ネイティブ・セクション一致型比率マージ）。
    /// ミリ秒単位の強制アライメントは将来 ONNX 等で拡張可能（音声は vocals / 伴奏解析の時刻と突き合わせ）。
    /// </summary>
    private static string MergeLyricsAndChordBlocksBySectionHeaders(string[] lyricLines, string[] chordLines)
    {
        var lyricBlocks = SplitChordproTextIntoSectionBlocks(lyricLines);
        var chordBlocks = SplitChordproTextIntoSectionBlocks(chordLines);
        if (lyricBlocks.Count == 0 && chordBlocks.Count == 0)
            return string.Empty;

        var chordUsed = new bool[chordBlocks.Count];
        var sb = new StringBuilder();

        for (var li = 0; li < lyricBlocks.Count; li++)
        {
            var lb = lyricBlocks[li];
            var ci = FindChordBlockIndexMatchingHeader(chordBlocks, chordUsed, lb.Header);
            if (!string.IsNullOrEmpty(lb.Header))
                sb.AppendLine(lb.Header);

            if (ci >= 0)
            {
                chordUsed[ci] = true;
                var cb = chordBlocks[ci];
                foreach (var merged in MergeMatchedSectionParagraphsNative(lb.ContentLines, cb.ContentLines))
                    sb.AppendLine(merged);
            }
            else
            {
                foreach (var line in lb.ContentLines)
                    sb.AppendLine(line);
            }
        }

        for (var i = 0; i < chordBlocks.Count; i++)
        {
            if (chordUsed[i])
                continue;
            var cb = chordBlocks[i];
            if (!string.IsNullOrEmpty(cb.Header))
                sb.AppendLine(cb.Header);
            foreach (var line in cb.ContentLines)
                sb.AppendLine(MergeLyricLineWithChordLine(string.Empty, line));
        }

        return sb.ToString();
    }

    /// <summary>段落内の複数行コードグリッドを和音トークン列にし、歌詞行へ文字数比で割り付けてマージする。</summary>
    private static List<string> MergeMatchedSectionParagraphsNative(
        List<string> lyricLines,
        List<string> chordGridLines)
    {
        if (lyricLines.Count == 0)
            return [];

        var tokens = CollectChordBracketTokensFromGridLines(chordGridLines);
        if (tokens.Count == 0)
            return [.. lyricLines];

        var weights = lyricLines.Select(l => Math.Max(1, l.Trim().Length)).ToArray();
        var totalW = weights.Sum();
        var results = new List<string>(lyricLines.Count);
        var tStart = 0;
        double cumFrac = 0;
        for (var li = 0; li < lyricLines.Count; li++)
        {
            cumFrac += weights[li] / (double)totalW;
            var tEnd = li == lyricLines.Count - 1
                ? tokens.Count
                : Math.Clamp((int)Math.Round(cumFrac * tokens.Count), tStart, tokens.Count);
            if (tEnd < tStart)
                tEnd = tStart;
            var slice = tEnd > tStart ? tokens.GetRange(tStart, tEnd - tStart) : [];
            tStart = tEnd;
            var ly = lyricLines[li];
            var synthChordLine = BuildChordLineForRatioMerge(slice, Math.Max(1, ly.Length));
            results.Add(MergeLyricLineWithChordLine(ly, synthChordLine));
        }

        return results;
    }

    private static List<string> CollectChordBracketTokensFromGridLines(List<string> chordGridLines)
    {
        var list = new List<string>();
        foreach (var line in chordGridLines)
        {
            for (var i = 0; i < line.Length; i++)
            {
                if (line[i] != '[')
                    continue;

                var close = line.IndexOf(']', i + 1);
                if (close < 0)
                    break;

                var tok = line.Substring(i, close - i + 1);
                if (IsChordBracketTokenForMerge(tok))
                    list.Add(tok);

                i = close;
            }
        }

        return list;
    }

    private static bool IsChordBracketTokenForMerge(string token)
    {
        if (token.Length < 3 || token[0] != '[' || token[^1] != ']')
            return false;

        var inner = token[1..^1].Trim();
        if (inner.Length == 0)
            return false;

        return !SectionHeaderInnerLooksStructural(inner);
    }

    /// <summary>比率マージ用に、トークンを歌詞長に沿って等間隔に配置した疑似コード行を作る。</summary>
    private static string BuildChordLineForRatioMerge(List<string> tokens, int targetLen)
    {
        if (tokens.Count == 0)
            return string.Empty;
        if (tokens.Count == 1)
            return tokens[0].PadRight(Math.Max(tokens[0].Length, targetLen));

        var spreadLen = Math.Max(Math.Max(targetLen, tokens.Count), 2);
        var len = Math.Max(spreadLen, tokens.Sum(static t => t.Length) + tokens.Count - 1);
        var sb = new StringBuilder(len);
        for (var i = 0; i < tokens.Count; i++)
        {
            var targetPos = (int)Math.Round(i * (double)(spreadLen - 1) / (tokens.Count - 1));
            while (sb.Length < targetPos)
                sb.Append(' ');

            sb.Append(tokens[i]);
        }

        while (sb.Length < targetLen)
            sb.Append(' ');

        return sb.ToString();
    }

    private static List<LyricChordSectionBlock> SplitChordproTextIntoSectionBlocks(string[] lines)
    {
        var blocks = new List<LyricChordSectionBlock>();
        LyricChordSectionBlock? cur = null;

        foreach (var raw in lines)
        {
            if (IsChordProSectionHeaderLine(raw))
            {
                if (cur != null)
                    blocks.Add(cur);
                cur = new LyricChordSectionBlock { Header = raw.Trim() };
            }
            else
            {
                cur ??= new LyricChordSectionBlock { Header = null };
                cur.ContentLines.Add(raw);
            }
        }

        if (cur != null)
            blocks.Add(cur);

        return blocks;
    }

    private static bool IsChordProSectionHeaderLine(string line)
    {
        var t = line.Trim();
        if (t.Length < 3 || t[0] != '[' || t[^1] != ']')
            return false;
        if (t.Contains('|'))
            return false;

        var inner = t[1..^1].Trim();
        if (inner.Length == 0)
            return false;

        if (SectionHeaderInnerLooksStructural(inner))
            return true;

        return inner.Length >= 6 && inner.Contains(' ', StringComparison.Ordinal);
    }

    private static bool SectionHeaderInnerLooksStructural(string inner)
    {
        return inner.StartsWith("Intro", StringComparison.OrdinalIgnoreCase)
               || inner.StartsWith("Outro", StringComparison.OrdinalIgnoreCase)
               || inner.StartsWith("Verse", StringComparison.OrdinalIgnoreCase)
               || inner.StartsWith("Chorus", StringComparison.OrdinalIgnoreCase)
               || inner.StartsWith("Bridge", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeSectionHeaderForPairing(string? header)
    {
        if (string.IsNullOrWhiteSpace(header))
            return string.Empty;
        var t = header.Trim();
        if (t.Length < 3 || t[0] != '[' || t[^1] != ']')
            return t;
        var inner = t[1..^1].Trim();
        if (inner.StartsWith("Chorus", StringComparison.OrdinalIgnoreCase))
            return "[Chorus]";
        return t;
    }

    private static int FindChordBlockIndexMatchingHeader(
        List<LyricChordSectionBlock> chordBlocks,
        bool[] chordUsed,
        string? header)
    {
        var want = NormalizeSectionHeaderForPairing(header);
        for (var j = 0; j < chordBlocks.Count; j++)
        {
            if (chordUsed[j])
                continue;
            var h = chordBlocks[j].Header;
            if (string.IsNullOrEmpty(header))
            {
                if (string.IsNullOrEmpty(h))
                    return j;
            }
            else if (!string.IsNullOrEmpty(h) &&
                     string.Equals(
                         NormalizeSectionHeaderForPairing(h),
                         want,
                         StringComparison.OrdinalIgnoreCase))
            {
                return j;
            }
        }

        return -1;
    }

    /// <summary>1 行の歌詞と 1 行のコード（[A][B]…）を比率位置で合体。</summary>
    internal static string MergeLyricLineWithChordLine(string lyricLine, string chordLine)
    {
        var tokens = new List<(int startInChordLine, string token)>();
        for (var i = 0; i < chordLine.Length; i++)
        {
            if (chordLine[i] != '[')
                continue;

            var close = chordLine.IndexOf(']', i + 1);
            if (close < 0)
                break;

            var tok = chordLine.Substring(i, close - i + 1);
            tokens.Add((i, tok));
            i = close;
        }

        if (tokens.Count == 0)
            return string.IsNullOrEmpty(lyricLine) ? chordLine : lyricLine;

        if (string.IsNullOrEmpty(lyricLine))
            return chordLine;

        var chordLen = chordLine.Length;
        if (chordLen <= 0)
            return lyricLine;

        var lyricLen = lyricLine.Length;
        var inserts = new List<(int pos, int order, string token)>(tokens.Count);
        for (var k = 0; k < tokens.Count; k++)
        {
            var (start, tok) = tokens[k];
            var pos = (int)Math.Round((double)start / chordLen * lyricLen);
            if (pos < 0)
                pos = 0;
            if (pos > lyricLen)
                pos = lyricLen;

            inserts.Add((pos, k, tok));
        }

        inserts.Sort((a, b) =>
        {
            var c = a.pos.CompareTo(b.pos);
            return c != 0 ? c : a.order.CompareTo(b.order);
        });

        var cap = lyricLine.Length;
        foreach (var (pos, order, token) in inserts)
            cap += token.Length;

        var outSb = new StringBuilder(cap);
        var insertPtr = 0;
        for (var i = 0; i < lyricLen; i++)
        {
            while (insertPtr < inserts.Count && inserts[insertPtr].pos == i)
            {
                outSb.Append(inserts[insertPtr].token);
                insertPtr++;
            }

            outSb.Append(lyricLine[i]);
        }

        while (insertPtr < inserts.Count && inserts[insertPtr].pos == lyricLen)
        {
            outSb.Append(inserts[insertPtr].token);
            insertPtr++;
        }

        return outSb.ToString();
    }

    /// <summary>
    /// Demucs 分離: まず demucs_wrapper.py + インストール済み Python（PyTorch DLL 問題を避ける）を試し、
    /// 無ければ tools/demucs-worker.exe（PyInstaller）にフォールバック。
    /// 環境変数 CHORDPRO_DEMUCS_PYTHON に python.exe のフルパスを指定すると、そのインタプリタを最優先する。
    /// </summary>
    private static async Task<(int ExitCode, string? Stderr, string? Stdout)> RunDemucsSeparationAsync(
        string baseDir,
        string demucsWrapperPath,
        string demucsExePath,
        string inputAudioPath,
        string outputDir,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(demucsWrapperPath))
        {
            var customPy = Environment.GetEnvironmentVariable("CHORDPRO_DEMUCS_PYTHON");
            if (!string.IsNullOrWhiteSpace(customPy) && File.Exists(customPy))
            {
                try
                {
                    return await RunDemucsPythonAsync(
                        customPy,
                        baseDir,
                        demucsWrapperPath,
                        inputAudioPath,
                        outputDir,
                        prefixArgs: null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Win32Exception ex)
                {
                    Debug.WriteLine($"CHORDPRO_DEMUCS_PYTHON 起動失敗: {ex}");
                }
            }

            foreach (var (launcher, prefixArgs) in new (string, string[])[]
                     {
                         ("py", new[] { "-3" }),
                         ("python", Array.Empty<string>()),
                         ("python3", Array.Empty<string>())
                     })
            {
                try
                {
                    return await RunDemucsPythonAsync(
                        launcher,
                        baseDir,
                        demucsWrapperPath,
                        inputAudioPath,
                        outputDir,
                        prefixArgs,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Win32Exception ex)
                {
                    Debug.WriteLine($"Demucs Python 候補 {launcher}: {ex}");
                }
            }
        }

        if (File.Exists(demucsExePath))
        {
            var toolDir = Path.GetDirectoryName(demucsExePath);
            if (string.IsNullOrEmpty(toolDir))
                toolDir = baseDir;

            return await RunUtf8ProcessAsync(
                demucsExePath,
                toolDir,
                [inputAudioPath, outputDir],
                cancellationToken).ConfigureAwait(false);
        }

        return (
            -1,
            "Demucs を起動できません。demucs_wrapper.py と PATH 上の python（または CHORDPRO_DEMUCS_PYTHON）、または tools/demucs-worker.exe を確認してください。",
            null);
    }

    private static async Task<(int ExitCode, string? Stderr, string? Stdout)> RunDemucsPythonAsync(
        string pythonExecutable,
        string workingDirectory,
        string demucsWrapperPath,
        string inputAudioPath,
        string outputDir,
        string[]? prefixArgs = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>();
        if (prefixArgs is { Length: > 0 })
            args.AddRange(prefixArgs);

        args.Add(demucsWrapperPath);
        args.Add(inputAudioPath);
        args.Add(outputDir);

        return await RunUtf8ProcessAsync(pythonExecutable, workingDirectory, args, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("子プロセスの強制終了に失敗: " + ex);
        }
    }

    private static async Task<(int ExitCode, string? Stderr, string? Stdout)> RunUtf8ProcessAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> argumentList,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        foreach (var a in argumentList)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                /* 無視 */
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stderr))
            Debug.WriteLine(stderr);
        if (!string.IsNullOrWhiteSpace(stdout))
            Debug.WriteLine(stdout);

        return (
            process.ExitCode,
            string.IsNullOrWhiteSpace(stderr) ? null : stderr,
            string.IsNullOrWhiteSpace(stdout) ? null : stdout);
    }

    private static string? BuildProcessErrorBody(string? stdout, string? stderr)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.AppendLine("[標準出力]");
            sb.AppendLine(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine("[標準エラー]");
            sb.AppendLine(stderr.Trim());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// ユーザー入力 BPM。空欄は false。20〜400 の範囲のみ true。
    /// </summary>
    private static bool TryParseUserBpm(string? text, out double bpm)
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

    private static string FormatBpmForDisplay(double bpm)
    {
        var s = bpm.ToString("0.###", CultureInfo.InvariantCulture).TrimEnd('0');
        return s.TrimEnd('.');
    }

    /// <summary>
    /// Chordino の先頭付近からルートを抜き出して {key:} 用の短い表記を推定する（厳密な調性推定ではない）。
    /// </summary>
    private static string? TryGuessKeyFromChords(IReadOnlyList<ChordPoint> sorted)
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

    private static string? ExtractChordRootKeyForHeader(ReadOnlySpan<char> chordSansSlash)
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

    /// <summary>
    /// Bar Tracker の小節開始時刻で小節を定義し、各小節を 4 拍のマスに分割して Chordino のコードを最近傍の拍に配置する。
    /// 1 行＝4 小節（不足分は空小節でパディング）。末尾のコードなし空小節行は出力しない。
    /// Segmenter のイベント（時刻＋セグメント ID）が渡されたときのみ、全小節で ID 変化を検出し行頭に見出しを挿入する。
    /// </summary>
    private static string BuildChordGridFromChordsWithBarsAndSegments(
        IReadOnlyList<ChordPoint> sortedChords,
        List<double> barStartTimesSec,
        IReadOnlyList<SegmentBoundaryEvent>? segmentEventsSec)
    {
        if (sortedChords.Count == 0)
            return string.Empty;

        var maxChordT = 0.0;
        foreach (var p in sortedChords)
        {
            if (p.Seconds > maxChordT)
                maxChordT = p.Seconds;
        }

        if (barStartTimesSec.Count == 0)
            return BuildChordGridFromChordsWithBarsAndSegments(
                sortedChords,
                SynthesizeBarStartsFromChordSpan(maxChordT),
                segmentEventsSec);

        var lastBarEnd = ComputeLastBarEndSec(barStartTimesSec, maxChordT);
        var numBars = barStartTimesSec.Count;

        var grid = new List<string[]>(numBars);
        for (var bi = 0; bi < numBars; bi++)
            grid.Add(["-", "-", "-", "-"]);

        foreach (var p in sortedChords)
        {
            var disp = FormatChordForDisplay(p.RawLabel);
            if (disp == null)
                continue;

            var bi = FindBarIndexForTime(p.Seconds, barStartTimesSec, lastBarEnd);
            var barStart = barStartTimesSec[bi];
            var barEnd = bi + 1 < barStartTimesSec.Count ? barStartTimesSec[bi + 1] : lastBarEnd;
            var span = barEnd - barStart;
            if (span <= 1e-6)
                span = 0.25;

            var beatStarts = new double[4];
            for (var k = 0; k < 4; k++)
                beatStarts[k] = barStart + k * (span / 4.0);

            var bestK = 0;
            var bestD = Math.Abs(p.Seconds - beatStarts[0]);
            for (var k = 1; k < 4; k++)
            {
                var d = Math.Abs(p.Seconds - beatStarts[k]);
                if (d < bestD || (Math.Abs(d - bestD) < 1e-9 && k < bestK))
                {
                    bestD = d;
                    bestK = k;
                }
            }

            grid[bi][bestK] = '[' + disp + ']';
        }

        while (grid.Count % 4 != 0)
            grid.Add(["-", "-", "-", "-"]);

        TrimTrailingChordEmptyFourBarLines(grid);

        if (grid.Count == 0)
            return string.Empty;

        Dictionary<int, string> middleHeadersByLineStart = [];
        if (segmentEventsSec is { Count: > 0 })
        {
            try
            {
                middleHeadersByLineStart = BuildMiddleSectionHeadersBySegmentIds(
                    grid,
                    barStartTimesSec,
                    segmentEventsSec);
            }
            catch
            {
                middleHeadersByLineStart = [];
            }
        }

        var lastLineStart = grid.Count - 4;
        var sb = new StringBuilder();
        for (var lineStart = 0; lineStart < grid.Count; lineStart += 4)
        {
            if (segmentEventsSec is { Count: > 0 })
            {
                if (lineStart == 0)
                    sb.AppendLine("[Intro]");
                if (lineStart == lastLineStart)
                    sb.AppendLine("[Outro]");
                if (lineStart != 0 && lineStart != lastLineStart &&
                    middleHeadersByLineStart.TryGetValue(lineStart, out var sectionLine))
                    sb.AppendLine(sectionLine);
            }

            AppendFourBarChordLine(sb, grid, lineStart);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Bar Tracker が空のとき、2 秒小節の等間隔グリッドで代替する。</summary>
    private static List<double> SynthesizeBarStartsFromChordSpan(double maxChordT)
    {
        const double widthSec = 2.0;
        var times = new List<double> { 0 };
        while (times[^1] < maxChordT + widthSec)
            times.Add(times[^1] + widthSec);

        return times;
    }

    /// <summary>1 行＝4 小節。行は必ず「|」で始まり「|」で終わる。各小節は拍をスペース結合し、小節間は単一の「|」のみ（|| にならない）。</summary>
    private static void AppendFourBarChordLine(StringBuilder sb, List<string[]> grid, int lineStart)
    {
        var bars = new string[4];
        var beats = new string[4];
        for (var j = 0; j < 4; j++)
        {
            var cells = grid[lineStart + j];
            for (var i = 0; i < 4; i++)
                beats[i] = cells[i] == "-" ? "." : cells[i];

            bars[j] = string.Join(" ", beats);
        }

        sb.Append('|').Append(' ').Append(bars[0]);
        for (var j = 1; j < 4; j++)
            sb.Append(' ').Append('|').Append(' ').Append(bars[j]);

        sb.Append(' ').Append('|');
    }

    /// <summary>Chordino が未配置の「-」だけの小節行を末尾から 4 小節単位で取り除く。</summary>
    private static void TrimTrailingChordEmptyFourBarLines(List<string[]> grid)
    {
        while (grid.Count >= 4 && IsFourBarBlockChordEmpty(grid, grid.Count - 4))
            grid.RemoveRange(grid.Count - 4, 4);
    }

    private static bool IsFourBarBlockChordEmpty(List<string[]> grid, int lineStart)
    {
        for (var j = 0; j < 4; j++)
        {
            if (!IsBarChordEmpty(grid[lineStart + j]))
                return false;
        }

        return true;
    }

    private static bool IsBarChordEmpty(string[] cells)
    {
        foreach (var c in cells)
        {
            if (c != "-")
                return false;
        }

        return true;
    }

    private static int RoundBarIndexToNearestFourBarLineStart(int barIdx, int gridBarCount)
    {
        if (gridBarCount <= 0)
            return 0;

        var r = (int)(Math.Round(barIdx / 4.0) * 4);
        var lastLineStart = (gridBarCount - 1) / 4 * 4;
        if (r < 0)
            r = 0;
        if (r > lastLineStart)
            r = lastLineStart;

        return r;
    }

    /// <summary>
    /// 先頭・最終 4 小節行以外で、小節ごとのセグメント ID 変化（最寄り 4 小節行頭へ丸め）に応じた Verse / Chorus / Bridge 見出しを構築する。
    /// </summary>
    private static Dictionary<int, string> BuildMiddleSectionHeadersBySegmentIds(
        List<string[]> grid,
        List<double> barStartTimesSec,
        IReadOnlyList<SegmentBoundaryEvent> events)
    {
        var lastLineStart = grid.Count - 4;
        var barType = new int[grid.Count];
        for (var bi = 0; bi < grid.Count; bi++)
            barType[bi] = GetSegmentTypeIdAtBarStart(barStartTimesSec[bi], events);

        var lineToFirstBoundaryBar = new Dictionary<int, int>();
        for (var bi = 1; bi < grid.Count; bi++)
        {
            if (barType[bi] == barType[bi - 1])
                continue;

            var lineStartAlign = RoundBarIndexToNearestFourBarLineStart(bi, grid.Count);
            if (lineStartAlign == 0 || lineStartAlign == lastLineStart)
                continue;

            if (!lineToFirstBoundaryBar.ContainsKey(lineStartAlign))
                lineToFirstBoundaryBar[lineStartAlign] = bi;
        }

        var idToHeader = new Dictionary<int, string>();
        var verseSerial = 0;
        var bridgeSerial = 0;
        var result = new Dictionary<int, string>();
        foreach (var ls in lineToFirstBoundaryBar.Keys.OrderBy(k => k))
        {
            var bi = lineToFirstBoundaryBar[ls];
            var sid = barType[bi];
            if (!idToHeader.TryGetValue(sid, out var label))
            {
                var ix = idToHeader.Count;
                label = (ix % 5) switch
                {
                    0 or 2 or 4 => NextVerseSectionHeader(ref verseSerial),
                    1 => "[Chorus]",
                    3 => NextBridgeSectionHeader(ref bridgeSerial),
                    _ => "[Section]"
                };
                idToHeader[sid] = label;
            }

            result[ls] = label;
        }

        return result;
    }

    private static string NextVerseSectionHeader(ref int verseSerial)
    {
        verseSerial++;
        return $"[Verse {verseSerial}]";
    }

    private static string NextBridgeSectionHeader(ref int bridgeSerial)
    {
        bridgeSerial++;
        return bridgeSerial == 1 ? "[Bridge]" : $"[Bridge {bridgeSerial}]";
    }

    private static int GetSegmentTypeIdAtBarStart(double barStartSec, IReadOnlyList<SegmentBoundaryEvent> sorted)
    {
        if (sorted.Count == 0)
            return 0;

        var bestIdx = -1;
        for (var i = 0; i < sorted.Count; i++)
        {
            if (sorted[i].TimeSec <= barStartSec + 1e-4)
                bestIdx = i;
            else
                break;
        }

        if (bestIdx < 0)
            return sorted[0].SegmentTypeId;

        return sorted[bestIdx].SegmentTypeId;
    }

    private static double ComputeLastBarEndSec(List<double> barTimes, double maxChordT)
    {
        if (barTimes.Count == 0)
            return maxChordT + 1.0;

        double span;
        if (barTimes.Count >= 2)
            span = barTimes[^1] - barTimes[^2];
        else
            span = 2.0;

        if (span < 0.1)
            span = 0.5;

        return Math.Max(barTimes[^1] + span, maxChordT + 0.25);
    }

    private static int FindBarIndexForTime(double tSec, List<double> barTimes, double lastBarEnd)
    {
        if (barTimes.Count == 0)
            return 0;

        var bi = LastIndexWhereLessOrEqual(barTimes, tSec);
        if (bi < 0)
            bi = 0;

        if (tSec > lastBarEnd + 1e-6)
            bi = barTimes.Count - 1;

        if (bi >= barTimes.Count)
            bi = barTimes.Count - 1;

        return bi;
    }

    private static int LastIndexWhereLessOrEqual(List<double> sortedAsc, double value)
    {
        var lo = 0;
        var hi = sortedAsc.Count - 1;
        var ans = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (sortedAsc[mid] <= value)
            {
                ans = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return ans;
    }

    private static List<double> ParseAndNormalizeBarStartTimes(IReadOnlyList<string> csvLines)
    {
        var raw = new List<double>();
        foreach (var line in csvLines)
        {
            if (TryParseCsvLeadingTimeSeconds(line, out var sec) && sec >= 0 && !double.IsNaN(sec) && !double.IsInfinity(sec))
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

    private static List<SegmentBoundaryEvent> ParseSegmentBoundaryEvents(IReadOnlyList<string> csvLines)
    {
        var raw = new List<SegmentBoundaryEvent>();
        foreach (var line in csvLines)
        {
            if (TryParseCsvSegmentEvent(line, out var t, out var id))
                raw.Add(new SegmentBoundaryEvent(t, id));
        }

        if (raw.Count == 0)
            return raw;

        raw.Sort((a, b) =>
        {
            var c = a.TimeSec.CompareTo(b.TimeSec);
            return c != 0 ? c : a.SegmentTypeId.CompareTo(b.SegmentTypeId);
        });

        const double eps = 1e-4;
        var merged = new List<SegmentBoundaryEvent> { raw[0] };
        for (var i = 1; i < raw.Count; i++)
        {
            if (Math.Abs(raw[i].TimeSec - merged[^1].TimeSec) <= eps)
                merged[^1] = raw[i];
            else
                merged.Add(raw[i]);
        }

        return merged;
    }

    private static bool TryParseCsvSegmentEvent(string line, out double timeSec, out int segmentTypeId)
    {
        segmentTypeId = 0;
        if (!TryParseCsvLeadingTimeSeconds(line, out timeSec))
            return false;

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
            return false;

        var parts = trimmed.Split(',');
        if (parts.Length <= 1)
            return true;

        for (var c = parts.Length - 1; c >= 1; c--)
        {
            var p = parts[c].Trim().Trim('"');
            if (int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                segmentTypeId = id;
                return true;
            }

            if (double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
                !double.IsNaN(d) && !double.IsInfinity(d))
            {
                segmentTypeId = (int)Math.Round(d);
                return true;
            }
        }

        return true;
    }

    private static bool TryParseCsvLeadingTimeSeconds(string line, out double sec)
    {
        sec = 0;
        if (string.IsNullOrWhiteSpace(line))
            return false;

        var trimmed = line.Trim();
        if (trimmed.StartsWith('#'))
            return false;

        var quoted = MyRegex3().Match(trimmed);
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

    /// <summary>
    /// 任意の VAMP 出力を CSV で標準出力に取り込む（Bar / Segmenter 等）。<c>-d</c> に続けてプラグイン出力記述子・音声パスを渡す。
    /// </summary>
    private static async Task<(int ExitCode, string? Stderr, List<string> Lines)> RunSonicVampCsvLinesAsync(
        string exePath,
        string audioPath,
        string vampOutputDescriptor,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        var toolDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(toolDir))
            toolDir = AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = toolDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(vampOutputDescriptor);
        psi.ArgumentList.Add(audioPath);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("csv");
        psi.ArgumentList.Add("--csv-stdout");

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PATH"] = string.IsNullOrEmpty(pathEnv)
            ? toolDir
            : toolDir + Path.PathSeparator + pathEnv;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    break;

                lines.Add(line);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try
            {
                await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                /* 無視 */
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr))
            Debug.WriteLine(stderr);

        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? null : stderr, lines);
    }

    private static async Task<(int ExitCode, string? Stderr, List<ChordPoint> Chords)> RunSonicAnnotatorAsync(
        string exePath,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        var chords = new List<ChordPoint>();

        var toolDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(toolDir))
            toolDir = AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = toolDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("vamp:nnls-chroma:chordino:simplechord");
        psi.ArgumentList.Add(audioPath);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("csv");
        psi.ArgumentList.Add("--csv-stdout");

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PATH"] = string.IsNullOrEmpty(pathEnv)
            ? toolDir
            : toolDir + Path.PathSeparator + pathEnv;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    break;

                var point = ParseCsvChordLine(line);
                if (point.HasValue)
                    chords.Add(point.Value);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try
            {
                await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                /* 無視 */
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr))
            Debug.WriteLine(stderr);

        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? null : stderr, chords);
    }

    private static readonly Regex TempoCsvFloatRegex = MyRegex1();

    /// <summary>
    /// Sonic Annotator + qm-tempotracker:tempo で BPM を推定する。
    /// </summary>
    private static async Task<(int ExitCode, string? Stderr, double? Bpm)> EstimateBpmAsync(
        string exePath,
        string audioPath,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        var toolDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(toolDir))
            toolDir = AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = toolDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add("vamp:qm-vamp-plugins:qm-tempotracker:tempo");
        psi.ArgumentList.Add(audioPath);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("csv");
        psi.ArgumentList.Add("--csv-stdout");

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PATH"] = string.IsNullOrEmpty(pathEnv)
            ? toolDir
            : toolDir + Path.PathSeparator + pathEnv;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    break;

                lines.Add(line);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try
            {
                await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                /* 無視 */
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr))
            Debug.WriteLine(stderr);

        var bpm = ParseGlobalTempoBpmFromCsvLines(lines);
        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? null : stderr, bpm);
    }

    /// <summary>
    /// CSV 行に含まれる 30〜320 の数値候補から代表 BPM（中央値）を取る。
    /// </summary>
    private static double? ParseGlobalTempoBpmFromCsvLines(IReadOnlyList<string> lines)
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

    private static ChordPoint? ParseCsvChordLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            return null;

        var quoted = MyRegex3().Match(trimmed);
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

    private static string DescribeWindowsExitCode(int exitCode)
    {
        unchecked
        {
            var u = (uint)exitCode;
            return u switch
            {
                0xC0000135 => "必要な DLL が見つかりません（プラグイン DLL や VC++ 再頒布可能パッケージ、作業フォルダを確認）。",
                0xC000007B => "実行形式が無効です（32/64 ビットの不一致など）。",
                0xC0000142 => "DLL の初期化に失敗しました。",
                _ => "sonic-annotator 同梱の DLL / vamp プラグイン、および Microsoft Visual C++ 再頒布可能パッケージを確認してください。"
            };
        }
    }

    private static string QuoteForArgument(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "\"\"";

        return path.Contains('"', StringComparison.Ordinal)
            ? "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\""
            : "\"" + path + "\"";
    }

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

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Chordpro (*.chordpro;*.pro)|*.chordpro;*.pro|すべてのファイル (*.*)|*.*",
            DefaultExt = ".chordpro",
            Title = "Chordpro として保存"
        };

        if (dlg.ShowDialog() != true)
            return;

        try
        {
            File.WriteAllText(
                dlg.FileName,
                ChordproOutputTextBox.Text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusLabel.Text = $"保存しました: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@",\s*([\d.Ee+-]+)\s*,\s*""([^""]*)""\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex3();
}
