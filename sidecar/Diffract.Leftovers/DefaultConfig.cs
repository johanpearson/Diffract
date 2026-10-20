using System.Reflection;

namespace Diffract.Leftovers;

/// <summary>The default configuration, embedded from diffract.json at the repo root.</summary>
public static class DefaultConfig
{
    private const string ResourceName = "Diffract.Leftovers.diffract.json";

    public static string ReadJson()
    {
        using var stream = typeof(DefaultConfig).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{ResourceName}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
