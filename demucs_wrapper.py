"""
Demucs を C# から呼び出すためのエントリ。

TorchAudio 2.9 以降は torchaudio.save() が TorchCodec 依存となり、
Windows で torchcodec が無い／使えないと分離の最後の WAV 書き出しで落ちる。
そのため、demucs を子プロセスではなく同一インタプリタで起動し、
torchaudio.save を soundfile ベースに差し替えてから demucs.separate を実行する。
"""

import io
import sys
import os


def _configure_stdio_utf8() -> None:
    """パイプへ UTF-8 で出力し、C# 側の UTF-8 デコードと一致させる（日本語パス・メッセージの化け防止）。"""
    if sys.platform == "win32":
        os.environ.setdefault("PYTHONUTF8", "1")
        os.environ.setdefault("PYTHONIOENCODING", "utf-8")
    try:
        if hasattr(sys.stdout, "reconfigure"):
            sys.stdout.reconfigure(encoding="utf-8", errors="replace")
        if hasattr(sys.stderr, "reconfigure"):
            sys.stderr.reconfigure(encoding="utf-8", errors="replace")
    except (AttributeError, OSError, ValueError, io.UnsupportedOperation):
        pass


_configure_stdio_utf8()


def _patch_torchaudio_save_with_soundfile() -> None:
    """torchaudio.save を soundfile 実装に差し替え（TorchCodec 不要）。"""
    import numpy as np
    import torch
    import torchaudio as ta

    try:
        import soundfile as sf
    except ImportError as e:
        raise RuntimeError(
            "soundfile がインストールされていません。次を実行してください: pip install soundfile"
        ) from e

    _orig = ta.save

    def _save(uri, src, sample_rate, **kwargs):
        path = str(uri)
        wav = src.detach().cpu()

        if wav.dim() == 1:
            data = wav.numpy().astype(np.float32, copy=False)
            data = np.expand_dims(data, axis=1)
        elif wav.dim() == 2:
            data = wav.numpy().astype(np.float32, copy=False).T
        else:
            return _orig(uri, src, sample_rate, **kwargs)

        bits = kwargs.get("bits_per_sample", 16)
        if bits == 32 and kwargs.get("encoding") == "PCM_F":
            sf.write(path, data, sample_rate, subtype="FLOAT")
            return

        data = np.clip(data, -1.0, 1.0)
        data_i16 = (data * 32767.0).astype(np.int16)
        sf.write(path, data_i16, sample_rate, subtype="PCM_16")

    ta.save = _save


def _run_demucs_in_process(input_path: str, output_dir: str) -> int:
    _patch_torchaudio_save_with_soundfile()

    old_argv = sys.argv[:]
    try:
        from demucs.separate import main as demucs_main

        sys.argv = [
            "demucs",
            "--two-stems",
            "vocals",
            "-o",
            output_dir,
            input_path,
        ]
        demucs_main()
        return 0
    except SystemExit as e:
        code = e.code
        if code is None:
            return 0
        if isinstance(code, int):
            return code
        return 1
    finally:
        sys.argv = old_argv


def _run_demucs_subprocess_fallback(input_path: str, output_dir: str) -> int:
    """PyInstaller 等で demucs が同梱されない場合の従来動作（環境側の torchaudio/torchcodec 依存）。"""
    cmd = [
        "demucs",
        "--two-stems",
        "vocals",
        "-o",
        output_dir,
        input_path,
    ]
    print(f"Running Demucs (subprocess) for: {input_path}")
    env = os.environ.copy()
    env.setdefault("PYTHONUTF8", "1")
    env.setdefault("PYTHONIOENCODING", "utf-8")
    import subprocess

    r = subprocess.run(cmd, check=False, env=env)
    return int(r.returncode)


def main() -> None:
    _configure_stdio_utf8()

    if len(sys.argv) < 3:
        print("Usage: demucs-worker.exe [input_audio_path] [output_dir]", file=sys.stderr)
        sys.exit(1)

    input_path = sys.argv[1]
    output_dir = sys.argv[2]

    print(f"Running Demucs for: {input_path}")

    try:
        rc = _run_demucs_in_process(input_path, output_dir)
    except ImportError:
        rc = _run_demucs_subprocess_fallback(input_path, output_dir)
    except Exception as e:
        if isinstance(e, OSError) and getattr(e, "winerror", None) == 1114:
            print(
                "ヒント: PyInstaller の単体 exe で同梱した PyTorch の DLL 読み込みに失敗することがあります。"
                " 対処例: (1) Visual C++ 再頒布可能パッケージを最新にする (2) PyInstaller を onedir でビルドする"
                " (3) 同じ環境で `python demucs_wrapper.py` を直接実行して demucs-worker.exe を使わない",
                file=sys.stderr,
            )
        print(f"Demucs error: {e}", file=sys.stderr)
        rc = 1

    if rc != 0:
        sys.exit(rc)

    print("Separation completed successfully.")


if __name__ == "__main__":
    main()
