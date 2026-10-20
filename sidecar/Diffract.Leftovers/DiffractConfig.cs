using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Diffract.Git.Snapshot;

namespace Diffract.Leftovers;

/// <summary>
/// Settings from diffract.json. Patterns that change what is reported have their own keys;
/// <see cref="Agents"/> only fills in the <see cref="Leftover.Agent"/> hint.
/// </summary>
public sealed record DiffractConfig
{
    /// <summary>"auto", or the name of the main branch.</summary>
    public string MainBranch { get; init; } = "auto";

    public int StaleAfterDays { get; init; }

    public int ActiveWithinMinutes { get; init; }

    public ArchiveConfig Archive { get; init; } = new();

    public IReadOnlyList<AgentConfig> Agents { get; init; } = [];

    public IReadOnlyList<string> CheckpointRefPatterns { get; init; } = [];

    /// <summary>Worktrees at these paths are never reported unless they are dirty.</summary>
    public IReadOnlyList<string> HiddenWorktreePathPatterns { get; init; } = [];

    public IReadOnlyList<string> WorktreeContainerPatterns { get; init; } = [];

    /// <summary>Stashes whose message contains one of these, ignoring case, are reported.</summary>
    public IReadOnlyList<string> ToolStashPatterns { get; init; } = [];

    /// <summary>Merged remote-tracking branches are reported only when the name after the remote matches one of these.</summary>
    public IReadOnlyList<string> RemoteBranchPatterns { get; init; } = [];

    /// <summary>The defaults embedded from diffract.json.</summary>
    public static DiffractConfig Default { get; } = Load(null);

    /// <summary>
    /// The defaults with <paramref name="overrideJson"/> laid over them. Objects merge key by key;
    /// any other value, including a list, replaces the default.
    /// </summary>
    public static DiffractConfig Load(string? overrideJson)
    {
        try
        {
            var merged = JsonNode.Parse(DefaultConfig.ReadJson())!;
            if (overrideJson is not null)
                merged = Merge(merged, JsonNode.Parse(overrideJson, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }));

            var config = merged.Deserialize(DiffractConfigJsonContext.Default.DiffractConfig)
                ?? throw new ConfigException("The config is empty.");
            config.Validate();
            return config;
        }
        catch (JsonException e)
        {
            throw new ConfigException($"The config is not valid: {e.Message}", e);
        }
    }

    public static DiffractConfig LoadFile(string path)
    {
        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new ConfigException($"Cannot read config file {path}: {e.Message}", e);
        }

        return Load(json);
    }

    public SnapshotOptions ToSnapshotOptions() => new()
    {
        MainBranch = MainBranch,
        WorktreeContainerPatterns = WorktreeContainerPatterns,
    };

    private void Validate()
    {
        if (StaleAfterDays < 1)
            throw new ConfigException("staleAfterDays must be at least 1.");
        if (ActiveWithinMinutes < 0)
            throw new ConfigException("activeWithinMinutes cannot be negative.");
        if (Archive.ReviewAfterDays < 1)
            throw new ConfigException("archive.reviewAfterDays must be at least 1.");
        if (string.IsNullOrEmpty(Archive.TagPrefix))
            throw new ConfigException("archive.tagPrefix cannot be empty.");
    }

    private static JsonNode? Merge(JsonNode defaults, JsonNode? overrides)
    {
        if (defaults is not JsonObject target || overrides is not JsonObject source)
            return overrides?.DeepClone();

        foreach (var (key, value) in source)
            target[key] = target[key] is { } existing ? Merge(existing.DeepClone(), value) : value?.DeepClone();
        return target;
    }
}

public sealed record ArchiveConfig
{
    public string TagPrefix { get; init; } = "";

    public int ReviewAfterDays { get; init; }
}

public sealed record AgentConfig
{
    public string Name { get; init; } = "";

    public IReadOnlyList<string> BranchPatterns { get; init; } = [];

    public IReadOnlyList<string> WorktreePathPatterns { get; init; } = [];

    public IReadOnlyList<string> RefPatterns { get; init; } = [];
}

public sealed class ConfigException(string message, Exception? inner = null) : Exception(message, inner);

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(DiffractConfig))]
internal sealed partial class DiffractConfigJsonContext : JsonSerializerContext;
