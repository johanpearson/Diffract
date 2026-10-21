using System.Text.RegularExpressions;
using Diffract.Git;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// The sidecar never creates a writable <see cref="GitRunner"/>. <see cref="GitRunnerOptions.ReadOnly"/> is true unless
/// something sets it, so it is enough to prove two things: the options the sidecar is given are forced to read-only,
/// and no code in the sidecar sets ReadOnly to anything but true.
/// </summary>
public partial class ReadOnlyTests
{
    [Fact]
    public void The_options_the_sidecar_is_given_are_forced_to_read_only()
    {
        Assert.True(new SidecarMethods(new GitRunnerOptions { ReadOnly = false }).RunnerOptions.ReadOnly);
    }

    [Fact]
    public void Read_only_is_the_default()
    {
        Assert.True(new GitRunnerOptions().ReadOnly);
    }

    [Fact]
    public void No_code_in_the_sidecar_sets_read_only_to_anything_but_true()
    {
        var sources = SidecarSources();

        var offenders = sources
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, number: i + 1)))
            .Where(l => SetsReadOnlyToSomethingElse(l.line))
            .Select(l => $"{Path.GetFileName(l.file)}:{l.number}: {l.line.Trim()}");

        Assert.Contains(sources, f => f.EndsWith("SidecarMethods.cs", StringComparison.Ordinal));
        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("new GitRunnerOptions { ReadOnly = false }", true)]
    [InlineData("options with { ReadOnly = allowWrites }", true)]
    [InlineData("new GitRunnerOptions { ReadOnly = !safe }", true)]
    [InlineData("Create(ReadOnly: false)", true)]
    [InlineData("runnerOptions with { ReadOnly = true }", false)]
    [InlineData("if (options.ReadOnly == false)", false)]
    [InlineData("var x = options.ReadOnly;", false)]
    public void The_scan_catches_every_way_of_setting_it(string line, bool caught)
    {
        Assert.Equal(caught, SetsReadOnlyToSomethingElse(line));
    }

    private static bool SetsReadOnlyToSomethingElse(string line) => ReadOnlyAssignment().IsMatch(line);

    private static List<string> SidecarSources()
    {
        var project = Path.Combine(RepoRoot.Find(), "sidecar", "Diffract.Sidecar");
        var separator = Path.DirectorySeparatorChar;
        return [.. Directory.EnumerateFiles(project, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{separator}obj{separator}") && !f.Contains($"{separator}bin{separator}"))];
    }

    // "ReadOnly =" or "ReadOnly:" followed by anything except "true", but not "ReadOnly ==".
    // The spaces are matched atomically, or the regex would give one back and see " true" as not "true".
    [GeneratedRegex(@"\bReadOnly\s*(=(?!=)|:)(?>\s*)(?!true\b)")]
    private static partial Regex ReadOnlyAssignment();
}
