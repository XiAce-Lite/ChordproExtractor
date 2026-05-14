namespace ChordproExtractor;

internal static class ChordproDocumentComposer
{
    internal static string BuildBody(
        string? audioPath,
        string bpmTextBoxText,
        double lastSuccessfulBpm,
        string lyricsText,
        IReadOnlyList<ChordPoint>? chordsForKey)
    {
        var bpmForPreamble = BpmInput.TryParseUserBpm(bpmTextBoxText, out var manualBpm)
            ? manualBpm
            : (lastSuccessfulBpm > 0 ? lastSuccessfulBpm : double.NaN);

        var keyGuess = chordsForKey != null ? ChordDisplayFormatter.TryGuessKeyFromChords(chordsForKey) : null;
        return ChordproPreamble.Build(audioPath ?? string.Empty, bpmForPreamble, keyGuess) + lyricsText;
    }
}
