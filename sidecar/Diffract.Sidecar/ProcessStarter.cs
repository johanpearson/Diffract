using System.Diagnostics;

namespace Diffract.Sidecar;

/// <summary>Starts a program and does not wait for it. Tests use a fake to see what would have started.</summary>
internal interface IProcessStarter
{
    /// <param name="newConsole">
    /// Give a console program its own window. The sidecar has no console of its own, so without this a program
    /// such as PowerShell would start with no window at all.
    /// </param>
    void Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool newConsole = false);
}

/// <summary>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never through a built command line.</summary>
internal sealed class ProcessStarter : IProcessStarter
{
    public void Start(string fileName, IReadOnlyList<string> arguments, string? workingDirectory = null, bool newConsole = false)
    {
        // Shell execute is what gives a console program a new window. The file name is still a full path, and
        // ArgumentList still quotes each argument, so nothing is ever parsed as a command line of our making.
        var psi = new ProcessStartInfo(fileName) { UseShellExecute = newConsole };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);
        if (workingDirectory is not null)
            psi.WorkingDirectory = workingDirectory;
        Process.Start(psi)?.Dispose();
    }
}
