using System.ComponentModel;
using Diffract.Git;
using Diffract.Git.Snapshot;
using Diffract.Leftovers;

namespace Diffract.Spike;

/// <summary>
/// <c>scan &lt;repo-path&gt; [--json] [--timing] [--config &lt;path&gt;]</c>: snapshots a repo with a read-only
/// git runner and prints its leftovers. Never writes to the scanned repo.
/// </summary>
internal sealed class ScanCommand(TextWriter output, TextWriter error, GitRunnerOptions runnerOptions, TimeProvider time)
{
    /// <param name="runningFrom">The folder the spike runs from. The worktree that contains it is never reported.</param>
    public async Task<int> RunAsync(string repoPath, ReadOnlyMemory<string> options, string? runningFrom, CancellationToken ct)
    {
        if (ScanOptions.Parse(options.Span) is not { } scan)
            return Program.Usage(error);

        try
        {
            var config = scan.ConfigPath is null ? DiffractConfig.Default : DiffractConfig.LoadFile(scan.ConfigPath);
            var builder = new RepoSnapshotBuilder(runnerOptions with { ReadOnly = true }, config.ToSnapshotOptions());

            var started = time.GetTimestamp();
            var result = await builder.BuildAsync(repoPath, ct);
            var elapsed = time.GetElapsedTime(started);

            var leftovers = new LeftoverClassifier().Classify(result.Snapshot, config, time, runningFrom);
            var report = new ScanReport(result.Snapshot, leftovers, elapsed, scan.Timing ? result.Timings : null);
            if (scan.Json)
                ScanJson.Write(output, report);
            else
                ScanText.Write(output, report);
            return ExitCodes.Success;
        }
        catch (NotAGitRepositoryException e)
        {
            error.WriteLine(e.Message);
            return ExitCodes.NotAGitRepository;
        }
        catch (GitVersionTooOldException e)
        {
            error.WriteLine(e.Message);
            return ExitCodes.GitTooOld;
        }
        catch (Exception e) when (e is GitException or ConfigException)
        {
            error.WriteLine(e.Message);
            return ExitCodes.Error;
        }
        catch (Win32Exception e)
        {
            error.WriteLine($"Could not start git ({runnerOptions.GitExecutable}): {e.Message}. Is git installed and on PATH?");
            return ExitCodes.Error;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            error.WriteLine("Cancelled.");
            return ExitCodes.Error;
        }
    }
}

internal sealed record ScanOptions(bool Json, bool Timing, string? ConfigPath)
{
    /// <summary>Null when the options are not valid.</summary>
    public static ScanOptions? Parse(ReadOnlySpan<string> args)
    {
        var options = new ScanOptions(false, false, null);
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--json" when !options.Json:
                    options = options with { Json = true };
                    break;
                case "--timing" when !options.Timing:
                    options = options with { Timing = true };
                    break;
                case "--config" when options.ConfigPath is null && i + 1 < args.Length:
                    options = options with { ConfigPath = args[++i] };
                    break;
                default:
                    return null;
            }
        }
        return options;
    }
}

/// <param name="Timings">Per git call, only when --timing was given.</param>
internal sealed record ScanReport(
    RepoSnapshot Snapshot,
    IReadOnlyList<Leftover> Leftovers,
    TimeSpan SnapshotDuration,
    IReadOnlyList<GitCallTiming>? Timings);
