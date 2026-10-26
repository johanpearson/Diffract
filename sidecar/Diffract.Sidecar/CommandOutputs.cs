using System.Text.Json;
using Diffract.Git;

namespace Diffract.Sidecar;

/// <summary>A line for the command window: the <c>command.output</c> notification.</summary>
/// <param name="Operation">The id the request was given as <c>operation</c>.</param>
internal sealed record CommandOutput(string Operation, GitOutputKind Kind, string Text);

/// <summary>
/// Git's output for the command window. A request that names an <c>operation</c> gets each command it runs, and each
/// line git writes to standard error, as a <c>command.output</c> notification, all sent before the request is answered.
/// A request without one runs quietly, as before.
/// </summary>
internal sealed class CommandOutputs(Func<string, JsonElement, Task> notify)
{
    public const string Method = "command.output";

    /// <summary>The runner options for one request.</summary>
    public GitRunnerOptions For(GitRunnerOptions options, JsonElement? parameters) =>
        OperationOf(parameters) is { } operation ? options with { OnOutput = line => Send(operation, line) } : options;

    // Waited for, so the lines go out in order and before the answer.
    private void Send(string operation, GitOutputLine line) =>
        notify(Method, SidecarMethods.Result(new CommandOutput(operation, line.Kind, line.Text))).GetAwaiter().GetResult();

    private static string? OperationOf(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } p && p.TryGetProperty("operation", out var operation) && operation.ValueKind == JsonValueKind.String
            ? operation.GetString()
            : null;
}
