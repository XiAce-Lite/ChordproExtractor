using System.Diagnostics;
using System.IO;
using System.Text;

namespace ChordproExtractor;

/// <summary>Sonic Annotator 実行（Chordino / Bar / BPM の共通起動パターン）。</summary>
internal static class SonicAnnotatorClient
{
    internal const string VampChordinoDescriptor = "vamp:nnls-chroma:chordino:simplechord";
    internal const string VampQmBarBeatTrackerBars = "vamp:qm-vamp-plugins:qm-barbeattracker:bars";
    internal const string VampTempoTrackerTempo = "vamp:qm-vamp-plugins:qm-tempotracker:tempo";

    internal static async Task<(int ExitCode, string? Stderr, List<ChordPoint> Chords)> RunChordinoAsync(
        string exePath,
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        var chords = new List<ChordPoint>();
        var (exit, stderr, lines) = await RunCsvStdoutCollectLinesAsync(
            exePath,
            audioPath,
            VampChordinoDescriptor,
            cancellationToken).ConfigureAwait(false);

        foreach (var line in lines)
        {
            var point = SonicCsvParser.ParseCsvChordLine(line);
            if (point.HasValue)
                chords.Add(point.Value);
        }

        return (exit, stderr, chords);
    }

    internal static Task<(int ExitCode, string? Stderr, List<string> Lines)> RunVampCsvLinesAsync(
        string exePath,
        string audioPath,
        string vampOutputDescriptor,
        CancellationToken cancellationToken) =>
        RunCsvStdoutCollectLinesAsync(exePath, audioPath, vampOutputDescriptor, cancellationToken);

    internal static async Task<(int ExitCode, string? Stderr, double? Bpm)> EstimateBpmAsync(
        string exePath,
        string audioPath,
        CancellationToken cancellationToken)
    {
        var (exit, stderr, lines) = await RunCsvStdoutCollectLinesAsync(
            exePath,
            audioPath,
            VampTempoTrackerTempo,
            cancellationToken).ConfigureAwait(false);

        var bpm = SonicCsvParser.ParseGlobalTempoBpmFromCsvLines(lines);
        return (exit, stderr, bpm);
    }

    private static async Task<(int ExitCode, string? Stderr, List<string> Lines)> RunCsvStdoutCollectLinesAsync(
        string exePath,
        string audioPath,
        string vampOutputDescriptor,
        CancellationToken cancellationToken)
    {
        var lines = new List<string>();

        var toolDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(toolDir))
            toolDir = AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            WorkingDirectory = toolDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(vampOutputDescriptor);
        psi.ArgumentList.Add(audioPath);
        psi.ArgumentList.Add("-w");
        psi.ArgumentList.Add("csv");
        psi.ArgumentList.Add("--csv-stdout");

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        psi.Environment["PATH"] = string.IsNullOrEmpty(pathEnv)
            ? toolDir
            : toolDir + Path.PathSeparator + pathEnv;

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            while (true)
            {
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line == null)
                    break;

                lines.Add(line);
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessExecution.TryKillProcessTree(process);
            try
            {
                await stderrTask.ConfigureAwait(false);
            }
            catch
            {
                /* 無視 */
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(stderr))
            AppLog.Debug(stderr);

        return (process.ExitCode, string.IsNullOrWhiteSpace(stderr) ? null : stderr, lines);
    }
}
