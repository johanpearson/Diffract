namespace Diffract.Spike.Tests;

public class ScanOptionsTests
{
    [Fact]
    public void No_options_means_text_output_with_default_config()
    {
        Assert.Equal(new ScanOptions(Json: false, Timing: false, ConfigPath: null), ScanOptions.Parse([]));
    }

    [Fact]
    public void Options_can_come_in_any_order()
    {
        Assert.Equal(new ScanOptions(true, true, @"C:\my config.json"), ScanOptions.Parse(["--timing", "--config", @"C:\my config.json", "--json"]));
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("--config")]
    [InlineData("--json", "--json")]
    [InlineData("--config", "a.json", "--config", "b.json")]
    [InlineData("extra-path")]
    public void Invalid_options_return_null(params string[] args)
    {
        Assert.Null(ScanOptions.Parse(args));
    }
}
