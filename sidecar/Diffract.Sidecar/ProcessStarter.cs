using System.Diagnostics;

namespace Diffract.Sidecar;

/// <summary>Starts a program and does not wait for it. Tests use a fake to see what would have started.</summary>
internal interface IProcessStarter
{
    void Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null);
}

/// <summary>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never through a built command line.</summary>
internal sealed class ProcessStarter : IProcessStarter
{
    public void Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null)
    {
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        Process.Start(psi)?.Dispose();
    }
}
