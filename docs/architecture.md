# アーキテクチャ・処理パイプライン

ChordproExtractor の内部構成と、解析ボタン 1 回で走る処理の流れです。

## ライブラリ構成

| プロジェクト | 役割 |
|-------------|------|
| `ChordproExtractor` | WPF UI・外部プロセス起動・パイプライン統合・音声再生 |
| `ChordproExtractor.Analysis` | コード点・CSV 解析・BPM 入力整形・小節時刻計算など **UI 非依存ロジック** |
| `ChordproExtractor.Tests` | xUnit。Analysis の単体テスト |

メインの `.csproj` はリポジトリ直下にあるため、**サブフォルダの Analysis / Tests の `.cs` をコンパイル対象から除外**する `DefaultItemExcludes` を設定しています（WPF の一時プロジェクトとの二重取り込み防止）。

## 処理パイプライン

1. **BPM**
   - テキストボックスに数値を入れている場合は **手動 BPM** として採用します。
   - 空または無効な場合は **Sonic Annotator** の `qm-tempotracker` で自動推定します。
2. **Demucs（ボーカル分離）**
   - 入力オーディオから **vocals.wav** と **no_vocals.wav** を生成します。
   - 作業ディレクトリは `%TEMP%\ChordproExtractor\demucs_<GUID>\` です。同一ファイル（パス・サイズ・更新日時）かつ有効な stems があれば **キャッシュ再利用** します（メタファイル `chordpro_demucs_source.txt` と stem 探索で判定）。
   - 約 **28 日間利用がない**作業フォルダは掃除対象（`DemucsWorkCache`）。最終利用日時はメタファイル 4 行目 `last_used_utc` で管理し、ファイル選択・解析（キャッシュヒット）・再生開始時に更新します。
3. **コード・小節**
   - **no_vocals.wav** に対して並列実行:
     - Chordino（コード時刻）
     - QM Bar Beat Tracker（小節開始時刻の CSV）
   - Bar Tracker が失敗しても Chordino が成功していれば **コード一覧は利用可能** で、小節表示は時刻ベースに切り替わる旨の警告が出ます。
4. **UI 反映**
   - 中央のコードパレットに候補が並び、クリックで歌詞エディタのキャレット位置に `[chord]` を挿入します（仮想化された `ListBox`）。
   - プレビュー用に ChordPro 断片を生成します（`ChordproDocumentComposer`）。

## 未完了時のクリーンアップ

Demucs が新規作業フォルダを作ったあと、**メタファイル書き込み前**にキャンセルや失敗でパイプラインが終わった場合、その **未完成フォルダを削除** してテンポラリを汚さないようにしています。

## ログ

`AppLog` 経由で診断用のスタブ（現状は主に例外などを `Debug` 出力）があります。UI のステータス行とは別系統です。

## 関連ソース

| ファイル | 内容 |
|----------|------|
| `AudioConvertPipeline.cs` | パイプライン統合 |
| `DemucsRunner.cs` | Demucs 起動 |
| `DemucsWorkCache.cs` | stem キャッシュ |
| `SonicAnnotatorClient.cs` | Chordino / Bar / BPM |
| `AudioPlaybackService.cs` | NAudio 再生（UI から利用） |

設定の永続化は [settings.md](settings.md)、外部ツール配置は [setup-tools.md](setup-tools.md) を参照してください。
