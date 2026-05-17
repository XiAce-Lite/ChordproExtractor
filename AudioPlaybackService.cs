using System.IO;
using NAudio.Wave;

namespace ChordproExtractor;

internal enum PlaybackAudioSource
{
    Original,
    Vocals,
    Accompaniment
}

/// <summary>NAudio による単一ストリーム再生（シーク・AB リピート・音源切替・スロー再生）。</summary>
internal sealed class AudioPlaybackService : IDisposable
{
    public const double MinPlaybackRate = 0.5;
    public const double MaxPlaybackRate = 1.0;

    private WaveOutEvent? _waveOut;
    private AudioFileReader? _reader;
    private PlaybackRateSampleProvider? _rateProvider;
    private string? _currentPath;
    private bool _abRepeatEnabled;
    private double? _pointASeconds;
    private double? _pointBSeconds;
    private double _playbackRate = 1.0;

    public event Action? PositionChanged;
    public event Action? PlaybackStopped;

    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;
    public bool IsPaused => _waveOut?.PlaybackState == PlaybackState.Paused;
    public bool HasLoadedReader => _reader != null;

    public double CurrentTimeSeconds =>
        _reader?.CurrentTime.TotalSeconds ?? 0;

    public double TotalTimeSeconds =>
        _reader?.TotalTime.TotalSeconds ?? 0;

    public double PlaybackRate
    {
        get => _playbackRate;
        set
        {
            var clamped = Math.Clamp(value, MinPlaybackRate, MaxPlaybackRate);
            if (Math.Abs(_playbackRate - clamped) < 0.0001)
                return;

            _playbackRate = clamped;
            if (_rateProvider != null)
                _rateProvider.PlaybackRate = (float)clamped;

            ReinitWaveOutIfOpen();
        }
    }

    public bool AbRepeatEnabled
    {
        get => _abRepeatEnabled;
        set => _abRepeatEnabled = value;
    }

    public double? PointASeconds => _pointASeconds;
    public double? PointBSeconds => _pointBSeconds;

    public float Volume
    {
        get => _reader?.Volume ?? 1f;
        set
        {
            if (_reader != null)
                _reader.Volume = Math.Clamp(value, 0f, 1f);
        }
    }

    public void ClearAbPoints()
    {
        _pointASeconds = null;
        _pointBSeconds = null;
        _abRepeatEnabled = false;
    }

    public void SetPointA() => _pointASeconds = CurrentTimeSeconds;

    public void SetPointB()
    {
        var b = CurrentTimeSeconds;
        if (_pointASeconds.HasValue && b <= _pointASeconds.Value)
        {
            _pointBSeconds = _pointASeconds;
            _pointASeconds = b;
        }
        else
        {
            _pointBSeconds = b;
        }
    }

    public bool TryLoad(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        if (string.Equals(_currentPath, filePath, StringComparison.OrdinalIgnoreCase) && _reader != null)
            return true;

        StopInternal(resetPosition: true);
        DisposeReader();

        try
        {
            _reader = new AudioFileReader(filePath);
            _rateProvider = new PlaybackRateSampleProvider(_reader.ToSampleProvider())
            {
                PlaybackRate = (float)_playbackRate
            };
            _currentPath = filePath;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, "音声読み込み");
            DisposeReader();
            _currentPath = null;
            return false;
        }
    }

    public void Play()
    {
        if (_rateProvider == null)
            return;

        if (_waveOut == null)
        {
            _waveOut = new WaveOutEvent();
            _waveOut.PlaybackStopped += OnWaveOutPlaybackStopped;
            _waveOut.Init(_rateProvider);
        }

        if (_waveOut.PlaybackState == PlaybackState.Paused)
            _waveOut.Play();
        else if (_waveOut.PlaybackState != PlaybackState.Playing)
            _waveOut.Play();

        PositionChanged?.Invoke();
    }

    public void Pause()
    {
        if (_waveOut?.PlaybackState == PlaybackState.Playing)
            _waveOut.Pause();
        PositionChanged?.Invoke();
    }

    public void Stop()
    {
        StopInternal(resetPosition: true);
        PositionChanged?.Invoke();
    }

    public void Seek(double seconds)
    {
        if (_reader == null)
            return;

        var total = TotalTimeSeconds;
        if (total > 0)
            seconds = Math.Clamp(seconds, 0, total);
        else
            seconds = Math.Max(0, seconds);

        _reader.CurrentTime = TimeSpan.FromSeconds(seconds);
        PositionChanged?.Invoke();
    }

    public void SkipSeconds(double delta) => Seek(CurrentTimeSeconds + delta);

    /// <summary>AB リピート境界を超えていれば A へ戻す。戻した場合 true。</summary>
    public bool TryApplyAbRepeat()
    {
        if (!_abRepeatEnabled || !_pointASeconds.HasValue || !_pointBSeconds.HasValue || _reader == null)
            return false;

        if (CurrentTimeSeconds < _pointBSeconds.Value)
            return false;

        Seek(_pointASeconds.Value);
        if (IsPlaying)
            Play();
        return true;
    }

    public void SwitchSourcePreservePosition(string newFilePath, double positionSeconds, bool resumePlaying)
    {
        var wasPlaying = IsPlaying;
        StopInternal(resetPosition: false);
        DisposeReader();

        if (!TryLoad(newFilePath))
            return;

        Seek(positionSeconds);
        if (resumePlaying || wasPlaying)
            Play();
    }

    public void Dispose()
    {
        StopInternal(resetPosition: true);
        DisposeReader();
    }

    private void ReinitWaveOutIfOpen()
    {
        if (_rateProvider == null || _waveOut == null)
            return;

        var pos = CurrentTimeSeconds;
        var wasPlaying = IsPlaying;
        var wasPaused = IsPaused;

        try
        {
            _waveOut.Stop();
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, "再生速度変更時の停止");
        }

        _waveOut.Dispose();
        _waveOut = null;

        Seek(pos);

        if (wasPlaying)
            Play();
        else if (wasPaused)
        {
            Play();
            Pause();
        }
    }

    private void StopInternal(bool resetPosition)
    {
        if (_waveOut != null)
        {
            try
            {
                _waveOut.Stop();
            }
            catch (Exception ex)
            {
                AppLog.Debug(ex, "再生停止");
            }
        }

        if (resetPosition && _reader != null)
            _reader.CurrentTime = TimeSpan.Zero;
    }

    private void DisposeReader()
    {
        _waveOut?.Dispose();
        _waveOut = null;
        _rateProvider = null;
        _reader?.Dispose();
        _reader = null;
        _currentPath = null;
    }

    private void OnWaveOutPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
            AppLog.Debug(e.Exception, "再生終了");

        PlaybackStopped?.Invoke();
        PositionChanged?.Invoke();
    }
}
