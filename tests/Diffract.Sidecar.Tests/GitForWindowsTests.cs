using System.Diagnostics;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// Which programs are using Git for Windows, from a process list the test describes. The shapes come from Johan's
/// laptop on 2026-10-21, when CodeScene's git processes stopped the installer (see docs/findings/winget.md).
/// </summary>
public sealed class GitForWindowsTests
{
    private const string Git = @"C:\Program Files\Git";
    private const int Self = 900;

    private static FakeProcessTable Table(params ProcessInfo[] processes)
    {
        var table = new FakeProcessTable().Add(processes);
        table.Descriptions["Code.exe"] = "Visual Studio Code";
        table.Descriptions["cs-ide.exe"] = "CodeScene cs-ide tool for windows";
        table.Descriptions["explorer.exe"] = "Windows Explorer";
        return table;
    }

    private static readonly ProcessInfo Explorer = new(10, 4, @"C:\Windows\explorer.exe");
    private static readonly ProcessInfo Code = new(20, 10, @"C:\Users\johan\AppData\Local\Programs\Microsoft VS Code\Code.exe");
    private static readonly ProcessInfo CodeHost = new(21, 20, @"C:\Users\johan\AppData\Local\Programs\Microsoft VS Code\Code.exe");
    private static readonly ProcessInfo CodeScene = new(30, 21, @"C:\Users\johan\.vscode\extensions\codescene\cs-ide.exe");

    private static IReadOnlyList<GitInUse> Users(FakeProcessTable table) => GitForWindows.Users(table, Git, Self);

    [Fact]
    public void An_editor_extensions_long_running_git_is_named_with_the_editor()
    {
        // cs-ide starts "git check-ignore --stdin" through git's cmd wrapper, which starts the real git.exe.
        var table = Table(Explorer, Code, CodeHost, CodeScene,
            new(31, 30, Git + @"\cmd\git.exe"),
            new(32, 31, Git + @"\mingw64\bin\git.exe"));

        Assert.Equal([new GitInUse("CodeScene cs-ide tool for windows", "Visual Studio Code", 2)], Users(table));
    }

    [Fact]
    public void A_Git_Bash_window_is_called_Git_Bash()
    {
        var table = Table(Explorer,
            new(40, 10, Git + @"\git-bash.exe"),
            new(41, 40, Git + @"\usr\bin\mintty.exe"),
            new(42, 41, Git + @"\usr\bin\bash.exe"));

        Assert.Equal([new GitInUse("Git Bash", null, 3)], Users(table));
    }

    [Fact]
    public void An_agents_script_is_named_with_the_program_that_runs_it()
    {
        var node = new ProcessInfo(50, 21, @"C:\Program Files\nodejs\node.exe");
        var table = Table(Explorer, Code, CodeHost, node,
            new(51, 50, Git + @"\bin\bash.exe"),
            new(52, 51, Git + @"\usr\bin\bash.exe"));
        table.Descriptions["node.exe"] = "Node.js JavaScript Runtime";

        Assert.Equal([new GitInUse("Node.js JavaScript Runtime", "Visual Studio Code", 2)], Users(table));
    }

    [Fact]
    public void Diffracts_own_git_calls_do_not_count()
    {
        var sidecar = new ProcessInfo(Self, 60, @"C:\Users\johan\AppData\Local\Diffract\diffract-sidecar.exe");
        var table = Table(Explorer, sidecar,
            new(61, Self, Git + @"\cmd\git.exe"),
            new(62, 61, Git + @"\mingw64\bin\git.exe"));

        Assert.Empty(Users(table));
    }

    [Fact]
    public void Programs_outside_the_Git_folder_do_not_count_even_with_a_similar_name()
    {
        var table = Table(Explorer, Code,
            new(70, 20, @"C:\Program Files\GitHub CLI\gh.exe"),
            new(71, 20, @"C:\Program Files\Git LFS\git-lfs.exe"),
            new(72, 20, null));

        Assert.Empty(Users(table));
    }

    [Fact]
    public void A_git_process_whose_parent_is_gone_goes_by_its_own_name()
    {
        var table = Table(new ProcessInfo(80, 12345, Git + @"\usr\bin\ssh-agent.exe"));

        Assert.Equal([new GitInUse("ssh-agent", null, 1)], Users(table));
    }

    [Fact]
    public void Reused_process_ids_cannot_make_it_loop()
    {
        var table = Table(
            new ProcessInfo(90, 91, Git + @"\usr\bin\bash.exe"),
            new ProcessInfo(91, 90, Git + @"\usr\bin\bash.exe"),
            new ProcessInfo(92, 92, Git + @"\usr\bin\sleep.exe"));

        Assert.Equal(3, Users(table).Sum(u => u.Processes));
    }

    [Fact]
    public void Each_program_is_listed_once_in_name_order()
    {
        var table = Table(Explorer, Code, CodeHost, CodeScene,
            new(31, 30, Git + @"\cmd\git.exe"),
            new(40, 10, Git + @"\git-bash.exe"),
            new(33, 30, Git + @"\cmd\git.exe"));

        Assert.Equal(["CodeScene cs-ide tool for windows", "Git Bash"], Users(table).Select(u => u.Program));
    }

    [Fact]
    public void Nothing_running_from_git_is_nobody()
    {
        Assert.Empty(Users(Table(Explorer, Code)));
    }

    // --- the real process list, read only --------------------------------------

    [Fact]
    public void The_real_process_list_holds_this_process_with_its_path_and_parent()
    {
        var table = new ProcessTable();

        var self = Assert.Single(table.Snapshot(), p => p.Id == Environment.ProcessId);

        Assert.Equal(Environment.ProcessPath, self.Path, ignoreCase: true);
        Assert.NotEqual(0, self.ParentId);
        Assert.False(string.IsNullOrWhiteSpace(table.Describe(self.Path!)));
    }

    [Fact]
    public void A_git_started_from_here_is_seen_but_left_out_as_ours()
    {
        using var git = Process.Start(new ProcessStartInfo("git", "hash-object --stdin") { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true })!;
        try
        {
            var table = new ProcessTable();
            var seen = Assert.Single(table.Snapshot(), p => p.Id == git.Id);
            Assert.Equal(Environment.ProcessId, seen.ParentId);

            // Only this process and its git, so other programs using git on the machine cannot interfere.
            var folder = Path.GetDirectoryName(Path.GetDirectoryName(seen.Path!))!;
            var ours = new FakeProcessTable().Add([.. table.Snapshot().Where(p => p.Id == git.Id || p.ParentId == git.Id || p.Id == Environment.ProcessId)]);
            Assert.Empty(GitForWindows.Users(ours, folder, Environment.ProcessId));
            Assert.NotEmpty(GitForWindows.Users(ours, folder, selfId: -1));
        }
        finally
        {
            git.StandardInput.Close();
            git.WaitForExit();
        }
    }

    // --- where Git for Windows is installed ---------------------------------------

    [Theory]
    [InlineData(@"C:\Tools\Git\cmd")]
    [InlineData(@"C:\Tools\Git\mingw64\bin")]
    public void Finds_the_install_folder_from_the_git_on_PATH(string pathEntry)
    {
        string[] files = [Path.Combine(pathEntry, "git.exe"), @"C:\Tools\Git\git-bash.exe"];
        var machine = new MachineLayout(@"C:\Windows\System32", @"C:\Users\johan\AppData\Local", @"C:\Program Files", pathEntry + @";C:\Windows",
            f => files.Contains(f, StringComparer.OrdinalIgnoreCase));

        Assert.Equal(@"C:\Tools\Git", GitForWindows.FindFolder(machine));
    }

    [Fact]
    public void Falls_back_to_the_default_install_folder_and_otherwise_finds_none()
    {
        var installed = new MachineLayout(@"C:\Windows\System32", @"C:\Users\johan\AppData\Local", @"C:\Program Files", null, f => f == @"C:\Program Files\Git\git-bash.exe");
        var missing = installed with { FileExists = _ => false };

        Assert.Equal(@"C:\Program Files\Git", GitForWindows.FindFolder(installed));
        Assert.Null(GitForWindows.FindFolder(missing));
    }
}
