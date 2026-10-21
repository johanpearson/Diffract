using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>system.openTerminal: which program starts, with which arguments. A fake starter, so no terminal opens.</summary>
public sealed class OpenTerminalTests : IDisposable
{
    private const string WindowsTerminal = @"C:\Users\johan\AppData\Local\Microsoft\WindowsApps\wt.exe";

    private readonly TempDirectory _temp = new();
    private readonly FakeProcesses _processes = new();
    private readonly List<SidecarMethods> _created = [];

    public void Dispose()
    {
        foreach (var methods in _created)
            methods.Dispose();
        _temp.Dispose();
    }

    private Task<JsonElement> OpenAsync(string path, string? windowsTerminal)
    {
        var methods = new SidecarMethods(new GitRunnerOptions(), processes: _processes, findWindowsTerminal: () => windowsTerminal);
        _created.Add(methods);
        return methods.Dispatch("system.openTerminal", JsonSerializer.SerializeToElement(new { path }), TestContext.Current.CancellationToken);
    }

    private string Folder(string name)
    {
        var folder = Path.GetFullPath(_temp.Combine(name));
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public async Task Opens_windows_terminal_in_the_folder_when_it_is_installed()
    {
        var folder = Folder("räksmörgås fix");

        var result = await OpenAsync(folder, WindowsTerminal);

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        var (fileName, arguments, workingDirectory, newConsole) = Assert.Single(_processes.Started);
        Assert.Equal(WindowsTerminal, fileName);
        Assert.Equal(["-d", folder], arguments);
        Assert.Equal((null, false), (workingDirectory, newConsole));
    }

    [Fact]
    public async Task Falls_back_to_powershell_in_a_window_of_its_own()
    {
        var folder = Folder("shop");

        await OpenAsync(folder, windowsTerminal: null);

        var (fileName, arguments, workingDirectory, newConsole) = Assert.Single(_processes.Started);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"), fileName);
        Assert.Equal(["-NoLogo"], arguments);
        Assert.Equal((folder, true), (workingDirectory, newConsole));
    }

    [Fact]
    public async Task A_folder_with_a_semicolon_gets_powershell_because_windows_terminal_would_split_the_command()
    {
        var folder = Folder("a;b");

        await OpenAsync(folder, WindowsTerminal);

        var started = Assert.Single(_processes.Started);
        Assert.EndsWith("powershell.exe", started.FileName);
        Assert.Equal(folder, started.WorkingDirectory);
    }

    [Fact]
    public async Task A_trailing_separator_is_dropped_so_windows_terminal_gets_a_clean_path()
    {
        var folder = Folder("shop");

        await OpenAsync(folder + Path.DirectorySeparatorChar, WindowsTerminal);

        Assert.Equal(["-d", folder], Assert.Single(_processes.Started).Arguments);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("a-file.txt")]
    [InlineData(@"relative\folder")]
    [InlineData("powershell.exe")]
    public async Task Refuses_anything_that_is_not_an_existing_folder(string path)
    {
        File.WriteAllText(_temp.Combine("a-file.txt"), "not a folder");
        var full = Path.IsPathRooted(path) || path.Contains('\\') || path.EndsWith(".exe") ? path : _temp.Combine(path);

        var error = await Assert.ThrowsAsync<RpcException>(() => OpenAsync(full, WindowsTerminal));

        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
        Assert.Empty(_processes.Started);
    }

    [Fact]
    public void Windows_powershell_is_found_by_its_full_path()
    {
        Assert.True(Path.IsPathFullyQualified(TerminalLauncher.PowerShell));
        Assert.True(File.Exists(TerminalLauncher.PowerShell), TerminalLauncher.PowerShell);
    }

    [Fact]
    public void Windows_terminal_is_looked_for_as_an_app_execution_alias()
    {
        var found = TerminalLauncher.FindWindowsTerminal();

        // CI may not have Windows Terminal. When there is one, it is the alias in WindowsApps.
        if (found is not null)
            Assert.EndsWith(@"\Microsoft\WindowsApps\wt.exe", found, StringComparison.OrdinalIgnoreCase);
    }
}
