using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using NAudio.Wave;

namespace ChordproExtractor;

public partial class MainWindow : Window
{
    private string? _selectedAudioPath;
    private bool _isProcessing;

    public MainWindow()
    {
        InitializeComponent();
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
        StatusLabel.Text = "準備完了。「Chordino で変換」で解析を開始します。";
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

        var toolPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "sonic-annotator.exe"));
        if (!File.Exists(toolPath))
        {
            StatusLabel.Text = $"Sonic Annotator が見つかりません: {toolPath}";
            return;
        }

        _isProcessing = true;
        ConvertButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ChordproTextBox.Clear();
        MainProgressBar.IsIndeterminate = true;
        MainProgressBar.Value = 0;
        StatusLabel.Text = "Sonic Annotator を実行しています…";

        try
        {
            var (exitCode, stderr) = await RunSonicAnnotatorAsync(toolPath, _selectedAudioPath);

            if (exitCode != 0)
            {
                var hint = DescribeWindowsExitCode(exitCode);
                StatusLabel.Text =
                    $"処理が失敗しました（終了コード {exitCode}）。{hint}";

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    ChordproTextBox.Text =
                        "[標準エラー出力]" + Environment.NewLine +
                        stderr.Trim() + Environment.NewLine + Environment.NewLine +
                        ChordproTextBox.Text;
                }
            }
            else
            {
                StatusLabel.Text = "解析が完了しました。内容を編集して保存できます。";
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"エラー: {ex.Message}";
            ChordproTextBox.Text = ex.ToString();
        }
        finally
        {
            MainProgressBar.IsIndeterminate = false;
            MainProgressBar.Value = MainProgressBar.Maximum;
            ConvertButton.IsEnabled = true;
            SaveButton.IsEnabled = true;
            _isProcessing = false;
        }
    }

    private async Task<(int ExitCode, string? Stderr)> RunSonicAnnotatorAsync(string exePath, string audioPath)
    {
        var args =
            "-d vamp:nnls-chroma:chordino:simplechord " +
            QuoteForArgument(audioPath) +
            " -w csv --csv-stdout";

        var toolDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(toolDir))
            toolDir = AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = toolDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PATH"] = string.IsNullOrEmpty(pathEnv)
            ? toolDir
            : toolDir + Path.PathSeparator + pathEnv;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync();

        string? line;
        while ((line = await process.StandardOutput.ReadLineAsync()) != null)
        {
            var chordLine = ParseCsvLineToChordproSkeleton(line);
            if (chordLine is null)
                continue;

            var text = chordLine + Environment.NewLine;
            await Dispatcher.InvokeAsync(() =>
            {
                ChordproTextBox.AppendText(text);
                ChordproTextBox.CaretIndex = ChordproTextBox.Text.Length;
                ChordproTextBox.ScrollToEnd();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }

        await process.WaitForExitAsync();
        var stderr = await stderrTask;
        if (!string.IsNullOrWhiteSpace(stderr))
            Debug.WriteLine(stderr);

        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? null : stderr);
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

    private static string? ParseCsvLineToChordproSkeleton(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var trimmed = line.Trim();
        if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
            return null;

        var quoted = Regex.Match(
            trimmed,
            @",\s*([\d.Ee+-]+)\s*,\s*""([^""]*)""\s*$",
            RegexOptions.CultureInvariant);
        if (quoted.Success)
        {
            var chordPart = quoted.Groups[2].Value;
            if (string.IsNullOrEmpty(chordPart))
                return null;

            var chord = FormatChordForDisplay(chordPart);
            if (chord is null)
                return null;

            return $"[{chord}] ──── ";
        }

        var comma = trimmed.IndexOf(',', StringComparison.Ordinal);
        if (comma <= 0 || comma >= trimmed.Length - 1)
            return null;

        var timeStr = trimmed[..comma].Trim().Trim('"');
        if (!double.TryParse(timeStr, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return null;

        var chordPart2 = trimmed[(comma + 1)..].Trim().Trim('"');
        if (string.IsNullOrEmpty(chordPart2))
            return null;

        var chord2 = FormatChordForDisplay(chordPart2);
        if (chord2 is null)
            return null;

        return $"[{chord2}] ──── ";
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
}
