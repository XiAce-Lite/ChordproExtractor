using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ChordproExtractor;

/// <summary>コードパレット 1 行（UI バインディング用）。</summary>
public sealed class ChordPaletteItemVm : INotifyPropertyChanged
{
    private bool _isUsed;

    public ChordPaletteItemVm(string label, string insert, string toolTipText)
    {
        Label = label;
        Insert = insert;
        ToolTipText = toolTipText;
    }

    public string Label { get; }
    public string Insert { get; }
    public string ToolTipText { get; }

    public bool IsUsed
    {
        get => _isUsed;
        set
        {
            if (_isUsed == value)
                return;
            _isUsed = value;
            OnPropertyChanged(nameof(IsUsed));
            OnPropertyChanged(nameof(CheckGlyph));
        }
    }

    public string CheckGlyph => _isUsed ? "\u2713" : string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
