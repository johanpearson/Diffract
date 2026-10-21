using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// system.listTerminals and system.openTerminal on a described machine: which terminals are found, and which program
/// starts with which arguments. A fake starter, so no terminal opens.
/// </summary>
public sealed class OpenTerminalTests : IDisposable
{
    private const string WindowsTerminal = @"C:\Users\johan\AppData\Local\Microsoft\WindowsApps\wt.exe";
    private const string WindowsPowerShell = @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe";
    private const string PowerShell7 = @"C:\Program Files\PowerShell\7\pwsh.exe";
    private const string PowerShell7Store = @"C:\Users\johan\AppData\Local\Microsoft\WindowsApps\pwsh.exe";
    private const string CommandPrompt = @"C:\Windows\System32\cmd.exe";
    private const string GitBash = @"C:\Program Files\Git\git-bash.exe";

    private static readonly string[] Everything = [WindowsTerminal, WindowsPowerShell, PowerShell7, CommandPrompt, GitBash, @"C:\Program Files\Git\cmd\git.exe"];

    private readonly TempDirectory _temp = new();
    private readonly FakeProcesses _processes = new();
    private readonly List<SidecarMethods> _created = [];

    public void Dispose()
    {
        foreach (var methods in _created)
            methods.Dispose();
        _temp.Dispose();
    }

    private static MachineLayout Machine(string[] files, string path = @"C:\Program Files\Git\cmd;C:\Windows\System32") =>
        new(@"C:\Windows\System32", @"C:\Users\johan\AppData\Local", @"C:\Program Files", path, f => files.Contains(f, StringComparer.OrdinalIgnoreCase));

    private SidecarMethods Methods(MachineLayout machine)
    {
        var methods = new SidecarMethods(new GitRunnerOptions(), processes: _processes, machine: machine);
        _created.Add(methods);
        return methods;
    }

    private Task<JsonElement> OpenAsync(string path, MachineLayout machine, string? terminal = null) =>
        Methods(machine).Dispatch("system.openTerminal", JsonSerializer.SerializeToElement(new { path, terminal }), TestContext.Current.CancellationToken);

    private async Task<string[]> ListAsync(MachineLayout machine) =>
        [.. (await Methods(machine).Dispatch("system.listTerminals", null, TestContext.Current.CancellationToken))
            .EnumerateArray().Select(t => $"{t.GetProperty("id").GetString()}: {t.GetProperty("name").GetString()}")];

    private string Folder(string name)
    {
        var folder = Path.GetFullPath(_temp.Combine(name));
        Directory.CreateDirectory(folder);
        return folder;
    }

    // --- which terminals are found ------------------------------------------------

    [Fact]
    public async Task Lists_every_installed_terminal_with_windows_terminal_first()
    {
        Assert.Equal(
            ["WindowsTerminal: Windows Terminal", "WindowsPowerShell: Windows PowerShell", "PowerShell7: PowerShell 7", "CommandPrompt: Command Prompt", "GitBash: Git Bash"],
            await ListAsync(Machine(Everything)));
    }

    [Fact]
    public async Task Lists_only_what_is_installed()
    {
        Assert.Equal(["WindowsPowerShell: Windows PowerShell", "CommandPrompt: Command Prompt"], await ListAsync(Machine([WindowsPowerShell, CommandPrompt], path: "")));
    }

    [Fact]
    public async Task Finds_powershell_7_from_the_store_too()
    {
        Assert.Contains("PowerShell7: PowerShell 7", await ListAsync(Machine([PowerShell7Store])));
    }

    [Theory]
    [InlineData(@"C:\Tools\Git\cmd", @"C:\Tools\Git\git-bash.exe")]
    [InlineData(@"C:\Tools\Git\mingw64\bin", @"C:\Tools\Git\git-bash.exe")]
    [InlineData(@"C:\Tools\Git\cmd\", @"C:\Tools\Git\git-bash.exe")]
    public void Finds_git_bash_next_to_the_git_on_path(string gitFolder, string gitBash)
    {
        var machine = Machine([Path.Combine(gitFolder, "git.exe"), gitBash], path: $@"C:\Windows\System32;{gitFolder}");

        Assert.Equal(gitBash, new TerminalLauncher(_processes, machine).Find(TerminalKind.GitBash));
    }

    [Fact]
    public void Finds_git_bash_in_its_default_folder_when_git_is_not_on_path()
    {
        Assert.Equal(GitBash, new TerminalLauncher(_processes, Machine([GitBash], path: "")).Find(TerminalKind.GitBash));
    }

    [Fact]
    public void Does_not_look_for_git_bash_more_than_two_folders_above_git()
    {
        var machine = Machine([@"C:\Tools\a\b\c\git.exe", @"C:\Tools\git-bash.exe"], path: @"C:\Tools\a\b\c");

        Assert.Null(new TerminalLauncher(_processes, machine).Find(TerminalKind.GitBash));
    }

    [Fact]
    public void Ignores_relative_and_empty_path_entries()
    {
        var machine = Machine([@"cmd\git.exe", "git-bash.exe"], path: @";cmd;;");

        Assert.Null(new TerminalLauncher(_processes, machine).Find(TerminalKind.GitBash));
    }

    // --- what starts --------------------------------------------------------------

    [Fact]
    public async Task Opens_windows_terminal_by_default_when_it_is_installed()
    {
        var folder = Folder("räksmörgås fix");

        var result = await OpenAsync(folder, Machine(Everything));

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        var (fileName, arguments, workingDirectory, newConsole) = Assert.Single(_processes.Started);
        Assert.Equal(WindowsTerminal, fileName);
        Assert.Equal(["-d", folder], arguments);
        Assert.Equal((null, false), (workingDirectory, newConsole));
    }

    [Fact]
    public async Task Opens_windows_powershell_by_default_without_windows_terminal()
    {
        var folder = Folder("shop");

        await OpenAsync(folder, Machine([WindowsPowerShell, CommandPrompt]));

        AssertStarted(WindowsPowerShell, ["-NoLogo"], folder, newConsole: true);
    }

    [Theory]
    [InlineData("WindowsPowerShell", WindowsPowerShell, new[] { "-NoLogo" }, true)]
    [InlineData("PowerShell7", PowerShell7, new[] { "-NoLogo" }, true)]
    [InlineData("CommandPrompt", CommandPrompt, new string[0], true)]
    public async Task Opens_the_chosen_console_in_its_own_window_in_the_folder(string terminal, string program, string[] arguments, bool newConsole)
    {
        var folder = Folder("shop");

        await OpenAsync(folder, Machine(Everything), terminal);

        AssertStarted(program, arguments, folder, newConsole);
    }

    [Fact]
    public async Task Opens_git_bash_in_the_folder_the_way_git_bash_here_does()
    {
        var folder = Folder("räksmörgås fix");

        await OpenAsync(folder, Machine(Everything), "GitBash");

        AssertStarted(GitBash, [$"--cd={folder}"], folder, newConsole: false);
    }

    [Fact]
    public async Task Never_passes_cmd_a_command()
    {
        await OpenAsync(Folder("shop"), Machine(Everything), "CommandPrompt");

        Assert.Empty(Assert.Single(_processes.Started).Arguments);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("WindowsTerminal")]
    public async Task A_folder_with_a_semicolon_gets_powershell_because_windows_terminal_would_split_the_command(string? terminal)
    {
        var folder = Folder("a;b");

        await OpenAsync(folder, Machine(Everything), terminal);

        AssertStarted(WindowsPowerShell, ["-NoLogo"], folder, newConsole: true);
    }

    [Fact]
    public async Task A_trailing_separator_is_dropped_so_windows_terminal_gets_a_clean_path()
    {
        var folder = Folder("shop");

        await OpenAsync(folder + Path.DirectorySeparatorChar, Machine(Everything));

        Assert.Equal(["-d", folder], Assert.Single(_processes.Started).Arguments);
    }

    // --- what is refused ----------------------------------------------------------

    [Fact]
    public async Task A_terminal_that_is_not_installed_is_refused_with_its_name()
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => OpenAsync(Folder("shop"), Machine([WindowsPowerShell]), "GitBash"));

        Assert.Equal((RpcErrorCodes.InvalidParams, "Git Bash is not installed."), (error.Code, error.Message));
        Assert.Empty(_processes.Started);
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("cmd /c calc")]
    [InlineData("")]
    public async Task An_unknown_terminal_is_refused(string terminal)
    {
        var error = await Assert.ThrowsAsync<RpcException>(() => OpenAsync(Folder("shop"), Machine(Everything), terminal));

        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
        Assert.Empty(_processes.Started);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("a-file.txt")]
    [InlineData(@"relative\folder")]
    [InlineData("powershell.exe")]
    public async Task Refuses_anything_that_is_not_an_existing_folder(string path)
    {
        File.WriteAllText(_temp.Combine("a-file.txt"), "not a folder");
        var full = path.Contains('\\') || path.EndsWith(".exe") ? path : _temp.Combine(path);

        var error = await Assert.ThrowsAsync<RpcException>(() => OpenAsync(full, Machine(Everything)));

        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
        Assert.Empty(_processes.Started);
    }

    // --- this machine -------------------------------------------------------------

    [Fact]
    public void On_any_windows_machine_windows_powershell_and_the_command_prompt_are_found()
    {
        var found = new TerminalLauncher(_processes).List().Select(t => t.Id);

        Assert.Contains(TerminalKind.WindowsPowerShell, found);
        Assert.Contains(TerminalKind.CommandPrompt, found);
    }

    private void AssertStarted(string program, string[] arguments, string folder, bool newConsole)
    {
        var started = Assert.Single(_processes.Started);
        Assert.Equal(program, started.FileName);
        Assert.Equal(arguments, started.Arguments);
        Assert.Equal((folder, newConsole), (started.WorkingDirectory, started.NewConsole));
    }
}
