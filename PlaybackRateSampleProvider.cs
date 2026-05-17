using NAudio.Wave;

namespace ChordproExtractor;

/// <summary>0.5〜1.0 倍のテープ式スロー再生（ピッチも下がる）。</summary>
internal sealed class PlaybackRateSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private float _rate = 1f;
    private float[] _scratch = [];

    public PlaybackRateSampleProvider(ISampleProvider source)
    {
        _source = source;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public float PlaybackRate
    {
        get => _rate;
        set => _rate = Math.Clamp(value, 0.5f, 1f);
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (count == 0)
            return 0;

        if (Math.Abs(_rate - 1f) < 0.0001f)
            return _source.Read(buffer, offset, count);

        var channels = WaveFormat.Channels;
        var framesOut = count / channels;
        if (framesOut == 0)
            return 0;

        var framesSource = Math.Max(1, (int)Math.Ceiling(framesOut * _rate));
        var sourceSamples = framesSource * channels;
        if (_scratch.Length < sourceSamples)
            _scratch = new float[sourceSamples];

        var read = _source.Read(_scratch, 0, sourceSamples);
        var framesRead = read / channels;
        if (framesRead == 0)
            return 0;

        for (var outFrame = 0; outFrame < framesOut; outFrame++)
        {
            var srcPos = outFrame * (framesRead - 1) / (float)Math.Max(framesOut - 1, 1);
            var i0 = (int)srcPos;
            var i1 = Math.Min(i0 + 1, framesRead - 1);
            var frac = srcPos - i0;
            for (var ch = 0; ch < channels; ch++)
            {
                var s0 = _scratch[i0 * channels + ch];
                var s1 = _scratch[i1 * channels + ch];
                buffer[offset + outFrame * channels + ch] = s0 * (1f - frac) + s1 * frac;
            }
        }

        return framesOut * channels;
    }
}
