using System.Text;

namespace Diffract.Git.Tests;

/// <summary>Reads captured git output from the Samples folder. See Samples/capture-samples.sh.</summary>
internal static class Sample
{
    public static string Read(string name) =>
        Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Samples", name)));
}
