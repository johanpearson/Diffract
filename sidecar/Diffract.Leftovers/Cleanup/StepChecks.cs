using Diffract.Git;
using Diffract.Git.Parsing;

namespace Diffract.Leftovers.Cleanup;

/// <summary>What the checks found: the first expectation that broke, and where the stash to drop is now.</summary>
/// <param name="Broken">Why the step must not run, in plain English. Null when everything still holds.</param>
/// <param name="StashName">The stash's place in the list right now, such as "stash@{1}", when the step expected one.</param>
internal sealed record Checked(string? Broken, string? StashName = null)
{
    public static readonly Checked Holds = new(Broken: null);
}

/// <summary>
/// Checks a step's expectations right before it runs, with as few git calls as the kinds need: one for all its refs,
/// one for all its objects, two for a worktree and two for a stash. Everything here only reads.
/// </summary>
internal sealed class StepChecks(string repoPath, GitRunnerOptions git, IFolderOperations folders)
{
    private const string RefFormat = "--format=%(refname)%00%(objectname)%00%(*objectname)";

    private readonly GitRunner _reads = new(repoPath, ReadOnly(git));

    public async Task<Checked> CheckAsync(IReadOnlyList<Expectation> expects, CancellationToken ct)
    {
        var broken = Folders(expects)
            ?? await RefsAsync(expects, ct).ConfigureAwait(false)
            ?? await ObjectsAsync(expects, ct).ConfigureAwait(false)
            ?? await WorktreesAsync(expects, ct).ConfigureAwait(false);
        return broken is null ? await StashAsync(expects, ct).ConfigureAwait(false) : new Checked(broken);
    }

    public static GitRunnerOptions ReadOnly(GitRunnerOptions git) => git with { ReadOnly = true, ChangesOnly = false, CleanupOnly = false };

    private string? Folders(IReadOnlyList<Expectation> expects) =>
        expects.Select(e => e switch
        {
            Expectation.NoFilesInUse free => folders.InUse(free.Path),
            Expectation.PathAbsent absent when Path.Exists(absent.Path) => $"Something is at {absent.Path} now, so the worktree is not added there again.",
            _ => null,
        }).FirstOrDefault(why => why is not null);

    private async Task<string?> RefsAsync(IReadOnlyList<Expectation> expects, CancellationToken ct)
    {
        List<string> names = [.. expects.Select(RefName).OfType<string>().Distinct()];
        if (names.Count == 0)
            return null;
        var output = (await _reads.RunAsync(["for-each-ref", RefFormat, .. names], ct).ConfigureAwait(false)).StandardOutput;
        var refs = output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Split('\0'))
            .ToDictionary(fields => fields[0], fields => new RefNow(fields[1], fields[2]), StringComparer.Ordinal);
        return expects.Select(e => RefBroken(e, refs)).FirstOrDefault(why => why is not null);
    }

    private static string? RefName(Expectation expectation) => expectation switch
    {
        Expectation.RefAt e => e.FullName,
        Expectation.RefAbsent e => e.FullName,
        Expectation.TagOn e => e.FullName,
        _ => null,
    };

    /// <summary>A ref as it is now. <paramref name="Peeled"/> is the commit an annotated tag points at, empty for other refs.</summary>
    private sealed record RefNow(string Id, string Peeled)
    {
        public string Commit => Peeled.Length > 0 ? Peeled : Id;
    }

    private static string? RefBroken(Expectation expectation, Dictionary<string, RefNow> refs) => expectation switch
    {
        Expectation.RefAt e => !refs.TryGetValue(e.FullName, out var now) ? $"{Display(e.FullName)} is gone."
            : now.Id != e.ObjectId ? $"{Display(e.FullName)} moved: it is at {Short(now.Id)} now, not {Short(e.ObjectId)}."
            : null,
        Expectation.RefAbsent e when refs.ContainsKey(e.FullName) => $"{Display(e.FullName)} is there again, so it was left as it is.",
        Expectation.TagOn e when !refs.TryGetValue(e.FullName, out var now) || now.Commit != e.CommitId => $"{Display(e.FullName)} no longer points at {Short(e.CommitId)}.",
        _ => null,
    };

    /// <summary>One call for all of them: <c>cat-file --batch-check</c> says "missing" for each one git no longer has.</summary>
    private async Task<string?> ObjectsAsync(IReadOnlyList<Expectation> expects, CancellationToken ct)
    {
        List<string> ids = [.. expects.OfType<Expectation.ObjectExists>().Select(e => e.ObjectId)];
        if (ids.Count == 0)
            return null;
        var command = new GitCommandLine(["cat-file", "--batch-check=%(objectname)"], ids);
        var output = (await _reads.RunAsync(command, ct).ConfigureAwait(false)).StandardOutput;
        var missing = output.Split('\n').FirstOrDefault(line => line.EndsWith(" missing", StringComparison.Ordinal));
        return missing is null ? null : $"Git no longer has {Short(missing)}, so it cannot be put back.";
    }

    private async Task<string?> WorktreesAsync(IReadOnlyList<Expectation> expects, CancellationToken ct)
    {
        if (expects.OfType<Expectation.WorktreeClean>().FirstOrDefault() is not { } clean)
            return null;
        var list = WorktreeListParser.Parse((await _reads.RunAsync(WorktreeListParser.Arguments, ct).ConfigureAwait(false)).StandardOutput);
        var worktree = list.FirstOrDefault(w => SamePath(w.Path, clean.Path));
        if (worktree is null || worktree.IsPrunable)
            return $"{clean.Path} is no longer a worktree.";
        if (worktree.IsLocked)
            return "The worktree is locked now.";
        var status = await new GitRunner(clean.Path, _reads.Options).RunAsync(["status", "--porcelain=v2", "-z"], ct).ConfigureAwait(false);
        return StatusParser.Parse(status.StandardOutput).Entries.Any(e => e.Kind != StatusEntryKind.Ignored)
            ? "The worktree has changes now, which git does not remove."
            : null;
    }

    /// <summary>
    /// Git drops a stash only by its place in the list, which moves as agents stash too. Its place is found by its
    /// commit, then checked once more with <c>rev-parse</c>, right before the drop (docs/findings/cleanup.md).
    /// </summary>
    private async Task<Checked> StashAsync(IReadOnlyList<Expectation> expects, CancellationToken ct)
    {
        var present = expects.OfType<Expectation.StashPresent>().FirstOrDefault();
        var absent = expects.OfType<Expectation.StashAbsent>().FirstOrDefault();
        if (present is null && absent is null)
            return Checked.Holds;
        var stashes = StashListParser.Parse((await _reads.RunAsync(StashListParser.Arguments, ct).ConfigureAwait(false)).StandardOutput);
        if (absent is not null)
            return new Checked(stashes.Any(s => s.CommitId == absent.CommitId) ? "The stash is in the list already." : null);
        return stashes.FirstOrDefault(s => s.CommitId == present!.CommitId) is { } stash
            ? await ConfirmAsync(stash, ct).ConfigureAwait(false)
            : new Checked("The stash is no longer in the list.");
    }

    private async Task<Checked> ConfirmAsync(StashEntry stash, CancellationToken ct)
    {
        var now = await _reads.RunAsync(["rev-parse", "--verify", "--quiet", stash.Name], [1], ct).ConfigureAwait(false);
        return now.StandardOutput.Trim() == stash.CommitId
            ? new Checked(null, stash.Name)
            : new Checked("The stash list changed while Diffract read it. Try again.");
    }

    private static bool SamePath(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);

    private static string Short(string id) => id[..Math.Min(8, id.Length)];

    /// <summary>A ref as people name it: "branch main", "tag v1".</summary>
    private static string Display(string fullName) => fullName switch
    {
        _ when fullName.StartsWith("refs/heads/", StringComparison.Ordinal) => $"Branch {fullName["refs/heads/".Length..]}",
        _ when fullName.StartsWith("refs/remotes/", StringComparison.Ordinal) => $"Remote-tracking branch {fullName["refs/remotes/".Length..]}",
        _ when fullName.StartsWith("refs/tags/", StringComparison.Ordinal) => $"Tag {fullName["refs/tags/".Length..]}",
        _ => $"Ref {fullName}",
    };
}
