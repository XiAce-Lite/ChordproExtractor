namespace ChordproExtractor;

/// <summary>Chordino CSV の 1 イベント（時刻秒・生ラベル）。</summary>
public readonly record struct ChordPoint(double Seconds, string RawLabel);
