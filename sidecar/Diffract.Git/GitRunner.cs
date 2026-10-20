using System.Diagnostics;
using System.Text;

namespace Diffract.Git;

/// <summary>Runs git as a child process in one working directory.</summary>
public sealed class GitRunner(string workingDirectory, GitRunnerOptions options)
{
    public string WorkingDirectory { get; } = workingDirectory;
    public GitRunnerOptions Options { get; } = options;

    public Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        RunAsync(args, [], ct);

    /// <summary>
    /// Runs git. A non-zero exit code throws <see cref="GitCommandException"/> unless it is in
    /// <paramref name="allowedExitCodes"/>, for commands like <c>merge-base --is-ancestor</c> that answer with it.
    /// </summary>
    public async Task<GitResult> RunAsync(IReadOnlyList<string> args, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct)
    {
        if (Options.ReadOnly && !ReadOnlyPolicy.IsAllowed(args, out var reason))
            throw new ReadOnlyViolationException(args, reason);

        ct.ThrowIfCancellationRequested();
        var time = Options.TimeProvider;
        var started = time.GetTimestamp();

        using var process = new Process { StartInfo = CreateStartInfo(args) };
        process.Start();
        process.StandardInput.Close();

        using var timeout = new CancellationTokenSource(Options.Timeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, linked.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(linked.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await Task.WhenAll(stdoutTask, stderrTask).ContinueWith(_ => { }, CancellationToken.None).ConfigureAwait(false);
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new GitTimeoutException(args, Options.Timeout);
            throw;
        }

        var result = new GitResult(args, process.ExitCode, stdoutTask.Result, stderrTask.Result, time.GetElapsedTime(started));
        if (result.ExitCode != 0 && !allowedExitCodes.Contains(result.ExitCode))
            throw new GitCommandException(args, result.ExitCode, result.StandardError);
        return result;
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args)
    {
        var psi = new ProcessStartInfo(Options.GitExecutable)
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        foreach (var (name, value) in Options.Environment)
            psi.Environment[name] = value;

        // Set last so options can never turn them off.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // Same as `-c core.longpaths=true`: git for Windows can then reach paths over 260 characters,
        // whatever the user's config says. It changes how paths are opened, never what is written.
        psi.Environment["GIT_CONFIG_COUNT"] = "1";
        psi.Environment["GIT_CONFIG_KEY_0"] = "core.longpaths";
        psi.Environment["GIT_CONFIG_VALUE_0"] = "true";
        return psi;
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return buffer.ToArray();
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
