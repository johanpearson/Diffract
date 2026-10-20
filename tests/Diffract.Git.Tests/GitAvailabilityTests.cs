using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Diffract.Git.Tests;

public partial class GitAvailabilityTests
{
    // Guards the test machine itself: the fixture tests need git 2.40 or newer on PATH.
    [Fact]
    public async Task Git_on_path_is_at_least_2_40()
    {
        var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("--version");
        using var process = Process.Start(psi)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        var match = VersionPattern().Match(output);
        Assert.True(match.Success, $"Unexpected output from git --version: {output}");
        var version = new Version(int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value));
        Assert.True(version >= new Version(2, 40), $"git {version} is older than 2.40");
    }

    [GeneratedRegex(@"git version (\d+)\.(\d+)")]
    private static partial Regex VersionPattern();
}
