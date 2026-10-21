using System.Globalization;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;

namespace Diffract.Spike;

/// <summary>The human-readable scan output: a header, one table row per leftover, a summary line and optional timings.</summary>
internal static class ScanText
{
    private static readonly string[] Headers = ["Verdict", "Kind", "Name", "Agent", "At risk", "Reason"];

    public static void Write(TextWriter output, ScanReport report)
    {
        var snapshot = report.Snapshot;
        output.WriteLine($"Diffract spike  {snapshot.RepoRoot}");
        output.WriteLine($"main: {snapshot.MainBranch}   git: {snapshot.GitVersion}   snapshot: {Ms(report.SnapshotDuration)}");
        output.WriteLine();

        if (report.Leftovers.Count == 0)
        {
            output.WriteLine("No leftovers.");
        }
        else
        {
            WriteTable(output, report.Leftovers);
            output.WriteLine();
            output.WriteLine(Summary(report.Leftovers));
        }

        if (report.Timings is { } timings)
        {
            output.WriteLine();
            WriteTimings(output, timings);
        }
    }

    public static string VerdictLabel(Verdict verdict) => verdict switch
    {
        Verdict.SafeToRemove => "Safe",
        Verdict.NeedsReview => "Needs review",
        Verdict.InUse => "In use",
        _ => verdict.ToString(),
    };

    public static string KindLabel(LeftoverKind kind) => kind switch
    {
        LeftoverKind.PrunableWorktree => "Prunable",
        LeftoverKind.LocalBranch => "Branch",
        LeftoverKind.RemoteBranch => "Remote branch",
        LeftoverKind.CheckpointSession => "Checkpoint",
        LeftoverKind.OrphanedCommit => "Orphans",
        LeftoverKind.BackupTag => "Backup tag",
        LeftoverKind.LeftoverFolder => "Folder",
        _ => kind.ToString(),
    };

    /// <summary>
    /// The commit count, or "-" for a folder: git does not know it, so nothing about it can be counted,
    /// and "0" would wrongly read as "nothing to lose".
    /// </summary>
    public static string AtRiskLabel(Leftover leftover) =>
        leftover.CommitsAtRisk?.ToString(CultureInfo.InvariantCulture) ?? "-";

    /// <summary>"4 leftovers: 2 safe, 1 needs review, 1 in use". Verdicts with no leftovers are left out.</summary>
    public static string Summary(IReadOnlyList<Leftover> leftovers)
    {
        var parts = new[] { (Verdict.SafeToRemove, "safe"), (Verdict.NeedsReview, "needs review"), (Verdict.InUse, "in use") }
            .Select(p => (Count: leftovers.Count(l => l.Verdict == p.Item1), Label: p.Item2))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {p.Label}");
        var noun = leftovers.Count == 1 ? "leftover" : "leftovers";
        return $"{leftovers.Count} {noun}: {string.Join(", ", parts)}";
    }

    private static void WriteTable(TextWriter output, IReadOnlyList<Leftover> leftovers)
    {
        var rows = leftovers
            .Select(l => new[]
            {
                VerdictLabel(l.Verdict), KindLabel(l.Kind), l.Name, l.Agent ?? "-",
                AtRiskLabel(l), l.Reason,
            })
            .Prepend(Headers)
            .ToList();

        // The last column is not padded, so long reasons do not leave trailing spaces.
        var widths = Enumerable.Range(0, Headers.Length - 1).Select(c => rows.Max(r => r[c].Length)).ToArray();
        foreach (var row in rows)
            output.WriteLine(string.Concat(widths.Select((w, c) => row[c].PadRight(w + 2))) + row[^1]);
    }

    // fsck is the slowest call by far and runs alongside the others, so it gets its own line.
    private static void WriteTimings(TextWriter output, IReadOnlyList<GitCallTiming> timings)
    {
        output.WriteLine("Git calls, slowest first:");
        foreach (var call in timings.OrderByDescending(t => t.Duration))
            output.WriteLine($"{Ms(call.Duration),9}  {Shorten(call.Command)}");

        var fsck = timings.Where(IsFsck).ToList();
        var others = timings.Where(t => !IsFsck(t)).ToList();
        output.WriteLine();
        output.WriteLine(fsck.Count == 0 ? "fsck: not run" : $"fsck: {Ms(fsck.Max(t => t.Duration))}");
        output.WriteLine(others.Count == 0
            ? "Other calls: none"
            : $"Other calls: {others.Count}, slowest {Ms(others.Max(t => t.Duration))}");
    }

    /// <summary>Keeps the first commit id of a command and counts the rest, since some calls pass hundreds.</summary>
    public static string Shorten(string command)
    {
        var parts = command.Split(' ');
        var ids = parts.Count(IsCommitId);
        if (ids <= 1)
            return command;

        var firstId = Array.FindIndex(parts, IsCommitId);
        var kept = parts.Where((p, i) => i <= firstId || !IsCommitId(p));
        return $"{string.Join(' ', kept)} (+{ids - 1} more ids)";
    }

    private static bool IsCommitId(string part) => part.Length == 40 && part.All(char.IsAsciiHexDigitLower);

    private static bool IsFsck(GitCallTiming timing) => timing.Command.StartsWith("git fsck", StringComparison.Ordinal);

    private static string Ms(TimeSpan duration) => $"{duration.TotalMilliseconds.ToString("0", CultureInfo.InvariantCulture)} ms";
}
