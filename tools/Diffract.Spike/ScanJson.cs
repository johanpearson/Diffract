using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;

namespace Diffract.Spike;

/// <summary>The <c>--json</c> scan output, written with the source-generated serializer.</summary>
internal static class ScanJson
{
    // Relaxed escaping keeps quotes and å ä ö readable. The output is never embedded in HTML.
    private static readonly JsonSerializerOptions Options = new(ScanJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static void Write(TextWriter output, ScanReport report)
    {
        var typeInfo = (JsonTypeInfo<ScanDocument>)Options.GetTypeInfo(typeof(ScanDocument));
        output.WriteLine(JsonSerializer.Serialize(ToDocument(report), typeInfo));
    }

    public static ScanDocument ToDocument(ScanReport report)
    {
        var s = report.Snapshot;
        var leftovers = report.Leftovers;
        return new ScanDocument(
            RepoRoot: s.RepoRoot,
            MainBranch: s.MainBranch,
            GitVersion: s.GitVersion.ToString(),
            TakenAt: s.TakenAt,
            SnapshotMs: Math.Round(report.SnapshotDuration.TotalMilliseconds),
            Snapshot: new SnapshotSummary(
                Worktrees: s.Worktrees,
                LocalBranches: s.Branches.Count(b => !b.IsRemote),
                RemoteBranches: s.Branches.Count(b => b.IsRemote),
                Tags: s.Tags.Count,
                Stashes: s.Stashes.Count,
                OtherRefs: s.OtherRefs.Count,
                UnreachableCommits: s.UnreachableCommits.Count,
                LeftoverFolders: s.LeftoverFolders),
            Summary: new LeftoverSummary(
                Total: leftovers.Count,
                SafeToRemove: leftovers.Count(l => l.Verdict == Verdict.SafeToRemove),
                NeedsReview: leftovers.Count(l => l.Verdict == Verdict.NeedsReview),
                InUse: leftovers.Count(l => l.Verdict == Verdict.InUse)),
            Leftovers: leftovers,
            Timings: report.Timings?.Select(t => new CallTiming(t.Command, Math.Round(t.Duration.TotalMilliseconds, 1))).ToList());
    }
}

internal sealed record ScanDocument(
    string RepoRoot,
    string MainBranch,
    string GitVersion,
    DateTimeOffset TakenAt,
    double SnapshotMs,
    SnapshotSummary Snapshot,
    LeftoverSummary Summary,
    IReadOnlyList<Leftover> Leftovers,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<CallTiming>? Timings);

/// <summary>Counts for refs, which can number in the hundreds; worktrees and leftover folders in full.</summary>
internal sealed record SnapshotSummary(
    IReadOnlyList<WorktreeInfo> Worktrees,
    int LocalBranches,
    int RemoteBranches,
    int Tags,
    int Stashes,
    int OtherRefs,
    int UnreachableCommits,
    IReadOnlyList<LeftoverFolderInfo> LeftoverFolders);

internal sealed record LeftoverSummary(int Total, int SafeToRemove, int NeedsReview, int InUse);

internal sealed record CallTiming(string Command, double Ms);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(ScanDocument))]
internal sealed partial class ScanJsonContext : JsonSerializerContext;
