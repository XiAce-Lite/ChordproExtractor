using System.IO;

namespace ChordproExtractor;

internal static class ChordproPaths
{
    internal static string DemucsWorkRoot =>
        Path.Combine(Path.GetTempPath(), "ChordproExtractor");
}
