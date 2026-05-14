# ChordproExtractor

オーディオからコード進行を推定し、歌詞と組み合わせて **ChordPro** 形式のテキストを編集・保存する Windows 向け WPF アプリです。  
ボーカル分離（Demucs）→ コード認識（Chordino）→ 小節位置（Bar Beat Tracker）→ BPM という流れで、外部プロセスをまとめて実行します。

---

## 仕様概要

### 処理パイプライン

1. **BPM**
   - テキストボックスに数値を入れている場合は **手動 BPM** として採用します。
   - 空または無効な場合は **Sonic Annotator** の `qm-tempotracker` で自動推定します。
2. **Demucs（ボーカル分離）**
   - 入力オーディオから **vocals.wav** と **no_vocals.wav** を生成します。
   - 作業ディレクトリは `%TEMP%\ChordproExtractor\demucs_<GUID>\` です。同一ファイル（パス・サイズ・更新日時）かつ有効な stems があれば **キャッシュ再利用** します（メタファイル `chordpro_demucs_source.txt` と stem 探索で判定）。
   - 約 28 日経過した作業フォルダは掃除対象として扱います（`DemucsWorkCache`）。
3. **コード・小節**
   - **no_vocals.wav** に対して並列実行:
     - Chordino（コード時刻）
     - QM Bar Beat Tracker（小節開始時刻の CSV）
   - Bar Tracker が失敗しても Chordino が成功していれば **コード一覧は利用可能** で、小節表示は時刻ベースに切り替わる旨の警告が出ます。
4. **UI 反映**
   - 右側のコードパレットに候補が並び、クリックで歌詞エディタのキャレット位置に `[chord]` を挿入します（仮想化された `ListBox`）。
   - プレビュー用に ChordPro 断片を生成します（保存形式の詳細は `ChordproDocumentComposer` / プレビュー表示ロジックに準拠）。

### 未完了時のクリーンアップ

Demucs が新規作業フォルダを作ったあと、**メタファイル書き込み前**にキャンセルや失敗でパイプラインが終わった場合、その **未完成フォルダを削除** してテンポラリを汚さないようにしています。

### 設定の永続化

次の内容を `%LocalAppData%\ChordproExtractor\settings.json` に JSON で保存します。

- ウィンドウ位置・サイズ  
- メイン 3 列（歌詞・コードパレット・プレビュー）の **Star 比率**（既定はアプリ内定数に基づく）  
- 最後にファイル選択したフォルダ  

### ログ

`AppLog` 経由で診断用のスタブ（現状は主に例外などを `Debug` 出力）があります。UI のステータス行とは別系統です。

### ライブラリ構成

| プロジェクト | 役割 |
|-------------|------|
| `ChordproExtractor` | WPF UI・外部プロセス起動・パイプライン統合 |
| `ChordproExtractor.Analysis` | コード点・CSV 解析・BPM 入力整形・小節時刻計算など **UI 非依存ロジック** |
| `ChordproExtractor.Tests` | xUnit。Analysis の単体テスト |

メインの `.csproj` はリポジトリ直下にあるため、**サブフォルダの Analysis / Tests の `.cs` をコンパイル対象から除外**する `DefaultItemExcludes` を設定しています（WPF の一時プロジェクトとの二重取り込み防止）。

---

## 必要環境

- **Windows**（WPF / `net8.0-windows`）
- **.NET 8 SDK**（ビルド・テスト実行用）

### 同梱・配置が必要な外部ツール

ビルド出力ディレクトリ（実行ファイルと同じ階層）に、次を配置します。`ChordproExtractor.csproj` の `CopyToOutputDirectory` で `tools\**` と `demucs_wrapper.py` がコピー対象になっています。

| パス | 内容 |
|------|------|
| `tools\sonic-annotator.exe` | [Sonic Visualiser / Sonic Annotator](https://www.sonicvisualiser.org/) 系の CLI。Chordino・Bar・BPM に使用。 |
| **Demucs のどちらか一方** | アプリ起動ディレクトリ（通常は `baseDir` = 実行ファイルのあるフォルダ）に **`demucs_wrapper.py`** がある、または **`tools\demucs-worker.exe`** があること。 |

`demucs_wrapper.py` を使う場合は、ラッパーが想定する **Python 環境と Demucs** がそのマシンで動くようにしてください。`demucs-worker.exe` は単体のワーカー想定です（実体は利用者が用意）。

---

## 使い方（アプリ操作）

1. **オーディオファイルを選ぶ**（対応形式は NAudio / パイプラインが読めるものに依存）。
2. 必要なら **BPM を手入力**。空にすると自動検出します。
3. **解析**（または同等のボタン）でパイプラインを実行します。時間がかかる処理のため **キャンセル** が使える場合があります（実装は `CancellationToken` 連携）。
4. 左（またはメイン）の **歌詞エディタ** にテキストを入力し、右の **コード一覧** からコードをクリックして `[コード]` を挿入します。
5. **プレビュー** で ChordPro 風の表示を確認し、**保存** で `.cho` / ChordPro テキストとして出力します（ファイル拡張子・フィルタはダイアログに従います）。

初回以降、ウィンドウレイアウトや列幅は `settings.json` に保存され、次回起動時に復元されます。

---

## ビルド・テスト（開発者向け）

ソリューションには **アプリ本体と Analysis** のみ含めています（テストをソリューションに入れると WPF の一時アセンブリと干渉しやすいため）。

```powershell
dotnet build "d:\Documents\GitHub\ChordproExtractor\ChordproExtractor.sln" -c Release
```

テストは **テストプロジェクトを直接指定**して実行します。

```powershell
dotnet test "d:\Documents\GitHub\ChordproExtractor\ChordproExtractor.Tests\ChordproExtractor.Tests.csproj" -c Release
```

Visual Studio から開く場合も、上記と同様にテストプロジェクト単体の実行が確実です。

---

## ライセンス・第三者ソフトウェア

利用する **sonic-annotator**、**Vamp プラグイン**、**Demucs** などは、それぞれの配布元のライセンスに従ってください。本リポジトリはそれらの再配布を前提とせず、実行時にユーザー環境へ配置する想定です。
