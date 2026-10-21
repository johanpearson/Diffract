using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Diffract.Sidecar.Rpc;

/// <param name="Id">A number or a string, echoed back unchanged. Null for a notification.</param>
internal sealed record RpcRequest(string? Jsonrpc, JsonElement? Id, string? Method, JsonElement? Params);

internal sealed record RpcResponse(
    string Jsonrpc,
    JsonElement? Id,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Result,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcError? Error)
{
    public static RpcResponse Success(JsonElement? id, JsonElement result) => new("2.0", id, result, null);

    public static RpcResponse Failure(JsonElement? id, int code, string message) => new("2.0", id, null, new RpcError(code, message));
}

internal sealed record RpcError(int Code, string Message);

/// <summary>The error codes JSON-RPC 2.0 defines.</summary>
internal static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;
}

internal sealed class RpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Serializer settings for everything the sidecar writes. Relaxed escaping keeps quotes and å ä ö readable;
/// the output is read by the app, never embedded in HTML.
/// </summary>
internal static class RpcJson
{
    public static JsonSerializerOptions Options { get; } = new(RpcJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonTypeInfo<RpcResponse> Response { get; } = (JsonTypeInfo<RpcResponse>)Options.GetTypeInfo(typeof(RpcResponse));

    public static JsonTypeInfo<AppInfo> AppInfo { get; } = (JsonTypeInfo<AppInfo>)Options.GetTypeInfo(typeof(AppInfo));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(AppInfo))]
internal sealed partial class RpcJsonContext : JsonSerializerContext;
