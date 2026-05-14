using System.IO;

namespace ChordproExtractor;

/// <summary>解析ボタン 1 回分の結果（UI へ反映するための DTO）。</summary>
internal sealed record ConvertPipelineResult(
    bool Success,
    IReadOnlyList<ChordPoint>? Chords,
    List<double>? BarStarts,
    double BpmValue,
    string? BpmDisplayForTextBox,
    string StatusMessage,
    List<string> Warnings,
    string? ChordproPaneErrorBody);

/// <summary>Demucs → Chordino / Bar / BPM までのオーケストレーション。</summary>
internal static class AudioConvertPipeline
{
    internal static async Task<ConvertPipelineResult> RunAsync(
        string baseDir,
        string selectedAudioPath,
        string bpmTextBoxText,
        CancellationToken cancellationToken)
    {
        var demucsWrapper = Path.GetFullPath(Path.Combine(baseDir, "demucs_wrapper.py"));
        var demucsExe = Path.GetFullPath(Path.Combine(baseDir, "tools", "demucs-worker.exe"));
        if (!File.Exists(demucsWrapper) && !File.Exists(demucsExe))
        {
            return new ConvertPipelineResult(
                false, null, null, 0, null,
                "Demucs 用に demucs_wrapper.py（出力にコピー）または tools/demucs-worker.exe のどちらかが必要です。",
                [],
                null);
        }

        var toolPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "tools", "sonic-annotator.exe"));
        if (!File.Exists(toolPath))
        {
            return new ConvertPipelineResult(
                false, null, null, 0, null,
                $"Sonic Annotator が見つかりません: {toolPath}",
                [],
                null);
        }

        string? ownDemucsDir = null;
        var demucsMetaWritten = false;
        var pipelineCompleted = false;

        try
        {
            DemucsWorkCache.EnsureWorkRootExists();

            double bpmValue = 0;
            string? bpmDisplay = null;
            if (BpmInput.TryParseUserBpm(bpmTextBoxText, out var manualBpm))
            {
                bpmValue = manualBpm;
            }
            else
            {
                var (bpmExit, bpmStderr, autoBpm) =
                    await SonicAnnotatorClient.EstimateBpmAsync(toolPath, selectedAudioPath, cancellationToken)
                        .ConfigureAwait(false);

                if (bpmExit != 0)
                {
                    var errBody = ProcessExecution.BuildProcessErrorBody(null, bpmStderr);
                    return new ConvertPipelineResult(
                        false, null, null, 0, null,
                        $"BPM 自動検出が失敗しました（終了コード {bpmExit}）。",
                        [],
                        errBody);
                }

                if (!autoBpm.HasValue)
                {
                    return new ConvertPipelineResult(
                        false, null, null, 0, null,
                        "BPM を結果から読み取れませんでした。qm-tempotracker の出力を確認するか、BPM を手動入力してください。",
                        [],
                        null);
                }

                bpmValue = autoBpm.Value;
                bpmDisplay = BpmInput.FormatBpmForDisplay(bpmValue);
            }

            var demucsWorkDir = DemucsWorkCache.TryFindReusableWorkDir(selectedAudioPath);
            var reusedDemucsCache = demucsWorkDir != null;
            if (demucsWorkDir != null)
            {
                DemucsWorkCache.TouchWorkDir(demucsWorkDir);
            }
            else
            {
                demucsWorkDir = Path.Combine(
                    ChordproPaths.DemucsWorkRoot,
                    "demucs_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(demucsWorkDir);
                ownDemucsDir = demucsWorkDir;

                var (demucsExit, demucsStderr, demucsStdout) =
                    await DemucsRunner.RunSeparationAsync(
                        baseDir,
                        demucsWrapper,
                        demucsExe,
                        selectedAudioPath,
                        demucsWorkDir,
                        cancellationToken).ConfigureAwait(false);

                if (demucsExit != 0)
                {
                    if (demucsExit == -1)
                    {
                        return new ConvertPipelineResult(
                            false, null, null, bpmValue, bpmDisplay,
                            "Demucs を起動できませんでした。",
                            [],
                            string.IsNullOrWhiteSpace(demucsStderr) ? null : demucsStderr.Trim());
                    }

                    var hint = ProcessExecution.DescribeWindowsExitCode(demucsExit);
                    var errBody = ProcessExecution.BuildProcessErrorBody(demucsStdout, demucsStderr);
                    return new ConvertPipelineResult(
                        false, null, null, bpmValue, bpmDisplay,
                        $"Demucs が失敗しました（終了コード {demucsExit}）。{hint}",
                        [],
                        errBody);
                }

                if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out _, out _))
                {
                    return new ConvertPipelineResult(
                        false, null, null, bpmValue, bpmDisplay,
                        "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。出力フォルダ構成を確認してください。",
                        [],
                        "探索ルート: " + demucsWorkDir);
                }

                DemucsWorkCache.WriteSourceMeta(demucsWorkDir, selectedAudioPath);
                demucsMetaWritten = true;
            }

            if (!DemucsWorkCache.TryResolveDemucsStemPaths(demucsWorkDir, out _, out var noVocalsWav))
            {
                return new ConvertPipelineResult(
                    false, null, null, bpmValue, bpmDisplay,
                    "Demucs 出力に vocals.wav / no_vocals.wav が見つかりません。キャッシュが壊れている可能性があります。",
                    [],
                    "探索ルート: " + demucsWorkDir);
            }

            var chordTask = SonicAnnotatorClient.RunChordinoAsync(toolPath, noVocalsWav, cancellationToken);
            var barTask = SonicAnnotatorClient.RunVampCsvLinesAsync(
                toolPath,
                noVocalsWav,
                SonicAnnotatorClient.VampQmBarBeatTrackerBars,
                cancellationToken);
            await Task.WhenAll(chordTask, barTask).ConfigureAwait(false);

            var (exitCode, stderr, chords) = await chordTask.ConfigureAwait(false);
            var (barExit, _, barLines) = await barTask.ConfigureAwait(false);

            if (exitCode != 0)
            {
                var hint = ProcessExecution.DescribeWindowsExitCode(exitCode);
                var errText = string.IsNullOrWhiteSpace(stderr)
                    ? null
                    : "[標準エラー出力]" + Environment.NewLine + stderr.Trim();
                return new ConvertPipelineResult(
                    false, null, null, bpmValue, bpmDisplay,
                    $"Chordino が失敗しました（終了コード {exitCode}）。{hint}",
                    [],
                    errText);
            }

            var warnParts = new List<string>();
            if (barExit != 0)
                warnParts.Add($"Bar Tracker 失敗（終了コード {barExit}）。小節番号は時刻表示に切り替わります。");

            var chordList = chords.ToList();
            chordList.Sort((a, b) => a.Seconds.CompareTo(b.Seconds));

            var barStarts = SonicCsvParser.ParseAndNormalizeBarStartTimes(barLines);

            if (barExit == 0 && barStarts.Count == 0 && barLines.Count > 0)
                warnParts.Add("Bar Tracker の CSV から小節時刻を解釈できませんでした。");

            var okMsg = reusedDemucsCache
                ? "Demucs（キャッシュ再利用）+ Chordino が完了しました。右のコードをクリックして歌詞へ挿入できます。"
                : "Demucs + Chordino が完了しました。右のコードをクリックして歌詞へ挿入できます。";
            var status = warnParts.Count > 0 ? okMsg + " " + string.Join(" ", warnParts) : okMsg;

            pipelineCompleted = true;
            return new ConvertPipelineResult(
                true,
                chordList,
                barStarts,
                bpmValue,
                bpmDisplay,
                status,
                warnParts,
                null);
        }
        finally
        {
            if (!pipelineCompleted)
                TryDeleteIncompleteDemucsWorkDir(ownDemucsDir, demucsMetaWritten);
        }
    }

    private static void TryDeleteIncompleteDemucsWorkDir(string? ownDemucsDir, bool demucsMetaWritten)
    {
        if (string.IsNullOrEmpty(ownDemucsDir) || demucsMetaWritten)
            return;

        try
        {
            if (Directory.Exists(ownDemucsDir))
                Directory.Delete(ownDemucsDir, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Debug(ex, "未完了 Demucs 作業フォルダの削除");
        }
    }
}
