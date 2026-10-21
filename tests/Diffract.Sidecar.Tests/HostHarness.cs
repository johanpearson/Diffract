using System.Text.Json;
using System.Threading.Channels;
using Diffract.Sidecar.Rpc;

namespace Diffract.Sidecar.Tests;

/// <summary>
/// Runs an <see cref="RpcHost"/> in process. Lines are sent one at a time and the host's output is read as it arrives,
/// so a test can send a request, wait for something, and only then send the next.
/// </summary>
internal sealed class HostHarness : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly Channel<string> _input = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _output = Channel.CreateUnbounded<string>();
    private readonly StringWriter _log = new();

    public HostHarness(RpcDispatch dispatch)
    {
        Host = new RpcHost(new ChannelReaderText(_input.Reader), new ChannelWriterText(_output.Writer), dispatch, _log);
        Running = Host.RunAsync(TestContext.Current.CancellationToken);
    }

    public RpcHost Host { get; }

    /// <summary>Completes when the host stops.</summary>
    public Task Running { get; }

    public string Log => _log.ToString();

    public void Send(string line) => _input.Writer.TryWrite(line);

    /// <summary>Ends the input, as when the app closes the sidecar's stdin, and waits for the host to stop.</summary>
    public async Task EndInputAsync()
    {
        _input.Writer.TryComplete();
        await Running.WaitAsync(Patience, TestContext.Current.CancellationToken);
    }

    /// <summary>The next line the host wrote.</summary>
    public async Task<string> NextAsync() =>
        await _output.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(Patience, TestContext.Current.CancellationToken);

    public async Task<JsonElement> NextJsonAsync()
    {
        using var document = JsonDocument.Parse(await NextAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Every line written so far, without waiting for more.</summary>
    public List<string> Drain()
    {
        var lines = new List<string>();
        while (_output.Reader.TryRead(out var line))
            lines.Add(line);
        return lines;
    }

    public async ValueTask DisposeAsync()
    {
        _input.Writer.TryComplete();
        await Running.WaitAsync(Patience);
    }

    private sealed class ChannelReaderText(ChannelReader<string> lines) : TextReader
    {
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            while (await lines.WaitToReadAsync(cancellationToken))
            {
                if (lines.TryRead(out var line))
                    return line;
            }
            return null;
        }
    }

    private sealed class ChannelWriterText(ChannelWriter<string> lines) : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public override Task WriteLineAsync(string? value)
        {
            lines.TryWrite(value ?? "");
            return Task.CompletedTask;
        }
    }
}
