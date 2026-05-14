using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;

namespace ChordproExtractor;

public partial class MainWindow : Window
{
    /// <summary>QM Bar and Beat Tracker の「小節線」出力（公式プラグイン名は barbeattracker）。</summary>
    private const string VampQmBarBeatTrackerBars = "vamp:qm-vamp-plugins:qm-barbeattracker:bars";

    private string? _selectedAudioPath;
    private bool _isProcessing;
    private CancellationTokenSource? _cts;

    private readonly record struct ChordPoint(double Seconds, string RawLabel);

    /// <summary>コードパレット 1 行（挿入文字列と、クリック済みの ✓ 表示用）。</summary>
    private sealed record ChordPaletteEntry(string Insert, TextBlock UsedCheckGlyph);

    /// <summary>直近の解析成功時のコード列（{key:} 推定・パレット用）。</summary>
    private List<ChordPoint>? _lastChordParseResult;

    /// <summary>直近の解析で確定した BPM（プリアンブル用フォールバック）。</summary>
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
        StatusLabel.Text = "準備完了。解析で BPM とコードパレットを生成し、歌詞へクリックでコードを挿入できます。";
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
        UpdateChordproPreviewButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SaveButton.IsEnabled = false;
        BpmTextBox.IsEnabled = false;
        _lastChordParseResult = null;
        _lastSuccessfulBpm = 0;
        ChordPalettePanel.Children.Clear();
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
                        ChordproOutputTextBox.Text = errBody;

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
                            ChordproOutputTextBox.Text = demucsStderr.Trim();
                        return;
                    }

                    var hint = DescribeWindowsExitCode(demucsExit);
                    StatusLabel.Text =
                        $"Demucs が失敗しました（終了コード {demucsExit}）。{hint}";

                    var errBody = BuildProcessErrorBody(demucsStdout, demucsStderr);
                    if (!string.IsNullOrEmpty(errBody))
                        ChordproOutputTextBox.Text = errBody;

                    return;
                }

                if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out _, out _))
                {
                    StatusLabel.Text =
                        "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。出力フォルダ構成を確認してください。";
                    ChordproOutputTextBox.Text = "探索ルート: " + demucsWorkDir;
                    return;
                }

                DemucsWorkCache.WriteSourceMeta(demucsWorkDir, _selectedAudioPath!);
            }

            if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out _, out var noVocalsWav))
            {
                StatusLabel.Text =
                    "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。キャッシュが壊れている可能性があります。";
                ChordproOutputTextBox.Text = "探索ルート: " + demucsWorkDir;
                return;
            }

            StatusLabel.Text = "Chordino と Bar Tracker を並列実行しています…";
            var chordTask = RunSonicAnnotatorAsync(toolPath, noVocalsWav, token);
            var barTask = RunSonicVampCsvLinesAsync(toolPath, noVocalsWav, VampQmBarBeatTrackerBars, token);
            await Task.WhenAll(chordTask, barTask).ConfigureAwait(true);

            var (exitCode, stderr, chords) = await chordTask.ConfigureAwait(true);
            var (barExit, _, barLines) = await barTask.ConfigureAwait(true);

            if (exitCode != 0)
            {
                var hint = DescribeWindowsExitCode(exitCode);
                StatusLabel.Text =
                    $"Chordino が失敗しました（終了コード {exitCode}）。{hint}";

                if (!string.IsNullOrWhiteSpace(stderr))
                    ChordproOutputTextBox.Text = "[標準エラー出力]" + Environment.NewLine + stderr.Trim();

                return;
            }

            var warnParts = new List<string>();
            if (barExit != 0)
                warnParts.Add($"Bar Tracker 失敗（終了コード {barExit}）。小節番号は時刻表示に切り替わります。");

            chords.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

            var barStarts = ParseAndNormalizeBarStartTimes(barLines);

            if (barExit == 0 && barStarts.Count == 0 && barLines.Count > 0)
                warnParts.Add("Bar Tracker の CSV から小節時刻を解釈できませんでした。");

            StatusLabel.Text = "コードパレットを組み立てています…";
            PopulateChordPalette(chords, barStarts);
            _lastChordParseResult = [.. chords];
            _lastSuccessfulBpm = bpmValue;
            UpdateChordproPreview();

            var okMsg = reusedDemucsCache
                ? "Demucs（キャッシュ再利用）+ Chordino が完了しました。右のコードをクリックして歌詞へ挿入できます。"
                : "Demucs + Chordino が完了しました。右のコードをクリックして歌詞へ挿入できます。";
            StatusLabel.Text = warnParts.Count > 0 ? okMsg + " " + string.Join(" ", warnParts) : okMsg;
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "キャンセルされました。";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"エラー: {ex.Message}";
            ChordproOutputTextBox.Text = ex.ToString();
        }
        finally
        {
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = MainProgressBar.Maximum;
            ConvertButton.IsEnabled = true;
            UpdateChordproPreviewButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            SaveButton.IsEnabled = true;
            BpmTextBox.IsEnabled = true;
            _isProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void UpdateChordproPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateChordproPreview();
        StatusLabel.Text = "Chordpro プレビューを更新しました。";
    }

    private void UpdateChordproPreview()
    {
        ChordproOutputTextBox.Text = BuildChordproDocumentBody();
    }

    private string BuildChordproDocumentBody()
    {
        var bpmForPreamble = TryParseUserBpm(BpmTextBox.Text, out var manualBpm)
            ? manualBpm
            : (_lastSuccessfulBpm > 0 ? _lastSuccessfulBpm : double.NaN);

        var keyGuess = _lastChordParseResult != null ? TryGuessKeyFromChords(_lastChordParseResult) : null;
        return ChordproPreamble.Build(_selectedAudioPath ?? string.Empty, bpmForPreamble, keyGuess) + LyricsTextBox.Text;
    }

    private void PopulateChordPalette(IReadOnlyList<ChordPoint> sortedChords, List<double> barStartTimesSec)
    {
        ChordPalettePanel.Children.Clear();
        if (sortedChords.Count == 0)
            return;

        var maxChordT = 0.0;
        foreach (var p in sortedChords)
        {
            if (p.Seconds > maxChordT)
                maxChordT = p.Seconds;
        }

        var items = new List<(int barIdx, double tSec, string insert)>();
        if (barStartTimesSec.Count == 0)
        {
            foreach (var p in sortedChords)
            {
                var disp = FormatChordForDisplay(p.RawLabel);
                if (disp == null)
                    continue;

                var insert = '[' + disp + ']';
                items.Add((-1, p.Seconds, insert));
            }
        }
        else
        {
            var lastBarEnd = ComputeLastBarEndSec(barStartTimesSec, maxChordT);
            foreach (var p in sortedChords)
            {
                var disp = FormatChordForDisplay(p.RawLabel);
                if (disp == null)
                    continue;

                var insert = '[' + disp + ']';
                var bi = FindBarIndexForTime(p.Seconds, barStartTimesSec, lastBarEnd);
                items.Add((bi, p.Seconds, insert));
            }
        }

        items.Sort((a, b) =>
        {
            var c = a.tSec.CompareTo(b.tSec);
            return c != 0 ? c : string.CompareOrdinal(a.insert, b.insert);
        });

        foreach (var (barIdx, tSec, insert) in items)
        {
            var label = barIdx < 0
                ? $"{FormatChordPaletteTime(tSec)}  {insert}"
                : $"Bar {barIdx + 1}: {insert}";

            var usedCheck = new TextBlock
            {
                Text = string.Empty,
                MinWidth = 22,
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.DarkGreen,
                FontSize = 14,
                FontWeight = FontWeights.SemiBold
            };
            var labelBlock = new TextBlock
            {
                Text = label,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center
            };
            var inner = new Grid();
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            inner.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(labelBlock, 0);
            Grid.SetColumn(usedCheck, 1);
            inner.Children.Add(labelBlock);
            inner.Children.Add(usedCheck);

            var btn = new Button
            {
                Content = inner,
                Tag = new ChordPaletteEntry(insert, usedCheck),
                Margin = new Thickness(0, 0, 0, 6),
                Padding = new Thickness(8, 6, 8, 6),
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                ToolTip = barIdx < 0 ? $"{tSec:0.###} s" : $"Bar {barIdx + 1}（{tSec:0.###} s）"
            };
            btn.Click += ChordPaletteButton_Click;
            ChordPalettePanel.Children.Add(btn);
        }
    }

    private static string FormatChordPaletteTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void ChordPaletteButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ChordPaletteEntry entry } || entry.Insert.Length == 0)
            return;

        var tb = LyricsTextBox;
        var idx = tb.CaretIndex;
        if (idx < 0)
            idx = 0;
        if (idx > tb.Text.Length)
            idx = tb.Text.Length;

        tb.Text = tb.Text.Insert(idx, entry.Insert);
        tb.CaretIndex = idx + entry.Insert.Length;
        tb.Focus();
        entry.UsedCheckGlyph.Text = "\u2713";
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
