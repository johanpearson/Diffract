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
    private Func<string, IReadOnlyList<string>, Task<CommandResult>> _answer = (f, a) => Task.FromResult(answer(f, a));

    /// <summary>For a program that answers later, such as an update the test holds until it says so.</summary>
    public static FakeCommands Async(Func<string, IReadOnlyList<string>, Task<CommandResult>> answer) =>
        new((_, _) => throw new InvalidOperationException()) { _answer = answer };

    public List<(string FileName, IReadOnlyList<string> Arguments, TimeSpan Timeout)> Ran { get; } = [];

    /// <summary>The cancellation token of each run, in the same order as <see cref="Ran"/>.</summary>
    public List<CancellationToken> Tokens { get; } = [];

    public Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        Ran.Add((fileName, [.. arguments], timeout));
        Tokens.Add(ct);
        return _answer(fileName, arguments);
    }
}

/// <summary>A process list the test writes. Programs are described by their file name without the extension, unless told otherwise.</summary>
internal sealed class FakeProcessTable : IProcessTable
{
    private readonly List<ProcessInfo> _processes = [];
    private readonly Lock _lock = new();

    /// <summary>Descriptions by file name, such as "Code.exe" = "Visual Studio Code".</summary>
    public Dictionary<string, string> Descriptions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public FakeProcessTable Add(params ProcessInfo[] processes)
    {
        lock (_lock)
            _processes.AddRange(processes);
        return this;
    }

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        lock (_lock)
            return [.. _processes];
    }

    public string Describe(string path) =>
        Descriptions.TryGetValue(Path.GetFileName(path), out var description) ? description : Path.GetFileNameWithoutExtension(path);
}
