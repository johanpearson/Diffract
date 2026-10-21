using System.Text;
using System.Text.Json;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>The line protocol, with a fake method table so no git is involved.</summary>
public class RpcHostTests
{
    private static async Task<JsonElement> Methods(string method, JsonElement? parameters, CancellationToken ct) => method switch
    {
        "echo" => parameters ?? JsonSerializer.SerializeToElement("no params"),
        "nothing" => RpcResponse.NullResult,
        "fail" => throw new RpcException(RpcErrorCodes.InvalidParams, "Bad params"),
        "notARepo" => throw new RpcException(ErrorKind.NotARepo, "Not a git repository: C:\\src"),
        "crash" => throw new InvalidOperationException("Something broke"),
        "wait" => await WaitForCancellation(ct),
        _ => throw new RpcException(RpcErrorCodes.MethodNotFound, $"Unknown method '{method}'"),
    };

    private static async Task<JsonElement> WaitForCancellation(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    private static async Task<(string[] Lines, string Log)> RunAsync(params string[] input)
    {
        var (output, log) = (new StringWriter { NewLine = "\n" }, new StringWriter());
        await new RpcHost(new StringReader(string.Join("\n", input)), output, Methods, log).RunAsync(TestContext.Current.CancellationToken);
        return (output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries), log.ToString());
    }

    [Fact]
    public async Task Answers_each_request_on_its_own_line_with_the_same_id()
    {
        var (lines, _) = await RunAsync(
            """{"jsonrpc":"2.0","id":1,"method":"echo","params":{"a":"line\nbreak"}}""",
            """{"jsonrpc":"2.0","id":"two","method":"echo"}""");

        Assert.Equivalent(
            new[]
            {
                """{"jsonrpc":"2.0","id":1,"result":{"a":"line\nbreak"}}""",
                """{"jsonrpc":"2.0","id":"two","result":"no params"}""",
            },
            lines);
    }

    [Fact]
    public async Task A_method_with_nothing_to_return_answers_with_a_null_result()
    {
        var (lines, _) = await RunAsync("""{"jsonrpc":"2.0","id":1,"method":"nothing"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":null}""", Assert.Single(lines));
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
    public async Task Diffract_errors_carry_their_kind_in_data()
    {
        var (lines, _) = await RunAsync("""{"jsonrpc":"2.0","id":1,"method":"notARepo"}""");

        Assert.Equal(
            """{"jsonrpc":"2.0","id":1,"error":{"code":-32001,"message":"Not a git repository: C:\\src","data":{"kind":"NotARepo"}}}""",
            Assert.Single(lines));
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

    [Fact]
    public async Task A_slow_request_does_not_hold_up_the_next_one()
    {
        await using var host = new HostHarness(Methods);

        host.Send("""{"jsonrpc":"2.0","id":1,"method":"wait"}""");
        host.Send("""{"jsonrpc":"2.0","id":2,"method":"echo"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":"no params"}""", await host.NextAsync());
    }

    [Theory]
    [InlineData("1")]
    [InlineData("\"abc\"")]
    public async Task Cancel_request_cancels_the_request_with_that_id(string id)
    {
        await using var host = new HostHarness(Methods);
        host.Send($$$"""{"jsonrpc":"2.0","id":{{{id}}},"method":"wait"}""");
        host.Send($$$"""{"jsonrpc":"2.0","id":99,"method":"echo"}""");
        await host.NextAsync(); // the echo, so "wait" is surely running

        host.Send($$$"""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":{{{id}}}}}""");

        Assert.Equal($$$"""{"jsonrpc":"2.0","id":{{{id}}},"error":{"code":-32800,"message":"The request was cancelled"}}""", await host.NextAsync());
    }

    [Fact]
    public async Task Cancel_matches_the_id_exactly_so_1_does_not_cancel_the_string_1()
    {
        await using var host = new HostHarness(Methods);
        host.Send("""{"jsonrpc":"2.0","id":"1","method":"wait"}""");

        host.Send("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":1}}""");
        host.Send("""{"jsonrpc":"2.0","id":2,"method":"echo"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":"no params"}""", await host.NextAsync());
        host.Send("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":"1"}}""");
        Assert.Contains("-32800", await host.NextAsync());
    }

    [Theory]
    [InlineData("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":42}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{}}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":"nonsense"}""")]
    [InlineData("""{"jsonrpc":"2.0","method":"$/cancelRequest"}""")]
    public async Task A_cancel_for_nothing_is_ignored(string cancel)
    {
        var (lines, log) = await RunAsync(cancel, """{"jsonrpc":"2.0","id":1,"method":"echo"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":1,"result":"no params"}""", Assert.Single(lines));
        Assert.Empty(log);
    }

    [Fact]
    public async Task An_id_that_is_still_running_cannot_be_reused()
    {
        await using var host = new HostHarness(Methods);
        host.Send("""{"jsonrpc":"2.0","id":7,"method":"wait"}""");

        host.Send("""{"jsonrpc":"2.0","id":7,"method":"echo"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":7,"error":{"code":-32600,"message":"Request id 7 is already in use"}}""", await host.NextAsync());
    }

    [Fact]
    public async Task Shutdown_answers_then_cancels_what_is_running_and_stops_reading()
    {
        await using var host = new HostHarness(Methods);
        host.Send("""{"jsonrpc":"2.0","id":1,"method":"wait"}""");

        host.Send("""{"jsonrpc":"2.0","id":2,"method":"app.shutdown"}""");
        host.Send("""{"jsonrpc":"2.0","id":3,"method":"echo"}""");

        Assert.Equal("""{"jsonrpc":"2.0","id":2,"result":null}""", await host.NextAsync());
        Assert.Equal("""{"jsonrpc":"2.0","id":1,"error":{"code":-32800,"message":"The request was cancelled"}}""", await host.NextAsync());
        await host.Running.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Empty(host.Drain());
    }

    [Fact]
    public async Task The_end_of_the_input_cancels_what_is_running_and_stops()
    {
        await using var host = new HostHarness(Methods);
        host.Send("""{"jsonrpc":"2.0","id":1,"method":"wait"}""");

        await host.EndInputAsync();

        Assert.Equal("""{"jsonrpc":"2.0","id":1,"error":{"code":-32800,"message":"The request was cancelled"}}""", Assert.Single(host.Drain()));
    }

    [Fact]
    public async Task Notifications_from_the_sidecar_are_one_line_each()
    {
        await using var host = new HostHarness(Methods);

        await host.Host.NotifyAsync("repo.changed", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = "C:\\räksmörgås\nx" }));

        Assert.Equal("""{"jsonrpc":"2.0","method":"repo.changed","params":{"path":"C:\\räksmörgås\nx"}}""", await host.NextAsync());
    }

    [Fact]
    public async Task Lines_written_at_the_same_time_never_interleave()
    {
        using var stream = new MemoryStream();
        var output = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        var host = new RpcHost(new StringReader(""), output, Methods, TextWriter.Null);
        var payload = JsonSerializer.SerializeToElement(new string('x', 2000));

        await Task.WhenAll(Enumerable.Range(0, 200).Select(_ => Task.Run(() => host.NotifyAsync("n", payload), TestContext.Current.CancellationToken)));

        var lines = Encoding.UTF8.GetString(stream.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(200, lines.Length);
        Assert.All(lines, line => Assert.Equal("n", JsonDocument.Parse(line).RootElement.GetProperty("method").GetString()));
    }
}
