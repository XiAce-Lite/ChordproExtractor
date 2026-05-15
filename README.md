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

ビルド出力ディレクトリ（実行ファイルと同じ階層、`AppContext.BaseDirectory`）に、次を配置します。`ChordproExtractor.csproj` の `CopyToOutputDirectory` で `tools\**\*` と `demucs_wrapper.py` がコピー対象になっています。

**リポジトリ上の `tools\` フォルダ**は `.gitignore` されており、**ソースツリーでは空のまま**運用します。開発者はプロジェクト直下に `tools\` を作成し、入手した実行ファイルや DLL を置いてからビルドしてください（ビルド後の `bin\…\net8.0-windows\` にも同じ構成がコピーされます）。

#### `tools\sonic-annotator.exe` と Vamp プラグイン

| パス | 内容 |
|------|------|
| `tools\sonic-annotator.exe` | [Sonic Annotator](https://www.sonicvisualiser.org/)（Sonic Visualiser 系配布に含まれる CLI）。コードでは作業ディレクトリと `PATH` の先頭を **この exe と同じフォルダ**に設定するため、配布物に付属する **DLL や Vamp プラグイン一式も `tools\` に同梱**してください。 |

アプリが `sonic-annotator` に渡す **Vamp 出力ディスクリプタ**（`SonicAnnotatorClient` 定数）は次のとおりです。

| 用途 | ディスクリプタ |
|------|----------------|
| コード認識（Chordino） | `vamp:nnls-chroma:chordino:simplechord` |
| 小節位置（Bar Beat Tracker） | `vamp:qm-vamp-plugins:qm-barbeattracker:bars` |
| BPM（手動未入力時） | `vamp:qm-vamp-plugins:qm-tempotracker:tempo` |

起動引数の形は `-d <上記> <音声ファイル> -w csv --csv-stdout` です。Chordino・QM プラグインが Sonic Annotator から解決できない場合は、プラグインの配置か 32/64 ビットの一致を確認してください（終了コードに応じたヒントは `ProcessExecution.DescribeWindowsExitCode` にあります）。

#### Demucs（`demucs_wrapper.py` または `tools\demucs-worker.exe`）

| パス | 内容 |
|------|------|
| **`demucs_wrapper.py`**（出力ディレクトリ直下） | リポジトリ同梱。Python で `demucs.separate` を **同一プロセス**で呼び出し、`torchaudio.save` を **soundfile** 経由に差し替えてから実行します（TorchAudio 2.9+ の TorchCodec 依存を避けるため）。Demucs には `--two-stems vocals` が渡され、`vocals.wav` / `no_vocals.wav` を出力します。C# からの引数: `入力オーディオ` `出力ディレクトリ`。 |
| **`tools\demucs-worker.exe`**（任意） | 上記ラッパーを [PyInstaller](https://pyinstaller.org/) 等で固めた想定のワーカー。リポジトリには **`demucs-worker.spec`**（エントリ `demucs_wrapper.py`）のみあり、exe 本体は各自がビルドして `tools\` に置きます。引数はラッパーと同じです。 |

Demucs 起動の優先順位は **`demucs_wrapper.py` が存在すれば Python 経由**（`CHORDPRO_DEMUCS_PYTHON` でフルパス指定可能。未指定時は `py -3` → `python` → `python3` を順に試行）、ラッパーが無い場合のみ **`tools\demucs-worker.exe`** です。

`demucs_wrapper.py` を使う Python 環境には、少なくとも **Demucs**（`demucs` パッケージ）、**PyTorch / torchaudio**、**NumPy**、ラッパーが要求する **`soundfile`**（`pip install soundfile`）が必要です。インポートに失敗した場合は従来どおり `demucs` コマンドのサブプロセスにフォールバックします（その場合は環境の torchaudio / torchcodec に依存します）。

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
dotnet build "{このプロジェクトの配置フォルダ}\ChordproExtractor\ChordproExtractor.sln" -c Release
```

テストは **テストプロジェクトを直接指定**して実行します。

```powershell
dotnet test "{このプロジェクトの配置フォルダ}\ChordproExtractor\ChordproExtractor.Tests\ChordproExtractor.Tests.csproj" -c Release
```

Visual Studio から開く場合も、上記と同様にテストプロジェクト単体の実行が確実です。

---

## ライセンス・第三者ソフトウェア

利用する **sonic-annotator**、**Vamp プラグイン**、**Demucs** などは、それぞれの配布元のライセンスに従ってください。本リポジトリはそれらの再配布を前提とせず、実行時にユーザー環境へ配置する想定です。
