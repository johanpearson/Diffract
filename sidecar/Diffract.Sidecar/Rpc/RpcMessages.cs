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
    /// <summary>A JSON <c>null</c>. A successful response must carry a result, even when there is nothing to say.</summary>
    public static JsonElement NullResult { get; } = JsonDocument.Parse("null").RootElement;

    public static RpcResponse Success(JsonElement? id, JsonElement result) => new("2.0", id, result, null);

    public static RpcResponse Failure(JsonElement? id, int code, string message, ErrorKind? kind = null) =>
        new("2.0", id, null, new RpcError(code, message, kind is { } k ? new RpcErrorData(k) : null));
}

/// <summary>A message from the sidecar that needs no answer, such as <c>repo.changed</c>.</summary>
internal sealed record RpcNotification(string Jsonrpc, string Method, JsonElement Params)
{
    public RpcNotification(string method, JsonElement parameters) : this("2.0", method, parameters)
    {
    }
}

internal sealed record RpcError(
    int Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] RpcErrorData? Data);

/// <summary>Set on Diffract's own errors, so the UI can tell them apart without reading the message.</summary>
internal sealed record RpcErrorData(ErrorKind Kind);

/// <summary>What went wrong, for errors that are Diffract's own rather than the protocol's.</summary>
internal enum ErrorKind { NotARepo, GitTooOld, GitFailed, TimedOut, ConfigInvalid }

/// <summary>The error codes JSON-RPC 2.0 defines, the one LSP adds for cancellation, and Diffract's own.</summary>
internal static class RpcErrorCodes
{
    public const int ParseError = -32700;
    public const int InvalidRequest = -32600;
    public const int MethodNotFound = -32601;
    public const int InvalidParams = -32602;
    public const int InternalError = -32603;

    /// <summary>The request was cancelled with <c>$/cancelRequest</c> or because the sidecar is shutting down.</summary>
    public const int RequestCancelled = -32800;

    public static int For(ErrorKind kind) => kind switch
    {
        ErrorKind.NotARepo => -32001,
        ErrorKind.GitTooOld => -32002,
        ErrorKind.GitFailed => -32003,
        ErrorKind.TimedOut => -32004,
        ErrorKind.ConfigInvalid => -32005,
        _ => InternalError,
    };
}

/// <summary>Thrown by a method to answer with an error. <see cref="Kind"/> is set for Diffract's own errors.</summary>
internal sealed class RpcException(int code, string message, ErrorKind? kind = null) : Exception(message)
{
    public int Code { get; } = code;

    public ErrorKind? Kind { get; } = kind;

    public RpcException(ErrorKind kind, string message) : this(RpcErrorCodes.For(kind), message, kind)
    {
    }
}

/// <summary>
/// Serializer settings for everything the sidecar reads and writes. Relaxed escaping keeps quotes and å ä ö readable;
/// the output is read by the app, never embedded in HTML.
/// </summary>
internal static class RpcJson
{
    public static JsonSerializerOptions Options { get; } = new(RpcJsonContext.Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static JsonTypeInfo<T> Type<T>() => (JsonTypeInfo<T>)Options.GetTypeInfo(typeof(T));
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(RpcRequest))]
[JsonSerializable(typeof(RpcResponse))]
[JsonSerializable(typeof(RpcNotification))]
[JsonSerializable(typeof(CancelParams))]
[JsonSerializable(typeof(AppInfo))]
[JsonSerializable(typeof(RepoOpenParams))]
[JsonSerializable(typeof(RepoRefreshParams))]
[JsonSerializable(typeof(RepoCloseParams))]
[JsonSerializable(typeof(OpenFolderParams))]
[JsonSerializable(typeof(OpenTerminalParams))]
[JsonSerializable(typeof(IReadOnlyList<TerminalInfo>))]
[JsonSerializable(typeof(RepoState))]
[JsonSerializable(typeof(RepoChanged))]
[JsonSerializable(typeof(GitUpdateCheck))]
[JsonSerializable(typeof(GitUpdateParams))]
[JsonSerializable(typeof(GitUpdateResult))]
[JsonSerializable(typeof(IReadOnlyList<GitInUse>))]
[JsonSerializable(typeof(HistoryReadParams))]
[JsonSerializable(typeof(HistoryUpdateParams))]
[JsonSerializable(typeof(HistoryUpdateResult))]
[JsonSerializable(typeof(HistoryRefsParams))]
[JsonSerializable(typeof(HistoryDetailsParams))]
[JsonSerializable(typeof(HistoryFileParams))]
[JsonSerializable(typeof(HistoryPage))]
[JsonSerializable(typeof(HistoryReadResult))]
[JsonSerializable(typeof(HistoryRefs))]
[JsonSerializable(typeof(Diffract.Git.Parsing.CommitDetails))]
[JsonSerializable(typeof(HistoryFile))]
[JsonSerializable(typeof(ChangesStatusParams))]
[JsonSerializable(typeof(ChangesFileParams))]
[JsonSerializable(typeof(Diffract.Git.Changes.WorktreeChanges))]
[JsonSerializable(typeof(ChangesFile))]
internal sealed partial class RpcJsonContext : JsonSerializerContext;
