using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Diffract.Leftovers.Cleanup;

/// <summary>One line of the operation log.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(CleanupStarted), "started")]
[JsonDerivedType(typeof(StepLogged), "step")]
public abstract record LogLine(string CleanupId);

/// <param name="Undoes">For an undo, the cleanup it takes back. Null for a cleanup.</param>
/// <param name="Leftovers">What was selected, so the history can say what a cleanup was about.</param>
public sealed record CleanupStarted(string CleanupId, string RepoPath, DateTimeOffset At, string? Undoes, IReadOnlyList<LoggedLeftover> Leftovers)
    : LogLine(CleanupId);

/// <summary>A leftover as the log and the history name it.</summary>
public sealed record LoggedLeftover(LeftoverKind Kind, string Name, string? Path)
{
    public static LoggedLeftover Of(Leftover leftover) => new(leftover.Kind, leftover.Name, leftover.Path);
}

/// <summary>A step that ran, or was skipped, with what takes it back when it was done.</summary>
public sealed record StepLogged(string CleanupId, StepResult Result, LoggedCommand? Undo, IReadOnlyList<Expectation> UndoExpects, string? UndoNote)
    : LogLine(CleanupId);

/// <summary>A git command as the log keeps it: cleanup commands only ever write lines to standard input.</summary>
public sealed record LoggedCommand(IReadOnlyList<string> Arguments, IReadOnlyList<string> Input);

/// <summary>A cleanup or an undo, as the log has it.</summary>
public sealed record LoggedCleanup(CleanupStarted Started, IReadOnlyList<StepLogged> Steps);

/// <summary>
/// Every cleanup and undo, step by step, so any of them can be taken back later. One file per repo in Diffract's own
/// folder, never in the repo (decision 2), named after the repo's path. Each line is written through to disk before
/// the next step runs. A line cut short, as by a crash, is left out when reading.
/// </summary>
public sealed class OperationLog(string folder)
{
    public static string DefaultFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Diffract", "oplog");

    public string Folder { get; } = folder;

    /// <summary>The file for a repo: the same for every way of writing its path, as Windows paths ignore case.</summary>
    public string FileFor(string repoPath)
    {
        var key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repoPath)).ToUpperInvariant();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        return Path.Combine(Folder, hash[..16] + ".jsonl");
    }

    public void Append(string repoPath, LogLine line)
    {
        Directory.CreateDirectory(Folder);
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, OperationLogJsonContext.Default.LogLine) + "\n");
        using var file = new FileStream(FileFor(repoPath), FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }

    /// <summary>Every cleanup and undo of the repo, oldest first.</summary>
    public IReadOnlyList<LoggedCleanup> History(string repoPath)
    {
        var lines = Read(repoPath);
        return
        [
            .. lines.OfType<CleanupStarted>().Select(started =>
                new LoggedCleanup(started, [.. lines.OfType<StepLogged>().Where(s => s.CleanupId == started.CleanupId)])),
        ];
    }

    private List<LogLine> Read(string repoPath)
    {
        var path = FileFor(repoPath);
        if (!File.Exists(path))
            return [];
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(file, Encoding.UTF8);
        List<LogLine> lines = [];
        while (reader.ReadLine() is { } text)
        {
            if (Parse(text) is { } line)
                lines.Add(line);
        }

        return lines;
    }

    private static LogLine? Parse(string text)
    {
        try
        {
            return JsonSerializer.Deserialize(text, OperationLogJsonContext.Default.LogLine);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(LogLine))]
internal sealed partial class OperationLogJsonContext : JsonSerializerContext;
