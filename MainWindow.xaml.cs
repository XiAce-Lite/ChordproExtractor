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
using Microsoft.Win32;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Whisper.net;

namespace ChordproExtractor;

public partial class MainWindow : Window
{
    private string? _selectedAudioPath;
    private bool _isProcessing;
    private CancellationTokenSource? _cts;

    private readonly record struct ChordPoint(double Seconds, string RawLabel);

    /// <summary>Whisper 由来の単語／文字とタイムスタンプ（秒）。</summary>
    public readonly record struct WhisperWordToken(double StartSec, double EndSec, string Text);

    public MainWindow()
    {
        InitializeComponent();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
    }

    private string GetSelectedWhisperLanguageCode()
    {
        if (WhisperLanguageComboBox.SelectedItem is ComboBoxItem item &&
            item.Tag is string tag &&
            !string.IsNullOrWhiteSpace(tag))
            return tag;

        return "ja";
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

        _selectedAudioPath = path;
        AudioPathTextBox.Text = path;
        UpdateDurationLabel();
        StatusLabel.Text = "準備完了。変換で Demucs → Chordino（伴奏）。チェックを入れると Whisper（歌声）→ マージも行います。";
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

        var useWhisper = UseWhisperLyricsCheckBox.IsChecked == true;

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

        string? modelPath = null;
        if (useWhisper)
        {
            modelPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "ggml-medium.bin"));
            if (!File.Exists(modelPath))
            {
                StatusLabel.Text = $"Whisper モデルが見つかりません: {modelPath}";
                MessageBox.Show(
                    this,
                    "tools/ggml-medium.bin を配置するか、左のチェックを外してコードのみの変換にしてください。",
                    "Whisper モデル未配置",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }
        }

        _isProcessing = true;
        ConvertButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        SaveButton.IsEnabled = false;
        WhisperLanguageComboBox.IsEnabled = false;
        BpmTextBox.IsEnabled = false;
        ChordproTextBox.Clear();
        WhisperPreviewTextBox.Clear();
        MainProgressBar.IsIndeterminate = true;
        MainProgressBar.Value = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        string? demucsWorkDir = null;
        string? whisperResampledPath = null;
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
                        ChordproTextBox.Text = errBody;

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

            var secondsPerFourBars = 960.0 / bpmValue;

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
                            ChordproTextBox.Text = demucsStderr.Trim();
                        return;
                    }

                    var hint = DescribeWindowsExitCode(demucsExit);
                    StatusLabel.Text =
                        $"Demucs が失敗しました（終了コード {demucsExit}）。{hint}";

                    var errBody = BuildProcessErrorBody(demucsStdout, demucsStderr);
                    if (!string.IsNullOrEmpty(errBody))
                        ChordproTextBox.Text = errBody;

                    return;
                }

                if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out _, out _))
                {
                    StatusLabel.Text =
                        "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。出力フォルダ構成を確認してください。";
                    ChordproTextBox.Text = "探索ルート: " + demucsWorkDir;
                    return;
                }

                DemucsWorkCache.WriteSourceMeta(demucsWorkDir, _selectedAudioPath!);
            }

            if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out var vocalsWav, out var noVocalsWav))
            {
                StatusLabel.Text =
                    "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。キャッシュが壊れている可能性があります。";
                ChordproTextBox.Text = "探索ルート: " + demucsWorkDir;
                return;
            }

            StatusLabel.Text = "Chordino（伴奏トラック）を実行しています…";
            var (exitCode, stderr, chords) = await RunSonicAnnotatorAsync(toolPath, noVocalsWav, token).ConfigureAwait(true);

            if (exitCode != 0)
            {
                var hint = DescribeWindowsExitCode(exitCode);
                StatusLabel.Text =
                    $"Chordino が失敗しました（終了コード {exitCode}）。{hint}";

                if (!string.IsNullOrWhiteSpace(stderr))
                    ChordproTextBox.Text = "[標準エラー出力]" + Environment.NewLine + stderr.Trim();

                return;
            }

            chords.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

            var preamble = ChordproPreamble.Build(_selectedAudioPath!, bpmValue, TryGuessKeyFromChords(chords));

            if (!useWhisper)
            {
                WhisperPreviewTextBox.Clear();
                StatusLabel.Text = "Chordpro 用にコードを並べています…";
                ChordproTextBox.Text = preamble + BuildChordproFromChordsOnly(chords, secondsPerFourBars);
                OutputTabControl.SelectedIndex = 0;
                StatusLabel.Text = reusedDemucsCache
                    ? "Demucs（キャッシュ再利用）+ Chordino の処理が完了しました。保存できます。"
                    : "Demucs + Chordino の処理が完了しました。保存できます。";
                return;
            }

            whisperResampledPath = Path.Combine(demucsWorkDir, "whisper_input_16k_mono.wav");
            StatusLabel.Text = "Whisper 用に 16kHz / 16bit / モノラル WAV に変換しています…";
            token.ThrowIfCancellationRequested();
            await Task.Run(() => WriteWhisperInputWaveFile16kMono16(vocalsWav, whisperResampledPath!), token)
                .ConfigureAwait(true);

            var langCode = GetSelectedWhisperLanguageCode();
            StatusLabel.Text = "Whisper.net（16kHz ボーカル WAV）で文字起こし中…";

            List<WhisperWordToken> whisperWords;
            try
            {
                whisperWords = await Task.Run(
                        () => CollectWhisperWordTokensAsync(modelPath!, whisperResampledPath!, langCode, token),
                        token)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"Whisper エラー: {ex.Message}";
                ChordproTextBox.Text = ex.ToString();
                return;
            }

            WhisperPreviewTextBox.Text = FormatWhisperPreview(whisperWords);
            OutputTabControl.SelectedIndex = 1;

            StatusLabel.Text = "コードと歌詞をマージしています…";
            var merged = MergeChordsWithWhisperTokens(chords, whisperWords, secondsPerFourBars);
            ChordproTextBox.Text = preamble + merged;
            OutputTabControl.SelectedIndex = 0;

            StatusLabel.Text = reusedDemucsCache
                ? "Demucs（キャッシュ再利用）+ Chordino + Whisper の処理が完了しました。保存できます。"
                : "Demucs + Chordino + Whisper の処理が完了しました。保存できます。";
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "キャンセルされました。";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"エラー: {ex.Message}";
            ChordproTextBox.Text = ex.ToString();
        }
        finally
        {
            TryDeleteFileIfExists(whisperResampledPath);

            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = MainProgressBar.Maximum;
            ConvertButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            SaveButton.IsEnabled = true;
            WhisperLanguageComboBox.IsEnabled = true;
            BpmTextBox.IsEnabled = true;
            _isProcessing = false;
            _cts?.Dispose();
            _cts = null;
        }
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

    /// <summary>whisper.cpp のトークン時刻（整数）を秒へ。</summary>
    private static double WhisperNativeTimeToSeconds(long native) => native * 10.0 / 1000.0;

    /// <summary>
    /// Whisper 入力用: 16kHz / 16bit / モノラル PCM WAV を一時ファイルに書き出す。
    /// まず MediaFoundationResampler を試し、失敗時は WDL リサンプラにフォールバックする。
    /// </summary>
    private static void WriteWhisperInputWaveFile16kMono16(string sourcePath, string destinationWavPath)
    {
        var dir = Path.GetDirectoryName(destinationWavPath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var targetFormat = new WaveFormat(16000, 16, 1);

        try
        {
            using var reader = new AudioFileReader(sourcePath);
            using var resampler = new MediaFoundationResampler(reader, targetFormat) { ResamplerQuality = 60 };
            WaveFileWriter.CreateWaveFile(destinationWavPath, resampler);
            return;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MediaFoundationResampler 失敗、WDL にフォールバック: {ex.Message}");
        }

        TryDeleteFileIfExists(destinationWavPath);

        using (var reader = new AudioFileReader(sourcePath))
        {
            var wave = reader.ToSampleProvider();
            if (reader.WaveFormat.Channels > 1)
                wave = wave.ToMono();

            var resampled = new WdlResamplingSampleProvider(wave, 16000);
            WaveFileWriter.CreateWaveFile(destinationWavPath, resampled.ToWaveProvider16());
        }
    }

    /// <summary>
    /// Whisper.net: トークン時刻 + SplitOnWord。言語は引数で指定。16kHz モノラル WAV ファイルを入力とする。
    /// </summary>
    private static async Task<List<WhisperWordToken>> CollectWhisperWordTokensAsync(
        string modelPath,
        string whisperPcmWavPath,
        string languageCode,
        CancellationToken cancellationToken)
    {
        await using var wavStream = new FileStream(
            whisperPcmWavPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        using var whisperFactory = WhisperFactory.FromPath(modelPath);

        using var processor = whisperFactory.CreateBuilder()
            .WithLanguage(languageCode)
            .WithTokenTimestamps()
            .SplitOnWord()
            .WithSuppressRegex(@"\[_TT_\d+\]|\[_BEG_\]|\[_END_\]|_TT_|_BEG_|_END_")
            .Build();

        var pool = new List<WhisperWordToken>(512);

        await foreach (var segment in processor.ProcessAsync(wavStream, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (segment.Tokens is null)
                continue;

            foreach (var tok in segment.Tokens)
            {
                var piece = tok.Text?.Trim('\r', '\n', ' ', '\u00a0') ?? string.Empty;
                if (!ShouldEmitWhisperTokenPiece(piece))
                    continue;

                var startSec = WhisperNativeTimeToSeconds(tok.Start);
                var endSec = WhisperNativeTimeToSeconds(tok.End);
                if (endSec < startSec)
                    (startSec, endSec) = (endSec, startSec);

                if (ContainsCjkKanaOrHangul(piece))
                {
                    foreach (var w in ExpandTextToCharTokens(startSec, endSec, piece))
                        pool.Add(w);
                }
                else
                {
                    pool.Add(new WhisperWordToken(startSec, endSec, piece));
                }
            }
        }

        return pool;
    }

    private static readonly Regex WhisperBracketTimestamp = MyRegex();

    /// <summary>[_BEG_] のように [ で始まり _…_ ] で閉じる短いメタ用。日本語の [ 音…] にはマッチしない。</summary>
    private static readonly Regex WhisperBracketUnderscoreMeta = MyRegex2();

    private static bool ContainsCjkKanaOrHangul(string s)
    {
        foreach (var c in s)
        {
            switch (c)
            {
                case >= '\u3040' and <= '\u309f': // Hiragana
                case >= '\u30a0' and <= '\u30ff': // Katakana
                    return true;
                case >= '\u4e00' and <= '\u9fff': // CJK Unified Ideographs
                    return true;
                case >= '\uac00' and <= '\ud7a3': // Hangul syllables
                    return true;
                default:
                    continue;
            }
        }

        return false;
    }

    /// <summary>
    /// トークン時刻用のメタ（[_BEG_] / [_TT_n]）や、それを文字分割した断片をプレビュー・マージから除外する。
    /// </summary>
    private static bool ShouldEmitWhisperTokenPiece(string piece)
    {
        var t = piece.Trim('\r', '\n', ' ', '\u00a0');
        if (t.Length == 0)
            return false;

        if (WhisperBracketTimestamp.IsMatch(t))
            return false;
        if (WhisperBracketUnderscoreMeta.IsMatch(t))
            return false;

        if (t.Contains("_TT_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (t.Contains("_BEG_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (t.Contains("_END_", StringComparison.OrdinalIgnoreCase))
            return false;
        if (t.Contains("<|", StringComparison.Ordinal))
            return false;
        if (t.Contains("|>", StringComparison.Ordinal))
            return false;

        // メタを 1 文字ずつ分割した断片（[, ], _, B, E, G, T, 数字）
        if (t.Length == 1)
        {
            var c = t[0];
            if (c is '[' or ']' or '_')
                return false;
            if (char.IsAsciiLetterOrDigit(c))
                return false;
        }

        return true;
    }

    private static IEnumerable<WhisperWordToken> ExpandTextToCharTokens(double startSec, double endSec, string text)
    {
        var span = endSec - startSec;
        if (span < 0)
            span = 0;

        if (text.Length == 0)
            yield break;

        if (text.Length == 1)
        {
            if (ShouldEmitWhisperTokenPiece(text))
                yield return new WhisperWordToken(startSec, endSec, text);
            yield break;
        }

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text.Substring(i, 1);
            if (!ShouldEmitWhisperTokenPiece(ch))
                continue;

            var s = startSec + span * i / text.Length;
            var e = startSec + span * (i + 1) / text.Length;
            yield return new WhisperWordToken(s, e, ch);
        }
    }

    private static string FormatWhisperPreview(IReadOnlyList<WhisperWordToken> tokens)
    {
        var sb = new StringBuilder();
        foreach (var t in tokens)
        {
            var start = TimeSpan.FromSeconds(t.StartSec);
            var end = TimeSpan.FromSeconds(t.EndSec);
            sb.Append(start.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture))
                .Append(" → ")
                .Append(end.ToString(@"mm\:ss\.fff", CultureInfo.InvariantCulture))
                .Append('\t')
                .AppendLine(t.Text);
        }

        return sb.ToString();
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

    private static void AppendNewlinesForFourBarBoundaries(StringBuilder sb, ref double nextBoundarySec, double timeSec,
        double secondsPerFourBars)
    {
        if (secondsPerFourBars <= 0 || double.IsNaN(secondsPerFourBars) || double.IsInfinity(secondsPerFourBars))
            return;

        while (timeSec >= nextBoundarySec)
        {
            sb.AppendLine();
            nextBoundarySec += secondsPerFourBars;
        }
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
    /// Whisper トークン連結時、ASCII 英字同士の境にだけスペースを入れて run-on を防ぐ。
    /// </summary>
    private static void AppendWhisperMergedText(StringBuilder sb, string text)
    {
        if (text.Length == 0)
            return;

        if (sb.Length > 0)
        {
            var last = sb[^1];
            var first = text[0];
            if (char.IsAsciiLetter(last) && char.IsAsciiLetter(first))
                sb.Append(' ');
        }

        sb.Append(text);
    }

    /// <summary>
    /// 歌詞なし: Chordino のイベントを時刻順にたどり、表示用コードが変わったときだけ [chord] を並べる。
    /// 4 小節ごとに改行する。
    /// </summary>
    private static string BuildChordproFromChordsOnly(IReadOnlyList<ChordPoint> sortedChords, double secondsPerFourBars)
    {
        if (sortedChords.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        string? lastDisp = null;
        var nextBoundary = secondsPerFourBars;
        foreach (var p in sortedChords)
        {
            var disp = FormatChordForDisplay(p.RawLabel);
            if (disp == null)
                continue;

            if (!string.Equals(disp, lastDisp, StringComparison.Ordinal))
            {
                AppendNewlinesForFourBarBoundaries(sb, ref nextBoundary, p.Seconds, secondsPerFourBars);
                sb.Append('[').Append(disp).Append(']');
                lastDisp = disp;
            }
        }

        return sb.ToString();
    }

    private static string MergeChordsWithWhisperTokens(
        IReadOnlyList<ChordPoint> chordsSorted,
        List<WhisperWordToken> whisperTokens,
        double secondsPerFourBars)
    {
        if (whisperTokens.Count == 0)
            return string.Empty;

        var sb = new StringBuilder();
        string? lastEmittedChordDisplay = null;
        var nextBoundary = secondsPerFourBars;

        foreach (var w in whisperTokens)
        {
            AppendNewlinesForFourBarBoundaries(sb, ref nextBoundary, w.StartSec, secondsPerFourBars);

            var active = FindChordActiveAtTimeForMerge(chordsSorted, w.StartSec);
            var disp = active.HasValue ? FormatChordForDisplay(active.Value.RawLabel) : null;

            if (!string.Equals(disp, lastEmittedChordDisplay, StringComparison.Ordinal))
            {
                if (disp != null)
                    sb.Append('[').Append(disp).Append(']');

                lastEmittedChordDisplay = disp;
            }

            AppendWhisperMergedText(sb, w.Text);
        }

        return sb.ToString();
    }

    private static ChordPoint? FindLastChordAtOrBefore(IReadOnlyList<ChordPoint> sorted, double timeSec)
    {
        if (sorted.Count == 0)
            return null;

        var lo = 0;
        var hi = sorted.Count - 1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            var t = sorted[mid].Seconds;
            if (t <= timeSec)
            {
                if (mid == sorted.Count - 1 || sorted[mid + 1].Seconds > timeSec)
                    return sorted[mid];
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return null;
    }

    /// <summary>
    /// マージ用: 指定時刻以前に確定した最後のコード。無い場合は Chordino の先頭行を曲頭のコードとして使う
    /// （先頭無音などで最初の歌詞が最初のコードイベントより前でも、開幕コードを出す）。
    /// </summary>
    private static ChordPoint? FindChordActiveAtTimeForMerge(IReadOnlyList<ChordPoint> sorted, double timeSec)
    {
        if (sorted.Count == 0)
            return null;

        var atOrBefore = FindLastChordAtOrBefore(sorted, timeSec);
        return atOrBefore ?? sorted[0];
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
                ChordproTextBox.Text,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            StatusLabel.Text = $"保存しました: {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [GeneratedRegex(@"^\[_TT_\d+\]$", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex();
    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex1();
    [GeneratedRegex(@"^\[_[A-Za-z0-9]+_\]$", RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex2();
    [GeneratedRegex(@",\s*([\d.Ee+-]+)\s*,\s*""([^""]*)""\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex MyRegex3();
}
