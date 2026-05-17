# ChordproExtractor

オーディオからコード進行を推定し、歌詞と組み合わせて **ChordPro** 形式のテキストを編集・保存する Windows 向け WPF アプリです。

- **解析**: Demucs でボーカル分離 → Chordino でコード・小節推定 → BPM
- **編集**: コードパレットから歌詞へワンクリック挿入
- **再生**: 元曲・ボイス・伴奏の切替、シーク、AB リピート、再生位置に合わせたコードハイライト

---

## できること

1. MP3 / WAV を読み込み、BPM とコード一覧を自動生成
2. 歌詞エディタのキャレット位置に、パレットのコードをクリックで `[Am]` など挿入
3. ChordPro プレビューの編集と `.chordpro` / `.pro` 保存
4. 解析後（またはキャッシュがある場合）は **元曲・ボイス・伴奏** を聴きながら編集

---

## 使い方

1. **音声ファイルを選ぶ**（MP3 / WAV）。ドラッグ＆ドロップも可。
2. 必要なら **BPM を手入力**。空欄のまま **解析** すると自動検出します。
3. 解析完了後、中央の **コードパレット** からコードをクリックして歌詞へ挿入します。
4. **再生バー**（解析行の下）で曲を聴けます。
   - 音源: **元曲** / **ボイス** / **伴奏**（解析後にボイス・伴奏が有効）
   - **▶** 再生、**❚❚** 一時停止（続きから）、**■** 停止（曲頭へ）
   - スライダーで任意位置へシーク、**◀10s** / **10s▶** で ±10 秒（**F3** / **F6**）
   - **A** / **B** で区間マーク、**AB** で区間リピート
   - **Shift+Space** または **F5** で再生/一時停止、**F4** で歌詞へ現在コードを挿入（歌詞ヘッダに表示）
   - **速度** 50%〜100%（遅くするのみ）
5. **Chordpro プレビュー更新** でヘッダ付きプレビューを再生成し、**保存** で出力します。

ウィンドウの大きさ・3 列の幅・音量は次回起動時に復元されます（[settings.md](docs/settings.md)）。

---

## 必要なもの

| 項目 | 内容 |
|------|------|
| OS | Windows |
| ランタイム | .NET 8（`net8.0-windows`） |
| 外部ツール | 実行ファイルと同じフォルダに `tools\sonic-annotator.exe`（Vamp プラグイン同梱）と Demucs（`demucs_wrapper.py` または `tools\demucs-worker.exe`） |

配置の詳細は **[docs/setup-tools.md](docs/setup-tools.md)** を参照してください。`tools\` はリポジトリに含まれないため、各自で用意してビルド出力へコピーされます。

---

## ドキュメント

| 文書 | 内容 |
|------|------|
| [docs/architecture.md](docs/architecture.md) | 処理パイプライン・プロジェクト構成 |
| [docs/setup-tools.md](docs/setup-tools.md) | Sonic Annotator / Demucs の配置 |
| [docs/settings.md](docs/settings.md) | `settings.json` の項目 |
| [docs/audio-playback.md](docs/audio-playback.md) | 再生 UI の設計 |

---

## ビルド・テスト（開発者向け）

ソリューションには **アプリ本体と Analysis** のみ含めています（テストをソリューションに入れると WPF の一時アセンブリと干渉しやすいため）。

```powershell
dotnet build "ChordproExtractor.sln" -c Release
dotnet test "ChordproExtractor.Tests\ChordproExtractor.Tests.csproj" -c Release
```

Visual Studio から開く場合も、テストは **テストプロジェクトを直接指定** して実行するのが確実です。

---

## ライセンス・第三者ソフトウェア

**sonic-annotator**、**Vamp プラグイン**、**Demucs** などは、それぞれの配布元のライセンスに従ってください。本リポジトリはそれらの再配布を前提とせず、実行時にユーザー環境へ配置する想定です。
