namespace Diffract.Spike;

internal static class Program
{
    public static async Task<int> Main(string[] args) => args switch
    {
        ["fixtures", var outputDir, ..] => await FixturesCommand.RunAsync(outputDir, args.AsMemory(2)),
        // The scan command arrives in phase 5 of milestone 1.
        _ => Usage(),
    };

    public static int Usage()
    {
        Console.Error.WriteLine("usage: diffract-spike fixtures <output-dir> [--scenario <name>]");
        return 1;
    }
}
