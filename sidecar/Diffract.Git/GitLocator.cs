namespace Diffract.Git;

/// <summary>
/// The git Diffract starts, found once and found again when it has gone. Updating Git for Windows can move the real
/// git, as 2.56 did from mingw64 to ucrt64, and Diffract keeps running meanwhile. Shared by every copy of the runner
/// options, so one runner finding git again does it for all.
/// </summary>
public sealed class GitLocator(Func<GitInstallation> find)
{
    private GitInstallation _current = find();

    /// <summary>Finds git the way Windows would on this machine.</summary>
    public static GitLocator ForThisMachine() => new(GitInstallation.ForThisMachine);

    /// <summary>The git found last.</summary>
    public GitInstallation Current => Volatile.Read(ref _current);

    /// <summary>Looks for git again, and keeps what it finds.</summary>
    public GitInstallation FindAgain()
    {
        var found = find();
        Volatile.Write(ref _current, found);
        return found;
    }
}
