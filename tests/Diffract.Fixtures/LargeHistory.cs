using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Diffract.Fixtures;

/// <param name="Commits">All commits, merges included.</param>
/// <param name="Branches">Local branches at the end, main included.</param>
public sealed record LargeHistoryRepo(string Path, int Commits, int Merges, int Branches, int Tags);

/// <summary>
/// A repo with a long, branchy history for history performance tests, written in one go with <c>git fast-import</c>.
/// Making 50,000 commits one by one would take hours on Windows; fast-import takes seconds. The same seed gives
/// the same history, commit ids included.
/// </summary>
public static class LargeHistory
{
    private static readonly string[] Authors =
        ["Johan Pearson", "Åsa Öberg", "GitHub Copilot", "Claude", "Zoë Ångström", "李雷"];

    private static readonly string[] Subjects =
        ["Fix flaky login test", "Add retry policy", "Refactor cart pricing", "Bump dependencies", "Räkna moms rätt",
         "Update README", "Extract service", "WIP", "Rename helpers", "Handle empty response"];

    /// <param name="commits">How many commits, merges included.</param>
    /// <param name="maxBranches">At most this many feature branches are open at once.</param>
    public static async Task<LargeHistoryRepo> BuildAsync(TempDirectory temp, string name, int commits, int seed = 1, int maxBranches = 6)
    {
        var repo = await FixtureRepo.InitAsync(temp, name);
        var (stream, stats) = Generate(commits, seed, maxBranches);
        await FastImportAsync(temp, repo.Path, stream);
        // fast-import writes refs and objects only. Check main out, so the repo looks like any other.
        await repo.GitAsync("reset", "-q", "--hard", "main");
        return stats with { Path = repo.Path };
    }

    /// <summary>The fast-import stream: main with feature branches that start from it, grow, and merge back.</summary>
    private static (byte[] Stream, LargeHistoryRepo Stats) Generate(int commits, int seed, int maxBranches)
    {
        var random = new Random(seed);
        var output = new MemoryStream();
        var start = new DateTimeOffset(2024, 1, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var tips = new Dictionary<string, int>();   // branch to its newest commit's mark
        var open = new List<string>();
        int merges = 0, tags = 0, mainCommits = 0, nextBranch = 1;

        for (var mark = 1; mark <= commits; mark++)
        {
            var roll = random.NextDouble();
            string branch;
            int? parent, merged = null;
            string? mergedBranch = null;

            if (mark == 1 || roll < 0.3)
            {
                branch = "main";
            }
            else if (roll < 0.42 && open.Count < maxBranches)
            {
                branch = $"feature/{nextBranch++:D5}";
                open.Add(branch);
                tips[branch] = tips["main"];
            }
            else if (roll < 0.52 && open.Count > 0)
            {
                var done = open[random.Next(open.Count)];
                open.Remove(done);
                branch = "main";
                merged = tips[done];
                mergedBranch = done;
                tips.Remove(done);
                merges++;
            }
            else
            {
                branch = open.Count > 0 ? open[random.Next(open.Count)] : "main";
            }

            parent = tips.TryGetValue(branch, out var tip) ? tip : null;
            var author = Authors[random.Next(Authors.Length)];
            var subject = merged is null ? $"{Subjects[random.Next(Subjects.Length)]} ({mark})" : $"Merge {mergedBranch} ({mark})";
            var when = start + mark * 60L;

            Write(output, $"commit refs/heads/{branch}\nmark :{mark}\n");
            Write(output, $"author {author} <{Email(author)}> {when} +0000\ncommitter {author} <{Email(author)}> {when} +0000\n");
            Data(output, $"{subject}\n");
            if (parent is { } p)
                Write(output, $"from :{p}\n");
            if (merged is { } m)
                Write(output, $"merge :{m}\n");
            Write(output, "M 644 inline history.txt\n");
            Data(output, $"{mark}\n");
            Write(output, "\n");
            tips[branch] = mark;

            // A merged branch is deleted, as most people do after merging. A null "from" deletes the ref.
            if (merged is not null)
                Write(output, $"reset refs/heads/{mergedBranch}\nfrom 0000000000000000000000000000000000000000\n\n");

            // A release tag on every 1,000th commit on main.
            if (branch == "main" && ++mainCommits % 1000 == 0)
            {
                tags++;
                Write(output, $"reset refs/tags/v{tags}.0.0\nfrom :{mark}\n\n");
            }
        }

        Write(output, "done\n");
        return (output.ToArray(), new LargeHistoryRepo("", commits, merges, open.Count + 1, tags));
    }

    /// <summary>The author's ASCII letters, so the address stays plain whatever the name.</summary>
    private static string Email(string author)
    {
        var local = new string([.. author.ToLowerInvariant().Where(char.IsAsciiLetter)]);
        return (local.Length > 0 ? local : "dev") + "@example.com";
    }

    private static void Write(MemoryStream output, string text) => output.Write(Encoding.UTF8.GetBytes(text));

    /// <summary>fast-import's <c>data</c> counts bytes, not characters.</summary>
    private static void Data(MemoryStream output, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        Write(output, $"data {bytes.Length.ToString(CultureInfo.InvariantCulture)}\n");
        output.Write(bytes);
    }

    /// <summary>
    /// <c>GitRunner</c> closes stdin, and fast-import reads its stream from there, so this starts git itself.
    /// Like <see cref="IsolatedGit.WritableRunner"/>, it refuses any folder outside the test's temp folder.
    /// </summary>
    private static async Task FastImportAsync(TempDirectory temp, string repoPath, byte[] stream)
    {
        // Throws for a folder outside the temp folder, before anything is written.
        IsolatedGit.WritableRunner(temp, repoPath);

        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = repoPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in new[] { "fast-import", "--quiet", "--done" })
            psi.ArgumentList.Add(arg);
        foreach (var (key, value) in IsolatedGit.Environment(temp))
            psi.Environment[key] = value;

        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.StandardInput.BaseStream.WriteAsync(stream);
        process.StandardInput.Close();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git fast-import failed with exit code {process.ExitCode}: {await stderr}");
        await stdout;
    }
}
