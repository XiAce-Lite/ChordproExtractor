# 外部ツールのセットアップ

## 必要環境

- **Windows**（WPF / `net8.0-windows`）
- **.NET 8 SDK**（ビルド・テスト実行用）

## 配置場所

ビルド出力ディレクトリ（実行ファイルと同じ階層、`AppContext.BaseDirectory`）に、次を配置します。`ChordproExtractor.csproj` の `CopyToOutputDirectory` で `tools\**\*` と `demucs_wrapper.py` がコピー対象になっています。

**リポジトリ上の `tools\` フォルダ**は `.gitignore` されており、**ソースツリーでは空のまま**運用します。開発者はプロジェクト直下に `tools\` を作成し、入手した実行ファイルや DLL を置いてからビルドしてください（ビルド後の `bin\…\net8.0-windows\` にも同じ構成がコピーされます）。

## Sonic Annotator と Vamp プラグイン

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

## Demucs（`demucs_wrapper.py` または `tools\demucs-worker.exe`）

| パス | 内容 |
|------|------|
| **`demucs_wrapper.py`**（出力ディレクトリ直下） | リポジトリ同梱。Python で `demucs.separate` を **同一プロセス**で呼び出し、`torchaudio.save` を **soundfile** 経由に差し替えてから実行します（TorchAudio 2.9+ の TorchCodec 依存を避けるため）。Demucs には `--two-stems vocals` が渡され、`vocals.wav` / `no_vocals.wav` を出力します。C# からの引数: `入力オーディオ` `出力ディレクトリ`。 |
| **`tools\demucs-worker.exe`**（任意） | 上記ラッパーを [PyInstaller](https://pyinstaller.org/) 等で固めた想定のワーカー。リポジトリには **`demucs-worker.spec`**（エントリ `demucs_wrapper.py`）のみあり、exe 本体は各自がビルドして `tools\` に置きます。引数はラッパーと同じです。 |

Demucs 起動の優先順位は **`demucs_wrapper.py` が存在すれば Python 経由**（`CHORDPRO_DEMUCS_PYTHON` でフルパス指定可能。未指定時は `py -3` → `python` → `python3` を順に試行）、ラッパーが無い場合のみ **`tools\demucs-worker.exe`** です。

`demucs_wrapper.py` を使う Python 環境には、少なくとも **Demucs**（`demucs` パッケージ）、**PyTorch / torchaudio**、**NumPy**、ラッパーが要求する **`soundfile`**（`pip install soundfile`）が必要です。インポートに失敗した場合は従来どおり `demucs` コマンドのサブプロセスにフォールバックします（その場合は環境の torchaudio / torchcodec に依存します）。

## ライセンス・第三者ソフトウェア

利用する **sonic-annotator**、**Vamp プラグイン**、**Demucs** などは、それぞれの配布元のライセンスに従ってください。本リポジトリはそれらの再配布を前提とせず、実行時にユーザー環境へ配置する想定です。
