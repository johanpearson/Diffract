using System.Text.Json;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>The line protocol, with a fake method table so no git is involved.</summary>
public class RpcHostTests
{
    private static Task<JsonElement> Echo(string method, JsonElement? parameters, CancellationToken ct) => method switch
    {
        "echo" => Task.FromResult(parameters ?? JsonSerializer.SerializeToElement("no params")),
        "fail" => throw new RpcException(RpcErrorCodes.InvalidParams, "Bad params"),
        "crash" => throw new InvalidOperationException("Something broke"),
        _ => throw new RpcException(RpcErrorCodes.MethodNotFound, $"Unknown method '{method}'"),
    };

    private static async Task<(string[] Lines, string Log)> RunAsync(params string[] input)
    {
        var (output, log) = (new StringWriter { NewLine = "\n" }, new StringWriter());
        await new RpcHost(new StringReader(string.Join("\n", input)), output, Echo, log).RunAsync(TestContext.Current.CancellationToken);
        return (output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries), log.ToString());
    }

    [Fact]
    public async Task Answers_each_request_on_its_own_line_with_the_same_id()
    {
        var (lines, _) = await RunAsync(
            """{"jsonrpc":"2.0","id":1,"method":"echo","params":{"a":"line\nbreak"}}""",
            """{"jsonrpc":"2.0","id":"two","method":"echo"}""");

        Assert.Equal(
            [
                """{"jsonrpc":"2.0","id":1,"result":{"a":"line\nbreak"}}""",
                """{"jsonrpc":"2.0","id":"two","result":"no params"}""",
            ],
            lines);
    }

    [Fact]
    public async Task Notifications_get_no_response()
    {
        var (lines, _) = await RunAsync("""{"jsonrpc":"2.0","method":"echo"}""", """{"jsonrpc":"2.0","method":"unknown"}""");

        Assert.Empty(lines);
    }

    [Fact]
    public async Task Blank_lines_are_skipped()
    {
        var (lines, _) = await RunAsync("", "   ", """{"jsonrpc":"2.0","id":1,"method":"echo"}""");

        Assert.Single(lines);
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"unknown"}""", """{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Unknown method 'unknown'"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1,"method":"fail"}""", """{"jsonrpc":"2.0","id":1,"error":{"code":-32602,"message":"Bad params"}}""")]
    [InlineData("""{"jsonrpc":"1.0","id":1,"method":"echo"}""", """{"jsonrpc":"2.0","id":1,"error":{"code":-32600,"message":"Not a JSON-RPC 2.0 request"}}""")]
    [InlineData("""{"jsonrpc":"2.0","id":1}""", """{"jsonrpc":"2.0","id":1,"error":{"code":-32600,"message":"Not a JSON-RPC 2.0 request"}}""")]
    public async Task Errors_use_the_json_rpc_codes(string request, string expected)
    {
        var (lines, _) = await RunAsync(request);

        Assert.Equal(expected, Assert.Single(lines));
    }

    [Fact]
    public async Task Invalid_json_is_a_parse_error_with_a_null_id()
    {
        var (lines, _) = await RunAsync("{not json", """{"jsonrpc":"2.0","id":2,"method":"echo"}""");

        using var error = JsonDocument.Parse(lines[0]);
        Assert.Equal(JsonValueKind.Null, error.RootElement.GetProperty("id").ValueKind);
        Assert.Equal(-32700, error.RootElement.GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":"no params"}""", lines[1]);
    }

    [Fact]
    public async Task An_unexpected_exception_is_an_internal_error_and_is_logged()
    {
        var (lines, log) = await RunAsync("""{"jsonrpc":"2.0","id":1,"method":"crash"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":1,"error":{"code":-32603,"message":"Something broke"}}""", Assert.Single(lines));
        Assert.Contains("crash failed", log);
    }
}
