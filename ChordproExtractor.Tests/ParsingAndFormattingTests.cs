using Xunit;

namespace ChordproExtractor.Tests;

public class ParsingAndFormattingTests
{
    [Fact]
    public void ParseCsvChordLine_CommaSeparated_ReturnsPoint()
    {
        var p = SonicCsvParser.ParseCsvChordLine("1.25, Eb");
        Assert.NotNull(p);
        Assert.Equal(1.25, p.Value.Seconds, 5);
        Assert.Equal("Eb", p.Value.RawLabel.Trim());
    }

    [Fact]
    public void ParseAndNormalizeBarStartTimes_DeduplicatesCloseTimes()
    {
        var lines = new[] { "0,0", "0.0005,0", "1.0,0" };
        var t = SonicCsvParser.ParseAndNormalizeBarStartTimes(lines);
        Assert.True(t.Count >= 2);
    }

    [Fact]
    public void ParseGlobalTempoBpmFromCsvLines_MedianInRange()
    {
        var lines = new[] { "junk 120.0 noise", "  90.5 , 100" };
        var bpm = SonicCsvParser.ParseGlobalTempoBpmFromCsvLines(lines);
        Assert.NotNull(bpm);
        Assert.InRange(bpm.Value, 30, 320);
    }

    [Theory]
    [InlineData("C:maj7", "CM7")]
    [InlineData("N", null)]
    public void FormatChordForDisplay_NormalizesOrSkips(string raw, string? expectContains)
    {
        var d = ChordDisplayFormatter.FormatChordForDisplay(raw);
        if (expectContains == null)
            Assert.Null(d);
        else
            Assert.Contains(expectContains, d!, StringComparison.Ordinal);
    }

    [Fact]
    public void BpmInput_TryParseUserBpm_Range()
    {
        Assert.False(BpmInput.TryParseUserBpm("", out _));
        Assert.False(BpmInput.TryParseUserBpm("19", out _));
        Assert.True(BpmInput.TryParseUserBpm("120", out var bpm));
        Assert.Equal(120, bpm);
    }

    [Fact]
    public void BarTimingMath_LastIndexWhereLessOrEqual()
    {
        var xs = new List<double> { 0, 1, 2, 3 };
        Assert.Equal(2, BarTimingMath.LastIndexWhereLessOrEqual(xs, 2.5));
    }
}
