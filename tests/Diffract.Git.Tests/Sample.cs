using System.Text;

namespace Diffract.Git.Tests;

/// <summary>Reads captured git output from the Samples folder. See Samples/capture-samples.sh.</summary>
internal static class Sample
{
    public static string Read(string name) => Encoding.UTF8.GetString(ReadBytes(name));

    public static byte[] ReadBytes(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name));

    /// <summary>The text of a capture script, to check it uses the same format as the parser.</summary>
    public static string Script(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Samples", name));
}
