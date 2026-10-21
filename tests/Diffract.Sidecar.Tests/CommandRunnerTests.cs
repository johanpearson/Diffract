namespace Diffract.Sidecar.Tests;

/// <summary>The runner for programs other than git. It runs git and Windows PowerShell here, never winget.</summary>
public sealed class CommandRunnerTests
{
    private static readonly string PowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    private static readonly CommandRunner Runner = new(TimeProvider.System);

    [Fact]
    public async Task Captures_the_output_and_exit_code()
    {
        var result = await Runner.RunAsync("git", ["version"], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.StartsWith("git version ", result.StandardOutput);
    }

    [Fact]
    public async Task A_failing_program_returns_its_exit_code()
    {
        var result = await Runner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "exit 7"], TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
    }

    [Fact]
    public async Task A_program_that_runs_too_long_times_out()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() =>
            Runner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 30"], TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken));

        Assert.Contains("powershell.exe", ex.Message);
    }

    [Fact]
    public async Task Cancelling_stops_the_program()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(500));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Runner.RunAsync(PowerShell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep 30"], TimeSpan.FromSeconds(30), cts.Token));
    }
}
