using System.Diagnostics;
using System.Text;

namespace Diffract.Sidecar;

/// <param name="ExitCode">As the program returned it. winget's own errors are negative, such as 0x8A150014.</param>
internal sealed record CommandResult(int ExitCode, string StandardOutput);

/// <summary>Runs a program other than git, waits for it and captures its output. Tests use a fake.</summary>
internal interface ICommandRunner
{
    /// <exception cref="TimeoutException">The program did not exit within <paramref name="timeout"/>. It was killed.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The program could not be started.</exception>
    Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
}

/// <summary>Arguments go through <see cref="ProcessStartInfo.ArgumentList"/>, never through a built command line.</summary>
internal sealed class CommandRunner(TimeProvider time) : ICommandRunner
{
    public async Task<CommandResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = psi };
        process.Start();
        process.StandardInput.Close();

        using var timer = new CancellationTokenSource(timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timer.Token);
        var stdout = process.StandardOutput.ReadToEndAsync(linked.Token);
        var stderr = process.StandardError.ReadToEndAsync(linked.Token);
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
            if (timer.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalSeconds:0} seconds.");
            throw;
        }
        return new CommandResult(process.ExitCode, stdout.Result);
    }
}
