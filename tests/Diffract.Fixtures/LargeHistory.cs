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

    /// <summary>At most this many feature branches are open at once.</summary>
    private const int MaxOpenBranches = 6;

    /// <param name="commits">How many commits, merges included.</param>
    public static async Task<LargeHistoryRepo> BuildAsync(TempDirectory temp, string name, int commits, int seed = 1)
    {
        var repo = await FixtureRepo.InitAsync(temp, name);
        var history = new HistoryWriter(seed);
        for (var mark = 1; mark <= commits; mark++)
            history.AddCommit(mark);
        await FastImportAsync(temp, repo.Path, history.Finish());
        // fast-import writes refs and objects only. Check main out, so the repo looks like any other.
        await repo.GitAsync("reset", "-q", "--hard", "main");
        return history.Stats(commits) with { Path = repo.Path };
    }

    /// <summary>The author's ASCII letters, so the address stays plain whatever the name.</summary>
    private static string Email(string author)
    {
        var local = new string([.. author.ToLowerInvariant().Where(char.IsAsciiLetter)]);
        return (local.Length > 0 ? local : "dev") + "@example.com";
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

    /// <param name="Merged">The mark of the branch tip a merge commit merges, or null for a plain commit.</param>
    private sealed record Step(string Branch, int? Merged = null, string? MergedBranch = null);

    /// <summary>The fast-import stream: main with feature branches that start from it, grow, and merge back.</summary>
    private sealed class HistoryWriter(int seed)
    {
        private static readonly long Start = new DateTimeOffset(2024, 1, 1, 8, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        private readonly Random _random = new(seed);
        private readonly MemoryStream _output = new();
        private readonly Dictionary<string, int> _tips = [];   // branch to its newest commit's mark
        private readonly List<string> _open = [];
        private int _merges;
        private int _tags;
        private int _mainCommits;
        private int _nextBranch = 1;

        public void AddCommit(int mark)
        {
            var step = ChooseStep(mark);
            WriteCommit(mark, step);
            _tips[step.Branch] = mark;

            // A merged branch is deleted, as most people do after merging. A null "from" deletes the ref.
            if (step.Merged is not null)
                Write($"reset refs/heads/{step.MergedBranch}\nfrom 0000000000000000000000000000000000000000\n\n");

            // A release tag on every 1,000th commit on main.
            if (step.Branch == "main" && ++_mainCommits % 1000 == 0)
            {
                _tags++;
                Write($"reset refs/tags/v{_tags}.0.0\nfrom :{mark}\n\n");
            }
        }

        public byte[] Finish()
        {
            Write("done\n");
            return _output.ToArray();
        }

        public LargeHistoryRepo Stats(int commits) => new("", commits, _merges, _open.Count + 1, _tags);

        private Step ChooseStep(int mark)
        {
            var roll = _random.NextDouble();
            if (mark == 1 || roll < 0.3)
                return new Step("main");
            if (roll < 0.42 && _open.Count < MaxOpenBranches)
                return StartBranch();
            if (roll < 0.52 && _open.Count > 0)
                return MergeBranch();
            return new Step(_open.Count > 0 ? _open[_random.Next(_open.Count)] : "main");
        }

        private Step StartBranch()
        {
            var branch = $"feature/{_nextBranch++:D5}";
            _open.Add(branch);
            _tips[branch] = _tips["main"];
            return new Step(branch);
        }

        private Step MergeBranch()
        {
            var done = _open[_random.Next(_open.Count)];
            _open.Remove(done);
            var merged = _tips[done];
            _tips.Remove(done);
            _merges++;
            return new Step("main", merged, done);
        }

        private void WriteCommit(int mark, Step step)
        {
            int? parent = _tips.TryGetValue(step.Branch, out var tip) ? tip : null;
            var author = Authors[_random.Next(Authors.Length)];
            var subject = step.Merged is null ? $"{Subjects[_random.Next(Subjects.Length)]} ({mark})" : $"Merge {step.MergedBranch} ({mark})";
            var when = Start + mark * 60L;

            Write($"commit refs/heads/{step.Branch}\nmark :{mark}\n");
            Write($"author {author} <{Email(author)}> {when} +0000\ncommitter {author} <{Email(author)}> {when} +0000\n");
            Data($"{subject}\n");
            if (parent is { } p)
                Write($"from :{p}\n");
            if (step.Merged is { } m)
                Write($"merge :{m}\n");
            Write("M 644 inline history.txt\n");
            Data($"{mark}\n");
            Write("\n");
        }

        private void Write(string text) => _output.Write(Encoding.UTF8.GetBytes(text));

        /// <summary>fast-import's <c>data</c> counts bytes, not characters.</summary>
        private void Data(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            Write($"data {bytes.Length.ToString(CultureInfo.InvariantCulture)}\n");
            _output.Write(bytes);
        }
    }
}
