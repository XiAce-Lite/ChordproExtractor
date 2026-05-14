using System.Diagnostics;

namespace ChordproExtractor;

/// <summary>診断メッセージの単一入口（将来ファイルログ等に差し替え可能）。</summary>
internal static class AppLog
{
    internal static void Debug(string message) => System.Diagnostics.Debug.WriteLine("[DBG] " + message);

    internal static void Debug(Exception ex, string? context = null) =>
        System.Diagnostics.Debug.WriteLine("[DBG] " + (context != null ? context + ": " : "") + ex);

    internal static void Warn(string message) => System.Diagnostics.Debug.WriteLine("[WRN] " + message);
}
