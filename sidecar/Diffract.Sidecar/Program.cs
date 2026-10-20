using System.Text;
using Diffract.Git;
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

        var methods = new SidecarMethods(new GitRunnerOptions());
        await new RpcHost(input, output, methods.Dispatch, Console.Error).RunAsync(CancellationToken.None);
        return 0;
    }
}
