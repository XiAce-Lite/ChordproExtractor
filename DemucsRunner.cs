using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ChordproExtractor;

/// <summary>
/// Demucs 分離: まず demucs_wrapper.py + インストール済み Python を試し、
/// 無ければ tools/demucs-worker.exe にフォールバック。
/// </summary>
internal static class DemucsRunner
{
    internal static async Task<(int ExitCode, string? Stderr, string? Stdout)> RunSeparationAsync(
        string baseDir,
        string demucsWrapperPath,
        string demucsExePath,
        string inputAudioPath,
        string outputDir,
        CancellationToken cancellationToken = default)
    {
        if (File.Exists(demucsWrapperPath))
        {
            var customPy = Environment.GetEnvironmentVariable("CHORDPRO_DEMUCS_PYTHON");
            if (!string.IsNullOrWhiteSpace(customPy) && File.Exists(customPy))
            {
                try
                {
                    return await RunDemucsPythonAsync(
                        customPy,
                        baseDir,
                        demucsWrapperPath,
                        inputAudioPath,
                        outputDir,
                        prefixArgs: null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Win32Exception ex)
                {
                    AppLog.Debug(ex, "CHORDPRO_DEMUCS_PYTHON 起動");
                }
            }

            foreach (var (launcher, prefixArgs) in new (string, string[])[]
                     {
                         ("py", new[] { "-3" }),
                         ("python", Array.Empty<string>()),
                         ("python3", Array.Empty<string>())
                     })
            {
                try
                {
                    return await RunDemucsPythonAsync(
                        launcher,
                        baseDir,
                        demucsWrapperPath,
                        inputAudioPath,
                        outputDir,
                        prefixArgs,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Win32Exception ex)
                {
                    AppLog.Debug(ex, "Demucs Python 候補 " + launcher);
                }
            }
        }

        if (File.Exists(demucsExePath))
        {
            var toolDir = Path.GetDirectoryName(demucsExePath);
            if (string.IsNullOrEmpty(toolDir))
                toolDir = baseDir;

            return await ProcessExecution.RunUtf8ProcessAsync(
                demucsExePath,
                toolDir,
                [inputAudioPath, outputDir],
                cancellationToken).ConfigureAwait(false);
        }

        return (
            -1,
            "Demucs を起動できません。demucs_wrapper.py と PATH 上の python（または CHORDPRO_DEMUCS_PYTHON）、または tools/demucs-worker.exe を確認してください。",
            null);
    }

    private static async Task<(int ExitCode, string? Stderr, string? Stdout)> RunDemucsPythonAsync(
        string pythonExecutable,
        string workingDirectory,
        string demucsWrapperPath,
        string inputAudioPath,
        string outputDir,
        string[]? prefixArgs = null,
        CancellationToken cancellationToken = default)
    {
        var args = new List<string>();
        if (prefixArgs is { Length: > 0 })
            args.AddRange(prefixArgs);

        args.Add(demucsWrapperPath);
        args.Add(inputAudioPath);
        args.Add(outputDir);

        return await ProcessExecution.RunUtf8ProcessAsync(pythonExecutable, workingDirectory, args, cancellationToken)
            .ConfigureAwait(false);
    }
}
