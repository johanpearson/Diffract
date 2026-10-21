namespace Diffract.Sidecar.Tests;

/// <summary>Records what would have been started. Nothing real ever opens in a test.</summary>
internal sealed class FakeProcesses : IProcessStarter
{
    public List<(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, bool NewConsole)> Started { get; } = [];

    public void Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool newConsole = false) =>
        Started.Add((fileName, [.. arguments], workingDirectory, newConsole));
}

/// <summary>Answers instead of running a program, and records what would have run. winget never runs in a test.</summary>
internal sealed class FakeCommands(Func<string, IReadOnlyList<string>, CommandResult> answer) : ICommandRunner
{
    public List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Ran { get; } = [];

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Ran.Add((fileName, [.. arguments], timeout));
        return Task.FromResult(answer(fileName, arguments));
    }
}
