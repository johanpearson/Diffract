namespace Diffract.Git;

/// <summary>
/// Decides whether a git command line is safe to run against a real repo.
/// Anything not explicitly recognised as a read is rejected.
/// </summary>
public static class ReadOnlyPolicy
{
    private static readonly HashSet<string> AnyArguments =
    [
        "version", "rev-parse", "status", "for-each-ref", "rev-list",
        "merge-base", "log", "fsck", "cat-file", "show-ref",
    ];

    private static readonly HashSet<string> ConfigReadOptions =
    [
        "--get", "--get-all", "--get-regexp", "--get-urlmatch", "--list", "-l",
    ];

    private static readonly HashSet<string> ConfigWriteOptions =
    [
        "--add", "--replace-all", "--unset", "--unset-all", "--edit", "-e",
        "--rename-section", "--remove-section",
    ];

    private static readonly HashSet<string> SymbolicRefReadOptions =
    [
        "-q", "--quiet", "--short", "--recurse", "--no-recurse",
    ];

    public static bool IsAllowed(IReadOnlyList<string> args, out string reason)
    {
        reason = Check(args) ?? "";
        return reason.Length == 0;
    }

    private static string? Check(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
            return "no subcommand";

        var subcommand = args[0];
        if (subcommand.StartsWith('-'))
            return "options before the subcommand are not allowed";

        // `log --output=<file>` writes a file.
        if (args.Any(a => a == "--output" || a.StartsWith("--output=", StringComparison.Ordinal)))
            return "--output writes a file";

        if (subcommand == "fsck" && args.Contains("--lost-found"))
            return "fsck --lost-found writes to .git/lost-found";

        if (AnyArguments.Contains(subcommand))
            return null;

        return subcommand switch
        {
            "worktree" or "stash" => args.Count > 1 && args[1] == "list" ? null : $"only '{subcommand} list' is allowed",
            "reflog" => CheckReflog(args),
            "config" => CheckConfig(args),
            "symbolic-ref" => CheckSymbolicRef(args),
            _ => $"'{subcommand}' is not on the read-only allowlist",
        };
    }

    // `git reflog` with no subcommand, or followed by an option, means `reflog show`.
    private static string? CheckReflog(IReadOnlyList<string> args) =>
        args.Count == 1 || args[1] == "show" || args[1].StartsWith('-') ? null : "only 'reflog show' is allowed";

    private static string? CheckConfig(IReadOnlyList<string> args)
    {
        if (args.Count > 1 && !args[1].StartsWith('-'))
            return args[1] is "get" or "list" ? null : "only 'config get' and 'config list' are allowed";

        if (args.Any(ConfigWriteOptions.Contains))
            return "config write options are not allowed";

        return args.Any(ConfigReadOptions.Contains) ? null : "config needs --get or --list";
    }

    // One name reads the ref. A second name, -d or -m changes it.
    private static string? CheckSymbolicRef(IReadOnlyList<string> args)
    {
        var rest = args.Skip(1).ToList();
        if (rest.Any(a => a.StartsWith('-') && !SymbolicRefReadOptions.Contains(a)))
            return "only -q, --short and --[no-]recurse are allowed with symbolic-ref";

        return rest.Count(a => !a.StartsWith('-')) == 1 ? null : "symbolic-ref may only read one ref";
    }
}
