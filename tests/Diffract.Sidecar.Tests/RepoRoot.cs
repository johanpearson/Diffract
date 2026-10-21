namespace Diffract.Sidecar.Tests;

internal static class RepoRoot
{
    /// <summary>The folder that holds Diffract.slnx, found by walking up from the test output folder.</summary>
    public static string Find()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Diffract.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Diffract.slnx not found above the test output folder.");
    }
}
