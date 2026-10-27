using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diffract.Leftovers;

/// <summary>
/// A row the user hid, and what it looked like then. The same leftover comes back by itself once it changes: its
/// commit moves, as a branch with new commits or a session with new checkpoints, its count of commits at risk
/// changes, or its verdict does.
/// </summary>
public sealed record HiddenRow
{
    public required LeftoverKind Kind { get; init; }

    public required string Name { get; init; }

    public string? Path { get; init; }

    public string? Agent { get; init; }

    public string? Commit { get; init; }

    public int? CommitsAtRisk { get; init; }

    public Verdict Verdict { get; init; }

    public static HiddenRow Of(Leftover leftover) => new()
    {
        Kind = leftover.Kind,
        Name = leftover.Name,
        Path = leftover.Path,
        Agent = leftover.Agent,
        Commit = leftover.Commit,
        CommitsAtRisk = leftover.CommitsAtRisk,
        Verdict = leftover.Verdict,
    };

    /// <summary>The same leftover, as the classifier tells them apart.</summary>
    public bool IsOf(Leftover leftover) =>
        Kind == leftover.Kind && Name == leftover.Name && string.Equals(Path, leftover.Path, StringComparison.OrdinalIgnoreCase) && Agent == leftover.Agent;

    /// <summary>The same leftover, unchanged since it was hidden.</summary>
    public bool Hides(Leftover leftover) =>
        IsOf(leftover) && Commit == leftover.Commit && CommitsAtRisk == leftover.CommitsAtRisk && Verdict == leftover.Verdict;
}

/// <summary>The rows the list shows, and those it leaves out until they change or are shown again.</summary>
public sealed record SplitRows(IReadOnlyList<Leftover> Shown, IReadOnlyList<Leftover> Hidden);

/// <summary>
/// The rows the user hid, one file per repo in Diffract's own folder next to the operation log, never in the repo.
/// Hiding changes nothing in the repo: it only leaves a row out of the list and its counts.
/// </summary>
public sealed class HiddenRows(string folder)
{
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diffract", "hidden");

    public string Folder { get; } = folder;

    /// <summary>One repo's hidden rows, in a file named after its path as the operation log names its files.</summary>
    public RepoHiddenRows For(string repoPath)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath)).ToUpperInvariant();
        return new RepoHiddenRows(Path.Combine(Folder, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16] + ".json"));
    }
}

/// <summary>One repo's hidden rows.</summary>
public sealed class RepoHiddenRows(string file)
{
    private static readonly Lock Writing = new();

    public string File { get; } = file;

    /// <summary>Splits the leftovers into those to show and those still hidden. Starts no git, reads one file.</summary>
    public SplitRows Split(IReadOnlyList<Leftover> leftovers)
    {
        var hidden = Read();
        return new SplitRows([.. leftovers.Where(l => !hidden.Any(h => h.Hides(l)))], [.. leftovers.Where(l => hidden.Any(h => h.Hides(l)))]);
    }

    /// <summary>Hides the row as it is now. Rows hidden earlier that changed since, or are gone, are forgotten.</summary>
    public void Hide(Leftover leftover, IReadOnlyList<Leftover> current) =>
        Change(current, rows => [.. rows.Where(r => !r.IsOf(leftover)), HiddenRow.Of(leftover)]);

    public void Unhide(Leftover leftover, IReadOnlyList<Leftover> current) =>
        Change(current, rows => [.. rows.Where(r => !r.IsOf(leftover))]);

    private void Change(IReadOnlyList<Leftover> current, Func<IEnumerable<HiddenRow>, IReadOnlyList<HiddenRow>> change)
    {
        lock (Writing)
            Write(change(Read().Where(r => current.Any(r.Hides))));
    }

    /// <summary>None when the file is missing or cannot be read: a lost hide only shows a row again.</summary>
    private IReadOnlyList<HiddenRow> Read()
    {
        try
        {
            return JsonSerializer.Deserialize(System.IO.File.ReadAllText(File), HiddenRowsJsonContext.Default.IReadOnlyListHiddenRow) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Written whole to a file next to it, then moved over it, so a crash never leaves half a file.</summary>
    private void Write(IReadOnlyList<HiddenRow> rows)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(File)!);
        var temp = File + ".tmp";
        System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(rows, HiddenRowsJsonContext.Default.IReadOnlyListHiddenRow));
        System.IO.File.Move(temp, File, overwrite: true);
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(IReadOnlyList<HiddenRow>))]
internal sealed partial class HiddenRowsJsonContext : JsonSerializerContext;
