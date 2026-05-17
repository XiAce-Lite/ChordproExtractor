using System.Collections.ObjectModel;

namespace ChordproExtractor;

internal static class ChordPaletteRowsFactory
{
    internal static string FormatChordPaletteTime(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}";

        return $"{(int)t.TotalMinutes}:{t.Seconds:D2}";
    }

    internal static ObservableCollection<ChordPaletteItemVm> BuildPaletteItems(
        IReadOnlyList<ChordPoint> sortedChords,
        List<double> barStartTimesSec)
    {
        var collection = new ObservableCollection<ChordPaletteItemVm>();
        if (sortedChords.Count == 0)
            return collection;

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
                var disp = ChordDisplayFormatter.FormatChordForDisplay(p.RawLabel);
                if (disp == null)
                    continue;

                var insert = '[' + disp + ']';
                items.Add((-1, p.Seconds, insert));
            }
        }
        else
        {
            var lastBarEnd = BarTimingMath.ComputeLastBarEndSec(barStartTimesSec, maxChordT);
            foreach (var p in sortedChords)
            {
                var disp = ChordDisplayFormatter.FormatChordForDisplay(p.RawLabel);
                if (disp == null)
                    continue;

                var insert = '[' + disp + ']';
                var bi = BarTimingMath.FindBarIndexForTime(p.Seconds, barStartTimesSec, lastBarEnd);
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
            var tip = barIdx < 0 ? $"{tSec:0.###} s" : $"Bar {barIdx + 1}（{tSec:0.###} s）";
            collection.Add(new ChordPaletteItemVm(label, insert, tip, tSec));
        }

        return collection;
    }
}
