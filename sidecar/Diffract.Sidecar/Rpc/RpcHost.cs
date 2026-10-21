using System.Collections.Concurrent;
using System.Text.Json;

namespace Diffract.Sidecar.Rpc;

/// <summary>Handles one request and returns its result, or throws <see cref="RpcException"/>.</summary>
internal delegate Task<JsonElement> RpcDispatch(string method, JsonElement? parameters, CancellationToken ct);

/// <param name="Id">The id of the request to cancel.</param>
internal sealed record CancelParams(JsonElement? Id);

/// <summary>
/// Reads JSON-RPC 2.0 messages from <paramref name="input"/>, one per line, and writes one response line per request.
/// Requests run concurrently, each with its own cancellation, so responses can come back in any order.
/// Notifications (requests without an id) get no response.
/// Stops when the input ends or after answering <c>app.shutdown</c>, and cancels whatever is still running.
/// </summary>
internal sealed class RpcHost(TextReader input, TextWriter output, RpcDispatch dispatch, TextWriter log)
{
    public const string CancelMethod = "$/cancelRequest";
    public const string ShutdownMethod = "app.shutdown";

    private readonly SemaphoreSlim _writeLock = new(1, 1);

    /// <summary>Running requests by their id as raw JSON, so 1 and "1" stay apart.</summary>
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

    private readonly ConcurrentDictionary<Task, byte> _running = new();

    public async Task RunAsync(CancellationToken ct)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            while (await input.ReadLineAsync(stopping.Token).ConfigureAwait(false) is { } line)
            {
                if (!string.IsNullOrWhiteSpace(line) && !await ReceiveAsync(line, stopping.Token).ConfigureAwait(false))
                    break;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        finally
        {
            await stopping.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(_running.Keys).ConfigureAwait(false);
        }
    }

    /// <summary>Sends a notification. Safe to call while requests are running.</summary>
    public Task NotifyAsync(string method, JsonElement parameters) =>
        WriteAsync(JsonSerializer.Serialize(new RpcNotification(method, parameters), RpcJson.Type<RpcNotification>()));

    /// <returns>False when the host should stop reading.</returns>
    private async Task<bool> ReceiveAsync(string line, CancellationToken stopping)
    {
        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(line, RpcJsonContext.Default.RpcRequest);
        }
        catch (JsonException e)
        {
            await RespondAsync(RpcResponse.Failure(null, RpcErrorCodes.ParseError, $"Not valid JSON: {e.Message}")).ConfigureAwait(false);
            return true;
        }

        if (request is not { Jsonrpc: "2.0", Method: { Length: > 0 } method })
        {
            await RespondAsync(RpcResponse.Failure(request?.Id, RpcErrorCodes.InvalidRequest, "Not a JSON-RPC 2.0 request")).ConfigureAwait(false);
            return true;
        }

        switch (method)
        {
            case CancelMethod:
                Cancel(request.Params);
                await RespondIfRequestAsync(request, RpcResponse.Success(request.Id, RpcResponse.NullResult)).ConfigureAwait(false);
                return true;

            case ShutdownMethod:
                await RespondIfRequestAsync(request, RpcResponse.Success(request.Id, RpcResponse.NullResult)).ConfigureAwait(false);
                return false;
        }

        var key = request.Id?.GetRawText();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        if (key is not null && !_inFlight.TryAdd(key, cancellation))
        {
            cancellation.Dispose();
            await RespondAsync(RpcResponse.Failure(request.Id, RpcErrorCodes.InvalidRequest, $"Request id {key} is already in use")).ConfigureAwait(false);
            return true;
        }

        // Task.Run, so a method that starts slowly cannot hold up reading the next line, such as its own cancellation.
        var task = Task.Run(() => HandleAsync(request, method, key, cancellation), CancellationToken.None);
        _running[task] = 0;
        _ = task.ContinueWith(t => _running.TryRemove(t, out _), TaskScheduler.Default);
        return true;
    }

    private async Task HandleAsync(RpcRequest request, string method, string? key, CancellationTokenSource cancellation)
    {
        RpcResponse response;
        try
        {
            var result = await dispatch(method, request.Params, cancellation.Token).ConfigureAwait(false);
            response = RpcResponse.Success(request.Id, result);
        }
        catch (RpcException e)
        {
            response = RpcResponse.Failure(request.Id, e.Code, e.Message, e.Kind);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            response = RpcResponse.Failure(request.Id, RpcErrorCodes.RequestCancelled, "The request was cancelled");
        }
        catch (Exception e)
        {
            await log.WriteLineAsync($"{method} failed: {e}").ConfigureAwait(false);
            response = RpcResponse.Failure(request.Id, RpcErrorCodes.InternalError, e.Message);
        }
        finally
        {
            if (key is not null)
                _inFlight.TryRemove(key, out _);
            cancellation.Dispose();
        }

        try
        {
            await RespondIfRequestAsync(request, response).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            await log.WriteLineAsync($"Could not answer {method}: {e.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>Cancels the request named in the params. Unknown or finished ids are ignored, as in LSP.</summary>
    private void Cancel(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } p)
            return;

        try
        {
            if (p.Deserialize(RpcJsonContext.Default.CancelParams)?.Id is { } id && _inFlight.TryGetValue(id.GetRawText(), out var cancellation))
                cancellation.Cancel();
        }
        catch (Exception e) when (e is JsonException or ObjectDisposedException)
        {
            // A malformed cancel, or the request finished meanwhile. Either way there is nothing to cancel.
        }
    }

    private Task RespondIfRequestAsync(RpcRequest request, RpcResponse response) =>
        request.Id is null ? Task.CompletedTask : RespondAsync(response);

    private Task RespondAsync(RpcResponse response) => WriteAsync(JsonSerializer.Serialize(response, RpcJson.Type<RpcResponse>()));

    private async Task WriteAsync(string line)
    {
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await output.WriteLineAsync(line).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
