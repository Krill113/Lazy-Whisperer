namespace LWhisper.DevTools;

/// <summary>
/// Метрики замера. Определения — строго спека §5:
/// rtf = elapsedMs / durationMs; tailRate = доля прогонов с rtf &gt; 1.5;
/// p10/p25 считаются и по всем прогонам, и отдельно по коротким (durationMs &lt; 5120);
/// distinctTexts — число различных textSha256 внутри плеча (детектор недетерминизма).
/// Прогоны с Error из статистики исключаются.
/// </summary>
public static class Statistics
{
    public const double TailThreshold = 1.5;

    /// <summary>
    /// Перцентиль методом nearest-rank: rank = ceil(p/100 × n), берётся элемент rank-1
    /// отсортированного по возрастанию набора. Вход может быть неотсортирован — копия сортируется внутри.
    /// Пустой набор → 0.
    /// </summary>
    public static double Percentile(IReadOnlyList<double> sortedOrUnsorted, int percentile)
    {
        if (sortedOrUnsorted == null || sortedOrUnsorted.Count == 0) return 0;
        if (percentile < 0) percentile = 0;
        if (percentile > 100) percentile = 100;

        var data = sortedOrUnsorted.ToArray();
        Array.Sort(data);

        var rank = (int)Math.Ceiling(percentile / 100.0 * data.Length);
        if (rank < 1) rank = 1;
        if (rank > data.Length) rank = data.Length;
        return data[rank - 1];
    }

    public static double TailRate(IEnumerable<RunRecord> runs, double threshold = TailThreshold)
    {
        var ok = runs.Where(r => r.Error == null).ToList();
        if (ok.Count == 0) return 0;
        return (double)ok.Count(r => r.Rtf > threshold) / ok.Count;
    }

    public static ArmSummary SummarizeArm(string arm, IEnumerable<RunRecord> runs)
    {
        var all = runs.Where(r => r.Error == null).ToList();
        var shortRuns = all.Where(r => r.DurationMs < RunReport.ShortDurationMs).ToList();
        var elapsed = all.Select(r => r.ElapsedMs).ToList();
        var shortElapsed = shortRuns.Select(r => r.ElapsedMs).ToList();

        return new ArmSummary
        {
            Arm = arm,
            N = all.Count,
            TailRate = TailRate(all),
            P10Ms = Percentile(elapsed, 10),
            P25Ms = Percentile(elapsed, 25),
            MedianMs = Percentile(elapsed, 50),
            P90Ms = Percentile(elapsed, 90),
            DistinctTexts = all.Select(r => r.TextSha256).Distinct(StringComparer.Ordinal).Count(),
            ShortN = shortRuns.Count,
            ShortTailRate = TailRate(shortRuns),
            ShortP10Ms = Percentile(shortElapsed, 10),
            ShortP25Ms = Percentile(shortElapsed, 25),
            ShortMedianMs = Percentile(shortElapsed, 50)
        };
    }

    public static RunSummary Summarize(IEnumerable<RunRecord> runs)
    {
        var list = runs.ToList();
        var total = SummarizeArm("*", list);

        var summary = new RunSummary
        {
            N = total.N,
            TailRate = total.TailRate,
            P10Ms = total.P10Ms,
            P25Ms = total.P25Ms,
            MedianMs = total.MedianMs,
            P90Ms = total.P90Ms,
            ShortN = total.ShortN,
            ShortTailRate = total.ShortTailRate,
            ShortP10Ms = total.ShortP10Ms,
            ShortP25Ms = total.ShortP25Ms,
            ShortMedianMs = total.ShortMedianMs
        };

        foreach (var group in list.GroupBy(r => r.Arm, StringComparer.Ordinal)
                                  .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            summary.ByArm.Add(SummarizeArm(group.Key, group));
        }

        return summary;
    }

    /// <summary>
    /// Сравнение с базлайном. Тексты сверяются посимвольно (спека §5, правило 3) по первому
    /// успешному прогону каждого файла: greedy-декодирование детерминировано, расхождение = регрессия.
    /// Дельты считаются как «текущий минус базлайн».
    /// </summary>
    public static BaselineDiff CompareToBaseline(RunReport current, RunReport baseline, string baselineFile)
    {
        var diff = new BaselineDiff
        {
            BaselineFile = baselineFile,
            TailRateDelta = current.Summary.TailRate - baseline.Summary.TailRate,
            P10DeltaMs = current.Summary.P10Ms - baseline.Summary.P10Ms
        };

        var baselineTexts = FirstTextByFileName(baseline);
        foreach (var pair in FirstTextByFileName(current))
        {
            if (!baselineTexts.TryGetValue(pair.Key, out var baselineText)) continue;
            if (string.Equals(baselineText, pair.Value, StringComparison.Ordinal)) continue;

            diff.TextMismatches++;
            diff.MismatchFiles.Add(pair.Key);
            diff.Mismatches.Add(new BaselineTextMismatch
            {
                File = pair.Key,
                BaselineText = baselineText,
                CurrentText = pair.Value
            });
        }

        diff.MismatchFiles.Sort(StringComparer.OrdinalIgnoreCase);
        diff.Mismatches.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.File, b.File));
        return diff;
    }

    private static Dictionary<string, string> FirstTextByFileName(RunReport report)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var run in report.Runs)
        {
            if (run.Error != null) continue;
            var key = Path.GetFileName(run.File);
            if (!map.ContainsKey(key)) map[key] = run.Text;
        }
        return map;
    }
}
