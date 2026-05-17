# ユーザー設定（settings.json）

次の内容を `%LocalAppData%\ChordproExtractor\settings.json` に JSON で保存します（`AppUserSettings`）。

| 項目 | 説明 |
|------|------|
| `WindowLeft` / `WindowTop` / `WindowWidth` / `WindowHeight` | メインウィンドウの位置とサイズ |
| `ColumnStars` | メイン 3 列（歌詞・コードパレット・プレビュー）の **Star 比率**。既定は 2 / 1.15 / 1.35 |
| `LastBrowseDirectory` | ファイル選択ダイアログの初期フォルダ |
| `PlaybackVolume` | 再生音量（0〜1）。既定 0.8 |
| `PlaybackRate` | 再生速度（0.5〜1.0）。既定 1.0 |

ウィンドウを閉じるたびに保存され、次回起動時に復元されます。
