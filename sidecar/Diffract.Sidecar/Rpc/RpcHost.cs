using System.Text.Json;

namespace Diffract.Sidecar.Rpc;

/// <summary>Handles one request and returns its result, or throws <see cref="RpcException"/>.</summary>
internal delegate Task<JsonElement> RpcDispatch(string method, JsonElement? parameters, CancellationToken ct);

/// <summary>
/// Reads JSON-RPC 2.0 messages from <paramref name="input"/>, one per line, and writes one response line per request.
/// Notifications (requests without an id) get no response. Stops when the input ends.
/// </summary>
internal sealed class RpcHost(TextReader input, TextWriter output, RpcDispatch dispatch, TextWriter log)
{
    public async Task RunAsync(CancellationToken ct)
    {
        while (await input.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (await HandleAsync(line, ct).ConfigureAwait(false) is { } response)
                await output.WriteLineAsync(JsonSerializer.Serialize(response, RpcJson.Response)).ConfigureAwait(false);
        }
    }

    private async Task<RpcResponse?> HandleAsync(string line, CancellationToken ct)
    {
        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, RpcJsonContext.Default.RpcRequest);
        }
        catch (JsonException e)
        {
            return RpcResponse.Failure(null, RpcErrorCodes.ParseError, $"Not valid JSON: {e.Message}");
        }

        if (request is not { Jsonrpc: "2.0", Method: { Length: > 0 } method })
            return RpcResponse.Failure(request?.Id, RpcErrorCodes.InvalidRequest, "Not a JSON-RPC 2.0 request");

        try
        {
            var result = await dispatch(method, request.Params, ct).ConfigureAwait(false);
            return request.Id is null ? null : RpcResponse.Success(request.Id, result);
        }
        catch (RpcException e)
        {
            return request.Id is null ? null : RpcResponse.Failure(request.Id, e.Code, e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            await log.WriteLineAsync($"{method} failed: {e}").ConfigureAwait(false);
            return request.Id is null ? null : RpcResponse.Failure(request.Id, RpcErrorCodes.InternalError, e.Message);
        }
    }
}
