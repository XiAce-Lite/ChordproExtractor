using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NAudio.Wave;

namespace ChordproExtractor;

public partial class MainWindow : Window
{
    private string? _selectedAudioPath;
    private bool _isProcessing;
    private CancellationTokenSource? _cts;

    private List<ChordPoint>? _lastChordParseResult;
    private double _lastSuccessfulBpm;

    private readonly AppUserSettings _settings = AppUserSettings.Load();
    private readonly ObservableCollection<ChordPaletteItemVm> _chordPaletteItems = new();

    public MainWindow()
    {
        InitializeComponent();
        ChordPaletteList.ItemsSource = _chordPaletteItems;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settings.WindowLeft.HasValue && _settings.WindowTop.HasValue &&
            _settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
        {
            Left = _settings.WindowLeft.Value;
            Top = _settings.WindowTop.Value;
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;
        }

        if (_settings.ColumnStars is { Length: 3 } stars)
        {
            MainEditorGrid.ColumnDefinitions[0].Width = new GridLength(stars[0], GridUnitType.Star);
            MainEditorGrid.ColumnDefinitions[2].Width = new GridLength(stars[1], GridUnitType.Star);
            MainEditorGrid.ColumnDefinitions[4].Width = new GridLength(stars[2], GridUnitType.Star);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _settings.WindowLeft = Left;
        _settings.WindowTop = Top;
        _settings.WindowWidth = Width;
        _settings.WindowHeight = Height;
        _settings.ColumnStars =
        [
            MainEditorGrid.ColumnDefinitions[0].Width.IsStar ? MainEditorGrid.ColumnDefinitions[0].Width.Value : 2,
            MainEditorGrid.ColumnDefinitions[2].Width.IsStar ? MainEditorGrid.ColumnDefinitions[2].Width.Value : 1.15,
            MainEditorGrid.ColumnDefinitions[4].Width.IsStar ? MainEditorGrid.ColumnDefinitions[4].Width.Value : 1.35
        ];
        _settings.Save();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "音声ファイル (*.mp3;*.wav)|*.mp3;*.wav|すべてのファイル (*.*)|*.*",
            Title = "音声ファイルを選択",
            InitialDirectory = GetBrowseInitialDirectory()
        };

        if (dlg.ShowDialog() == true)
        {
            _settings.LastBrowseDirectory = Path.GetDirectoryName(dlg.FileName);
            SetAudioPath(dlg.FileName);
        }
    }

    private string? GetBrowseInitialDirectory()
    {
        if (!string.IsNullOrEmpty(_settings.LastBrowseDirectory) && Directory.Exists(_settings.LastBrowseDirectory))
            return _settings.LastBrowseDirectory;
        if (!string.IsNullOrEmpty(_selectedAudioPath))
        {
            var d = Path.GetDirectoryName(_selectedAudioPath);
            if (!string.IsNullOrEmpty(d) && Directory.Exists(d))
                return d;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    }

    private void DropZone_DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length > 0)
            e.Effects = DragDropEffects.Copy;
        else
            e.Effects = DragDropEffects.None;

        e.Handled = true;
    }

    private void DropZone_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] paths &&
            paths.Length > 0)
            SetAudioPath(paths[0]);

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
            AppLog.Debug(ex, "音声長さ");
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

        var bpmInput = BpmTextBox.Text.Trim();
        if (bpmInput.Length > 0 && !BpmInput.TryParseUserBpm(bpmInput, out _))
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
        _chordPaletteItems.Clear();
        ChordproOutputTextBox.Clear();
        MainProgressBar.IsIndeterminate = true;
        MainProgressBar.Value = 0;

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        try
        {
            var baseDir = AppContext.BaseDirectory;
            var result = await AudioConvertPipeline.RunAsync(baseDir, _selectedAudioPath!, BpmTextBox.Text, token)
                .ConfigureAwait(true);

            if (!string.IsNullOrEmpty(result.BpmDisplayForTextBox))
            {
                try
                {
                    await Dispatcher.InvokeAsync(() => BpmTextBox.Text = result.BpmDisplayForTextBox);
                }
                catch (Exception ex)
                {
                    AppLog.Debug(ex, "BPM 表示の更新");
                }
            }

            if (!result.Success)
            {
                StatusLabel.Text = result.StatusMessage;
                if (!string.IsNullOrEmpty(result.ChordproPaneErrorBody))
                    ChordproOutputTextBox.Text = result.ChordproPaneErrorBody;
                return;
            }

            var chords = result.Chords!.ToList();
            var barStarts = result.BarStarts!;
            _chordPaletteItems.Clear();
            foreach (var vm in ChordPaletteRowsFactory.BuildPaletteItems(chords, barStarts))
                _chordPaletteItems.Add(vm);

            _lastChordParseResult = chords;
            _lastSuccessfulBpm = result.BpmValue;
            UpdateChordproPreview();
            StatusLabel.Text = result.StatusMessage;
        }
        catch (OperationCanceledException)
        {
            StatusLabel.Text = "キャンセルされました。";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"エラー: {ex.Message}";
            ChordproOutputTextBox.Text = ex.ToString();
            AppLog.Debug(ex, "解析");
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
        ChordproOutputTextBox.Text = ChordproDocumentComposer.BuildBody(
            _selectedAudioPath,
            BpmTextBox.Text,
            _lastSuccessfulBpm,
            LyricsTextBox.Text,
            _lastChordParseResult);
    }

    private void ChordPaletteInsertButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChordPaletteItemVm vm } || vm.Insert.Length == 0)
            return;

        var tb = LyricsTextBox;
        var idx = tb.CaretIndex;
        if (idx < 0)
            idx = 0;
        if (idx > tb.Text.Length)
            idx = tb.Text.Length;

        tb.Text = tb.Text.Insert(idx, vm.Insert);
        tb.CaretIndex = idx + vm.Insert.Length;
        tb.Focus();
        vm.IsUsed = true;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "Chordpro (*.chordpro;*.pro)|*.chordpro;*.pro|すべてのファイル (*.*)|*.*",
            DefaultExt = ".chordpro",
            Title = "Chordpro として保存",
            InitialDirectory = GetBrowseInitialDirectory()
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
}
