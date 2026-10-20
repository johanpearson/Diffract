using System.Text;
using Diffract.Git;

namespace Diffract.Spike;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };

        return args switch
        {
            ["scan", var repoPath, ..] when !repoPath.StartsWith("--", StringComparison.Ordinal) =>
                await new ScanCommand(Console.Out, Console.Error, new GitRunnerOptions(), TimeProvider.System)
                    .RunAsync(repoPath, args.AsMemory(2), Environment.CurrentDirectory, cancel.Token),
            ["fixtures", var outputDir, ..] => await FixturesCommand.RunAsync(outputDir, args.AsMemory(2)),
            _ => Usage(Console.Error),
        };
    }

    public static int Usage() => Usage(Console.Error);

    public static int Usage(TextWriter error)
    {
        error.WriteLine("usage: diffract-spike scan <repo-path> [--json] [--timing] [--config <path>]");
        error.WriteLine("       diffract-spike fixtures <output-dir> [--scenario <name>]");
        return ExitCodes.Error;
    }
}

internal static class ExitCodes
{
    public const int Success = 0;
    public const int Error = 1;
    public const int NotAGitRepository = 2;
    public const int GitTooOld = 3;
}
