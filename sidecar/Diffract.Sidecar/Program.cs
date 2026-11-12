using System.Text;
using Diffract.Git;
using Diffract.Leftovers.Cleanup;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar;

/// <summary>
/// The sidecar the Diffract app starts. It speaks JSON-RPC 2.0 over stdin and stdout, one message per line.
/// stdout carries only protocol messages; anything else goes to stderr.
/// </summary>
internal static class Program
{
    public static async Task<int> Main()
    {
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var input = new StreamReader(Console.OpenStandardInput(), utf8);
        await using var output = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };

        // The methods send notifications through the host, and the host dispatches to the methods.
        RpcHost? host = null;
        // The real git, not Git for Windows' launcher, which costs a second process start on every call. Found again
        // when an update of Git for Windows moves it.
        var runnerOptions = new GitRunnerOptions { Locator = GitLocator.ForThisMachine() };
        // The copies of discarded files last until Diffract closes: cleared now, in case the last run crashed, and on exit.
        var copies = DiscardBackups.Default;
        ClearCopies(copies);
        try
        {
            using var methods = new SidecarMethods(runnerOptions, new SidecarDependencies { Notify = (method, parameters) => host!.NotifyAsync(method, parameters), Log = Console.Error, DiscardBackups = copies });
            host = new RpcHost(input, output, methods.Dispatch, Console.Error);
            await host.RunAsync(CancellationToken.None);
            return 0;
        }
        finally
        {
            ClearCopies(copies);
        }
    }

    /// <summary>A copy another program holds open stays until the next start. That must never stop Diffract.</summary>
    private static void ClearCopies(DiscardBackups copies)
    {
        try
        {
            copies.Clear();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove the copies of discarded files: {e.Message}");
        }
    }
}
