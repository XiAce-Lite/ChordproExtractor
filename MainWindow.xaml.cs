using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
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
    private readonly ObservableCollection<ChordPaletteItemVm> _chordPaletteItems = [];
    private readonly AudioPlaybackService _playback = new();
    private readonly DispatcherTimer _playbackTimer;

    private string? _demucsWorkDir;
    private string? _vocalsPath;
    private string? _noVocalsPath;
    private bool _isUserSeeking;
    private bool _suppressPlaybackSpeedCombo;
    private int _lastHighlightedChordIndex = -1;

    public MainWindow()
    {
        InitializeComponent();
        ChordPaletteList.ItemsSource = _chordPaletteItems;

        _playbackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += PlaybackTimer_Tick;
        _playback.PositionChanged += OnPlaybackPositionChanged;
        _playback.PlaybackStopped += OnPlaybackStopped;

        var vol = _settings.PlaybackVolume ?? 0.8;
        VolumeSlider.Value = vol;
        _playback.Volume = (float)vol;

        InitPlaybackSpeedCombo();
        var rate = Math.Clamp(_settings.PlaybackRate ?? 1.0, AudioPlaybackService.MinPlaybackRate,
            AudioPlaybackService.MaxPlaybackRate);
        _playback.PlaybackRate = rate;
        SelectPlaybackSpeedCombo(rate);
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
        _playbackTimer.Stop();
        _settings.PlaybackVolume = VolumeSlider.Value;
        _settings.PlaybackRate = _playback.PlaybackRate;
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
        _playback.Dispose();
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
        {
            BpmTextBox.Clear();
            ResetPlaybackForNewFile();
        }

        ResolveDemucsStemsFromCache();
        UpdateDurationLabel();
        StatusLabel.Text = "準備完了。解析で BPM とコードパレットを生成し、歌詞へクリックでコードを挿入できます。";
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
            if (!string.IsNullOrEmpty(result.DemucsWorkDir))
            {
                _demucsWorkDir = result.DemucsWorkDir;
                ResolveDemucsStemPaths();
                RecordDemucsCacheAccess();
            }

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

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_isProcessing)
            return;

        if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift)
        {
            TogglePlayPause();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.F3:
                SkipPlaybackSeconds(-10);
                e.Handled = true;
                break;
            case Key.F4:
                InsertActiveChord();
                e.Handled = true;
                break;
            case Key.F5:
                TogglePlayPause();
                e.Handled = true;
                break;
            case Key.F6:
                SkipPlaybackSeconds(10);
                e.Handled = true;
                break;
        }
    }

    private void ChordPaletteInsertButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: ChordPaletteItemVm vm })
            return;
        InsertChordAtCaret(vm);
    }

    private void InsertChordAtCaret(ChordPaletteItemVm vm)
    {
        if (vm.Insert.Length == 0)
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

    private void InsertActiveChord()
    {
        var idx = FindActiveChordIndex(_chordPaletteItems, _playback.CurrentTimeSeconds);
        if (idx < 0)
        {
            StatusLabel.Text = "挿入できるコードがありません（解析後に再生してください）。";
            return;
        }

        InsertChordAtCaret(_chordPaletteItems[idx]);
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

    private void ResetPlaybackForNewFile()
    {
        _playbackTimer.Stop();
        _playback.Stop();
        _playback.ClearAbPoints();
        AbRepeatToggle.IsChecked = false;
        SourceOriginalRadio.IsChecked = true;
        _demucsWorkDir = null;
        _vocalsPath = null;
        _noVocalsPath = null;
        SourceVocalsRadio.IsEnabled = false;
        SourceAccompanimentRadio.IsEnabled = false;
        ClearChordHighlights();
        UpdatePlaybackUi();
    }

    private void ResolveDemucsStemsFromCache()
    {
        if (string.IsNullOrEmpty(_selectedAudioPath))
            return;

        var cached = DemucsWorkCache.TryFindReusableWorkDir(_selectedAudioPath);
        if (cached != null)
        {
            _demucsWorkDir = cached;
            ResolveDemucsStemPaths();
            RecordDemucsCacheAccess();
        }
    }

    private void RecordDemucsCacheAccess()
    {
        if (!string.IsNullOrEmpty(_demucsWorkDir))
            DemucsWorkCache.RecordCacheAccess(_demucsWorkDir);
    }

    private void InitPlaybackSpeedCombo()
    {
        PlaybackSpeedCombo.Items.Clear();
        foreach (var (label, rate) in new (string Label, double Rate)[]
                 {
                     ("100%", 1.0), ("90%", 0.9), ("80%", 0.8), ("75%", 0.75), ("50%", 0.5)
                 })
        {
            PlaybackSpeedCombo.Items.Add(new ComboBoxItem { Content = label, Tag = rate });
        }
    }

    private void SelectPlaybackSpeedCombo(double rate)
    {
        _suppressPlaybackSpeedCombo = true;
        foreach (ComboBoxItem item in PlaybackSpeedCombo.Items)
        {
            if (item.Tag is double d && Math.Abs(d - rate) < 0.001)
            {
                PlaybackSpeedCombo.SelectedItem = item;
                break;
            }
        }

        _suppressPlaybackSpeedCombo = false;
    }

    private void PlaybackSpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlaybackSpeedCombo || !IsLoaded)
            return;

        if (PlaybackSpeedCombo.SelectedItem is ComboBoxItem { Tag: double rate })
            _playback.PlaybackRate = rate;
    }

    private void ResolveDemucsStemPaths()
    {
        _vocalsPath = null;
        _noVocalsPath = null;
        if (string.IsNullOrEmpty(_demucsWorkDir))
        {
            SourceVocalsRadio.IsEnabled = false;
            SourceAccompanimentRadio.IsEnabled = false;
            return;
        }

        if (DemucsWorkCache.TryResolveDemucsStemPaths(_demucsWorkDir, out var v, out var nv))
        {
            _vocalsPath = v;
            _noVocalsPath = nv;
            SourceVocalsRadio.IsEnabled = true;
            SourceAccompanimentRadio.IsEnabled = true;
        }
        else
        {
            SourceVocalsRadio.IsEnabled = false;
            SourceAccompanimentRadio.IsEnabled = false;
        }
    }

    private PlaybackAudioSource GetSelectedSource()
    {
        if (SourceVocalsRadio.IsChecked == true)
            return PlaybackAudioSource.Vocals;
        if (SourceAccompanimentRadio.IsChecked == true)
            return PlaybackAudioSource.Accompaniment;
        return PlaybackAudioSource.Original;
    }

    private string? GetPathForSource(PlaybackAudioSource source) =>
        source switch
        {
            PlaybackAudioSource.Vocals => _vocalsPath,
            PlaybackAudioSource.Accompaniment => _noVocalsPath,
            _ => _selectedAudioPath
        };

    private bool EnsurePlaybackLoaded()
    {
        var path = GetPathForSource(GetSelectedSource());
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return false;

        if (_playback.TryLoad(path))
        {
            SeekSlider.Maximum = Math.Max(_playback.TotalTimeSeconds, 0.001);
            return true;
        }

        return false;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => TogglePlayPause();

    private void TogglePlayPause()
    {
        if (_playback.IsPlaying)
        {
            _playback.Pause();
            _playbackTimer.Stop();
        }
        else
        {
            if (!EnsurePlaybackLoaded())
            {
                StatusLabel.Text = "再生できる音声がありません。";
                return;
            }

            _playback.Play();
            _playbackTimer.Start();
            RecordDemucsCacheAccess();
        }

        UpdatePlaybackUi();
    }

    private void StopPlaybackButton_Click(object sender, RoutedEventArgs e)
    {
        _playbackTimer.Stop();
        _playback.Stop();
        ClearChordHighlights();
        UpdatePlaybackUi();
    }

    private void Rewind10Button_Click(object sender, RoutedEventArgs e) => SkipPlaybackSeconds(-10);

    private void Forward10Button_Click(object sender, RoutedEventArgs e) => SkipPlaybackSeconds(10);

    private void SkipPlaybackSeconds(double delta)
    {
        if (!EnsurePlaybackLoaded())
            return;
        _playback.SkipSeconds(delta);
        UpdatePlaybackUi();
        UpdateChordHighlight();
    }

    private void PlaybackSourceRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;

        var newPath = GetPathForSource(GetSelectedSource());
        if (string.IsNullOrEmpty(newPath) || !File.Exists(newPath))
            return;

        var pos = _playback.HasLoadedReader ? _playback.CurrentTimeSeconds : 0;
        var playing = _playback.IsPlaying;
        _playback.SwitchSourcePreservePosition(newPath, pos, playing);
        if (playing)
            _playbackTimer.Start();
        else
            _playbackTimer.Stop();

        SeekSlider.Maximum = Math.Max(_playback.TotalTimeSeconds, 0.001);
        UpdatePlaybackUi();
        UpdateChordHighlight();
    }

    private void SeekSlider_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        _isUserSeeking = true;

    private void SeekSlider_PreviewMouseLeftButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isUserSeeking = false;
        if (!EnsurePlaybackLoaded())
            return;
        _playback.Seek(SeekSlider.Value);
        UpdatePlaybackUi();
        UpdateChordHighlight();
    }

    private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_isUserSeeking || !IsLoaded)
            return;

        PlaybackCurrentTimeText.Text = FormatPlaybackTime(e.NewValue);
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
            return;
        _playback.Volume = (float)e.NewValue;
    }

    private void PointAButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePlaybackLoaded())
            return;
        _playback.SetPointA();
        StatusLabel.Text = $"A = {FormatPlaybackTime(_playback.PointASeconds ?? 0)}";
    }

    private void PointBButton_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsurePlaybackLoaded())
            return;
        _playback.SetPointB();
        StatusLabel.Text = $"B = {FormatPlaybackTime(_playback.PointBSeconds ?? 0)}";
    }

    private void AbRepeatToggle_Click(object sender, RoutedEventArgs e) =>
        _playback.AbRepeatEnabled = AbRepeatToggle.IsChecked == true;

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (_playback.TryApplyAbRepeat())
            return;

        UpdatePlaybackUi();
        UpdateChordHighlight();

        if (!_playback.IsPlaying && !_playback.IsPaused)
            _playbackTimer.Stop();
    }

    private void OnPlaybackPositionChanged() =>
        Dispatcher.BeginInvoke(UpdatePlaybackUi);

    private void OnPlaybackStopped()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_playback.CurrentTimeSeconds >= _playback.TotalTimeSeconds - 0.05 && _playback.TotalTimeSeconds > 0)
            {
                _playbackTimer.Stop();
                ClearChordHighlights();
            }

            UpdatePlaybackUi();
        });
    }

    private void UpdatePlaybackUi()
    {
        PlayPauseButton.Content = _playback.IsPlaying ? "❚❚" : "▶";

        var total = _playback.HasLoadedReader ? _playback.TotalTimeSeconds : GetDurationSecondsFromFile();
        var current = _playback.HasLoadedReader ? _playback.CurrentTimeSeconds : 0;

        if (total <= 0 && !string.IsNullOrEmpty(_selectedAudioPath))
            total = GetDurationSecondsFromFile();

        PlaybackTotalTimeText.Text = FormatPlaybackTime(total);
        PlaybackCurrentTimeText.Text = FormatPlaybackTime(current);

        if (!_isUserSeeking)
        {
            SeekSlider.Maximum = Math.Max(total, 0.001);
            SeekSlider.Value = Math.Min(current, SeekSlider.Maximum);
        }

        UpdateCurrentChordDisplay();
    }

    private void UpdateCurrentChordDisplay()
    {
        if (_lastHighlightedChordIndex < 0 || _lastHighlightedChordIndex >= _chordPaletteItems.Count)
        {
            CurrentChordTextBlock.Text = "現在: —";
            return;
        }

        var vm = _chordPaletteItems[_lastHighlightedChordIndex];
        var stateGlyph = _playback.IsPlaying ? "▶" : _playback.IsPaused ? "❚❚" : "·";
        var timeText = _playback.HasLoadedReader
            ? FormatPlaybackTime(_playback.CurrentTimeSeconds)
            : FormatPlaybackTime(vm.Seconds);
        CurrentChordTextBlock.Text = $"{stateGlyph} 現在: {vm.Insert}  {timeText}";
    }

    private double GetDurationSecondsFromFile()
    {
        if (string.IsNullOrEmpty(_selectedAudioPath) || !File.Exists(_selectedAudioPath))
            return 0;

        try
        {
            using var reader = new AudioFileReader(_selectedAudioPath);
            return reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    private static string FormatPlaybackTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";
        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    private void UpdateDurationLabel()
    {
        if (string.IsNullOrEmpty(_selectedAudioPath) || !File.Exists(_selectedAudioPath))
        {
            DurationLabel.Text = "長さ: —";
            PlaybackTotalTimeText.Text = "0:00";
            return;
        }

        try
        {
            using var reader = new AudioFileReader(_selectedAudioPath);
            DurationLabel.Text = $"長さ: {reader.TotalTime:hh\\:mm\\:ss\\.fff}";
            if (!_playback.HasLoadedReader)
            {
                PlaybackTotalTimeText.Text = FormatPlaybackTime(reader.TotalTime.TotalSeconds);
                SeekSlider.Maximum = Math.Max(reader.TotalTime.TotalSeconds, 0.001);
            }
        }
        catch (Exception ex)
        {
            DurationLabel.Text = "長さ: 取得できませんでした";
            AppLog.Debug(ex, "音声長さ");
        }
    }

    private void ClearChordHighlights()
    {
        foreach (var vm in _chordPaletteItems)
            vm.IsActive = false;
        _lastHighlightedChordIndex = -1;
        UpdateCurrentChordDisplay();
    }

    private void UpdateChordHighlight()
    {
        if (_chordPaletteItems.Count == 0)
        {
            ClearChordHighlights();
            return;
        }

        var idx = FindActiveChordIndex(_chordPaletteItems, _playback.CurrentTimeSeconds);
        if (idx != _lastHighlightedChordIndex)
        {
            for (var i = 0; i < _chordPaletteItems.Count; i++)
                _chordPaletteItems[i].IsActive = i == idx;

            _lastHighlightedChordIndex = idx;

            if (idx >= 0)
                ChordPaletteList.ScrollIntoView(_chordPaletteItems[idx]);
        }

        UpdateCurrentChordDisplay();
    }

    private static int FindActiveChordIndex(ObservableCollection<ChordPaletteItemVm> items, double positionSeconds)
    {
        if (items.Count == 0)
            return -1;

        var lo = 0;
        var hi = items.Count - 1;
        var best = -1;
        while (lo <= hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (items[mid].Seconds <= positionSeconds)
            {
                best = mid;
                lo = mid + 1;
            }
            else
                hi = mid - 1;
        }

        return best;
    }
}
