using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChordproExtractor;

/// <summary>ウィンドウ位置・列比率・最後に開いたフォルダなどのユーザー設定。</summary>
internal sealed class AppUserSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }

    /// <summary>メイン 3 列の Star 係数（歌詞・パレット・プレビュー）。既定 2 / 1.15 / 1.35。</summary>
    public double[]? ColumnStars { get; set; }

    public string? LastBrowseDirectory { get; set; }

    /// <summary>再生音量 0〜1。未設定時は 0.8。</summary>
    public double? PlaybackVolume { get; set; }

    /// <summary>再生速度 0.5〜1.0。未設定時は 1.0。</summary>
    public double? PlaybackRate { get; set; }

    internal static string SettingsFilePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ChordproExtractor",
            "settings.json");

    internal static AppUserSettings Load()
    {
        try
        {
            var path = SettingsFilePath;
            if (!File.Exists(path))
                return new AppUserSettings();

            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppUserSettings>(json, JsonOptions) ?? new AppUserSettings();
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, "設定の読み込み");
            return new AppUserSettings();
        }
    }

    internal void Save()
    {
        try
        {
            var path = SettingsFilePath;
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, "設定の保存");
        }
    }
}
