using System.Text.RegularExpressions;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// Only the cleanup executor creates a cleanup runner, which allows nothing but CleanupPolicy (see CleanupPolicyTests).
/// The sidecar hands it read-only options, so no other code in the sidecar or its libraries may turn CleanupOnly on.
/// </summary>
public partial class CleanupRunnerTests
{
    [Fact]
    public void Only_the_cleanup_executor_creates_a_cleanup_runner()
    {
        var root = Path.Combine(RepoRoot.Find(), "sidecar");
        var separator = Path.DirectorySeparatorChar;

        var setters = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"))
            .Where(f => File.ReadLines(f).Any(TurnsOn))
            .Select(f => Path.GetRelativePath(root, f).Replace(separator, '/'));

        Assert.Equal(["Diffract.Leftovers/Cleanup/StepRunner.cs"], setters);
    }

    [Theory]
    [InlineData("git with { CleanupOnly = true }", true)]
    [InlineData("new GitRunnerOptions { CleanupOnly = allow }", true)]
    [InlineData("Create(CleanupOnly: true)", true)]
    [InlineData("git with { ReadOnly = true, ChangesOnly = false, CleanupOnly = false }", false)]
    [InlineData("if (Options.CleanupOnly)", false)]
    [InlineData("if (Options.CleanupOnly == true)", false)]
    public void The_scan_catches_every_way_of_turning_it_on(string line, bool caught)
    {
        Assert.Equal(caught, TurnsOn(line));
    }

    private static bool TurnsOn(string line) => CleanupOnlyAssignment().IsMatch(line);

    // "CleanupOnly =" or "CleanupOnly:" followed by anything except "false", but not "CleanupOnly ==".
    [GeneratedRegex(@"\bCleanupOnly\s*(=(?!=)|:)(?>\s*)(?!false\b)")]
    private static partial Regex CleanupOnlyAssignment();
}
