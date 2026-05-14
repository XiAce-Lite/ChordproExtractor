namespace ChordproExtractor;

internal static class BarTimingMath
{
    internal static double ComputeLastBarEndSec(List<double> barTimes, double maxChordT)
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

    internal static int FindBarIndexForTime(double tSec, List<double> barTimes, double lastBarEnd)
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

    internal static int LastIndexWhereLessOrEqual(List<double> sortedAsc, double value)
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
}
