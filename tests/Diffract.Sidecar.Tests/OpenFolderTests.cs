using System.Text.Json;
using Diffract.Fixtures;
using Diffract.Git;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>system.openFolder, with a fake process starter: no Explorer window opens.</summary>
public sealed class OpenFolderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FakeProcesses _processes = new();
    private readonly SidecarMethods _methods;

    public OpenFolderTests()
    {
        _methods = new SidecarMethods(new GitRunnerOptions(), processes: _processes);
    }

    public void Dispose()
    {
        _methods.Dispose();
        _temp.Dispose();
    }

    private Task<JsonElement> OpenAsync(string path) =>
        _methods.Dispatch("system.openFolder", JsonSerializer.SerializeToElement(new { path }), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Opens_an_existing_folder_in_explorer_from_the_windows_folder()
    {
        var folder = _temp.Combine("räksmörgås fix");
        Directory.CreateDirectory(folder);

        var result = await OpenAsync(folder);

        Assert.Equal(JsonValueKind.Null, result.ValueKind);
        var started = Assert.Single(_processes.Started);
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), started.FileName);
        Assert.Equal([Path.GetFullPath(folder)], started.Arguments);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("a-file.txt")]
    public async Task Refuses_anything_that_is_not_an_existing_folder(string name)
    {
        File.WriteAllText(_temp.Combine("a-file.txt"), "not a folder");

        var error = await Assert.ThrowsAsync<RpcException>(() => OpenAsync(_temp.Combine(name)));

        Assert.Equal(RpcErrorCodes.InvalidParams, error.Code);
        Assert.Empty(_processes.Started);
    }

    [Theory]
    [InlineData(@"relative\folder")]
    [InlineData("calc.exe")]
    [InlineData(@"\\?\")]
    public async Task Refuses_a_path_that_is_not_fully_qualified(string path)
    {
        await Assert.ThrowsAsync<RpcException>(() => OpenAsync(path));

        Assert.Empty(_processes.Started);
    }
}
