namespace Diffract.Sidecar.Tests;

/// <summary>Records what would have been started. Nothing real ever opens in a test.</summary>
internal sealed class FakeProcesses : IProcessStarter
{
    public List<(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory, bool NewConsole)> Started { get; } = [];

    public void Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool newConsole = false) =>
        Started.Add((fileName, [.. arguments], workingDirectory, newConsole));
}
