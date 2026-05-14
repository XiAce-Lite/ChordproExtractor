using System.Diagnostics;
using System.Text;

namespace ChordproExtractor;

internal static class ProcessExecution
{
    internal static void TryKillProcessTree(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug("子プロセスの強制終了に失敗: " + ex);
        }
    }

    internal static async Task<(int ExitCode, string? Stderr, string? Stdout)> RunUtf8ProcessAsync(
        string fileName,
        string workingDirectory,
        IReadOnlyList<string> argumentList,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        psi.Environment["PYTHONUTF8"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        foreach (var a in argumentList)
            psi.ArgumentList.Add(a);

        using var process = new Process { StartInfo = psi };
        process.Start();

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillProcessTree(process);
            try
            {
                await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
            }
            catch
            {
                /* 無視 */
            }

            throw;
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stderr))
            AppLog.Debug(stderr);
        if (!string.IsNullOrWhiteSpace(stdout))
            AppLog.Debug(stdout);

        return (
            process.ExitCode,
            string.IsNullOrWhiteSpace(stderr) ? null : stderr,
            string.IsNullOrWhiteSpace(stdout) ? null : stdout);
    }

    internal static string? BuildProcessErrorBody(string? stdout, string? stderr)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(stdout))
        {
            sb.AppendLine("[標準出力]");
            sb.AppendLine(stdout.Trim());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            if (sb.Length > 0)
                sb.AppendLine();
            sb.AppendLine("[標準エラー]");
            sb.AppendLine(stderr.Trim());
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    internal static string DescribeWindowsExitCode(int exitCode)
    {
        unchecked
        {
            var u = (uint)exitCode;
            return u switch
            {
                0xC0000135 => "必要な DLL が見つかりません（プラグイン DLL や VC++ 再頒布可能パッケージ、作業フォルダを確認）。",
                0xC000007B => "実行形式が無効です（32/64 ビットの不一致など）。",
                0xC0000142 => "DLL の初期化に失敗しました。",
                _ => "sonic-annotator 同梱の DLL / vamp プラグイン、および Microsoft Visual C++ 再頒布可能パッケージを確認してください。"
            };
        }
    }
}
