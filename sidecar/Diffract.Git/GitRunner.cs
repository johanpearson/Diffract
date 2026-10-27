using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Diffract.Git;

/// <summary>
/// Reads git's standard output while it is written. Returns true when it read to the end, false to stop git early.
/// </summary>
public delegate Task<bool> GitOutputReader(Stream output, CancellationToken ct);

/// <summary>A git command and the lines for its standard input.</summary>
/// <param name="Input">
/// Such as the revisions for <c>--stdin</c>, which has no length limit as the command line has. Written while git
/// runs, then closed. Git stopping before it has read them all is no error.
/// </param>
public sealed record GitCommandLine(IReadOnlyList<string> Arguments, IReadOnlyList<string> Input)
{
    /// <summary>
    /// Bytes for standard input, written exactly as they are instead of <see cref="Input"/>. A patch must be, so that
    /// its CRs survive, and paths for <c>--pathspec-file-nul</c> are separated by NULs, not line feeds.
    /// </summary>
    public byte[]? RawInput { get; init; }

    public static GitCommandLine WithBytes(IReadOnlyList<string> arguments, byte[] input) => new(arguments, []) { RawInput = input };

    internal byte[] InputBytes() => RawInput ?? Encoding.UTF8.GetBytes(string.Concat(Input.Select(line => line + "\n")));
}

/// <summary>Runs git as a child process in one working directory.</summary>
public sealed class GitRunner(string workingDirectory, GitRunnerOptions options)
{
    public string WorkingDirectory { get; } = workingDirectory;
    public GitRunnerOptions Options { get; } = options;

    public Task<GitResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct) =>
        RunAsync(args, [], ct);

    /// <summary>As <see cref="RunAsync(IReadOnlyList{string}, CancellationToken)"/>, with standard input. Any non-zero exit code fails.</summary>
    public Task<GitResult> RunAsync(GitCommandLine command, CancellationToken ct) => BufferAsync(command, [], ct);

    /// <summary>With standard input, and exit codes that are returned rather than thrown.</summary>
    public Task<GitResult> RunAsync(GitCommandLine command, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct) =>
        BufferAsync(command, allowedExitCodes, ct);

    /// <summary>
    /// Runs git. A non-zero exit code throws <see cref="GitCommandException"/> unless it is in
    /// <paramref name="allowedExitCodes"/>, for commands like <c>merge-base --is-ancestor</c> that answer with it.
    /// </summary>
    public Task<GitResult> RunAsync(IReadOnlyList<string> args, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct) =>
        BufferAsync(new GitCommandLine(args, []), allowedExitCodes, ct);

    private async Task<GitResult> BufferAsync(GitCommandLine command, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var result = await StreamAsync(command, async (output, token) =>
        {
            await output.CopyToAsync(buffer, token).ConfigureAwait(false);
            return true;
        }, allowedExitCodes, ct).ConfigureAwait(false);
        return new GitResult(result, buffer.ToArray());
    }

    /// <summary>
    /// Runs git and hands its output to <paramref name="read"/> as it arrives, so large output never has to be held whole.
    /// When <paramref name="read"/> returns false, git is stopped and its exit code is not checked. When it throws,
    /// git is stopped and the exception is passed on. Timeout and cancellation cover the reading too.
    /// </summary>
    public Task<GitStreamResult> StreamAsync(IReadOnlyList<string> args, GitOutputReader read, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct) =>
        StreamAsync(new GitCommandLine(args, []), read, allowedExitCodes, ct);

    /// <summary>As the other StreamAsync, with lines for git's standard input. Any non-zero exit code fails.</summary>
    public Task<GitStreamResult> StreamAsync(GitCommandLine command, GitOutputReader read, CancellationToken ct) =>
        StreamAsync(command, read, [], ct);

    private async Task<GitStreamResult> StreamAsync(GitCommandLine command, GitOutputReader read, IReadOnlyCollection<int> allowedExitCodes, CancellationToken ct)
    {
        var args = command.Arguments;
        CheckPolicy(command);

        ct.ThrowIfCancellationRequested();
        var time = Options.TimeProvider;
        var started = time.GetTimestamp();

        Watcher?.Invoke(GitOutputLine.CommandOf(args));
        using var process = Start(args);
        try
        {
            var writing = WriteInputAsync(process.StandardInput.BaseStream, command.InputBytes());
            var result = await ReadAsync(process, args, read, ct).ConfigureAwait(false);
            await writing.ConfigureAwait(false);
            var streamed = new GitStreamResult(args, result.ExitCode, result.StandardError, time.GetElapsedTime(started));
            if (Failed(streamed.ExitCode, allowedExitCodes))
                throw new GitCommandException(args, streamed.ExitCode!.Value, streamed.StandardError);
            return streamed;
        }
        finally
        {
            Options.OnCall?.Invoke(new GitCall(args, time.GetElapsedTime(started)));
        }
    }

    private async Task<(int? ExitCode, string StandardError)> ReadAsync(Process process, IReadOnlyList<string> args, GitOutputReader read, CancellationToken ct)
    {
        using var timeout = new CancellationTokenSource(Options.Timeout, Options.TimeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var stdout = process.StandardOutput.BaseStream;
        var stderrTask = ReadErrorAsync(process.StandardError, linked.Token);

        try
        {
            if (!await read(stdout, linked.Token).ConfigureAwait(false))
            {
                Kill(process);
                return (null, await StandardErrorSoFarAsync(stderrTask).ConfigureAwait(false));
            }

            // Whatever the reader left unread, so git is never blocked writing to a full pipe.
            await stdout.CopyToAsync(Stream.Null, linked.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
            return (process.ExitCode, await stderrTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await StandardErrorSoFarAsync(stderrTask).ConfigureAwait(false);
            if (timeout.IsCancellationRequested && !ct.IsCancellationRequested)
                throw new GitTimeoutException(args, Options.Timeout);
            throw;
        }
        catch
        {
            Kill(process);
            await StandardErrorSoFarAsync(stderrTask).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// A cleanup runner allows only <see cref="CleanupPolicy"/>, and a changes runner only <see cref="ChangesPolicy"/>,
    /// reads included, so neither can ever be used for anything else. A read-only runner allows only
    /// <see cref="ReadOnlyPolicy"/>.
    /// </summary>
    private void CheckPolicy(GitCommandLine command)
    {
        var args = command.Arguments;
        if (Options.CleanupOnly)
            CheckCleanup(command);
        else if (Options.ChangesOnly)
            CheckChange(args);
        else if (Options.ReadOnly)
            CheckRead(args);
    }

    private static void CheckCleanup(GitCommandLine command)
    {
        if (!CleanupPolicy.IsAllowed(command, out var reason))
            throw new CleanupPolicyViolationException(command.Arguments, reason);
    }

    private static void CheckChange(IReadOnlyList<string> args)
    {
        if (!ChangesPolicy.IsAllowed(args, out var reason))
            throw new ChangesPolicyViolationException(args, reason);
    }

    private static void CheckRead(IReadOnlyList<string> args)
    {
        if (!ReadOnlyPolicy.IsAllowed(args, out var reason))
            throw new ReadOnlyViolationException(args, reason);
    }

    /// <summary>Writes the input, then closes standard input so git sees the end.</summary>
    private static async Task WriteInputAsync(Stream stdin, byte[] input)
    {
        try
        {
            if (input.Length > 0)
                await stdin.WriteAsync(input).ConfigureAwait(false);
            await stdin.FlushAsync().ConfigureAwait(false);
            stdin.Close();
        }
        catch (IOException)
        {
            // Git stopped reading: it ended, or was stopped. Its exit code, or the caller, says why.
        }
    }

    /// <summary>Who sees the commands run: only for changes, see <see cref="GitRunnerOptions.OnOutput"/>.</summary>
    private Action<GitOutputLine>? Watcher => Options.ChangesOnly || Options.CleanupOnly ? Options.OnOutput : null;

    /// <summary>All of standard error, and each line passed on as it arrives when someone is watching.</summary>
    private async Task<string> ReadErrorAsync(StreamReader stderr, CancellationToken ct)
    {
        if (Watcher is not { } onOutput)
            return await stderr.ReadToEndAsync(ct).ConfigureAwait(false);

        var all = new StringBuilder();
        var splitter = new GitOutputSplitter();
        var buffer = new char[4096];
        int read;
        while ((read = await stderr.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0)
        {
            all.Append(buffer, 0, read);
            foreach (var line in splitter.Feed(buffer.AsSpan(0, read)))
                onOutput(line);
        }
        foreach (var line in splitter.Flush())
            onOutput(line);
        return all.ToString();
    }

    /// <summary>A non-zero exit code that the caller did not allow. A stopped process has none and never failed.</summary>
    private static bool Failed(int? exitCode, IReadOnlyCollection<int> allowedExitCodes) =>
        exitCode is { } code && code != 0 && !allowedExitCodes.Contains(code);

    private static Task<string> StandardErrorSoFarAsync(Task<string> stderrTask) =>
        stderrTask.ContinueWith(t => t.IsCompletedSuccessfully ? t.Result : "", CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    /// <summary>
    /// Starts git. When it cannot be started and the locator now finds git somewhere else, as after an update of Git
    /// for Windows, that git is started instead.
    /// </summary>
    private Process Start(IReadOnlyList<string> args)
    {
        var installation = Options.Installation;
        try
        {
            return Start(args, installation);
        }
        catch (Win32Exception) when (Options.Locator is { } locator)
        {
            var found = locator.FindAgain();
            if (found == installation)
                throw;
            return Start(args, found);
        }
    }

    private Process Start(IReadOnlyList<string> args, GitInstallation installation)
    {
        var process = new Process { StartInfo = CreateStartInfo(args, installation) };
        try
        {
            process.Start();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private ProcessStartInfo CreateStartInfo(IReadOnlyList<string> args, GitInstallation installation)
    {
        var psi = new ProcessStartInfo(installation.Executable)
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
        if (installation.PathPrefix is { } prefix)
            psi.Environment["PATH"] = psi.Environment.TryGetValue("PATH", out var path) && !string.IsNullOrEmpty(path) ? prefix + System.IO.Path.PathSeparator + path : prefix;

        // Set last so options can never turn them off.
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";
        // A path such as "*.txt" or ":x" is that file and nothing else, never a pattern that stages other files.
        // Not for stash, which takes no paths: with it, `stash push --include-untracked` stashes new files but
        // leaves them on disk, since git's own clean step inside it passes ":/" as a pattern.
        if (Options.ChangesOnly && args[0] != "stash")
            psi.Environment["GIT_LITERAL_PATHSPECS"] = "1";
        // Same as `-c core.longpaths=true`: git for Windows can then reach paths over 260 characters,
        // whatever the user's config says. It changes how paths are opened, never what is written.
        // And `-c core.fsmonitor=false`: with fsmonitor on, even `git status` starts a background
        // daemon that writes into .git and keeps running. It makes no difference to the output.
        psi.Environment["GIT_CONFIG_COUNT"] = "2";
        psi.Environment["GIT_CONFIG_KEY_0"] = "core.longpaths";
        psi.Environment["GIT_CONFIG_VALUE_0"] = "true";
        psi.Environment["GIT_CONFIG_KEY_1"] = "core.fsmonitor";
        psi.Environment["GIT_CONFIG_VALUE_1"] = "false";
        return psi;
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
