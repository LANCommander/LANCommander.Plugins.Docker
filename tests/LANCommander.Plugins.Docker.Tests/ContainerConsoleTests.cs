using System.Text;
using System.Threading.Channels;
using LANCommander.Plugins.Docker.Engine;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Tests;

/// <summary>The engine's consoles, as far as they can be tried without a Docker daemon.</summary>
public sealed class ContainerConsoleTests
{
    [Fact]
    public void AServerWithAContainerOffersItsLogAndTwoTerminals()
    {
        var consoles = ContainerConsoles.For(new DockerServerSettings { HostId = Guid.NewGuid(), ContainerId = "arena" });

        Assert.Equal(
        [
            new ServerEngineConsole("log", "Container log", ServerEngineConsoleKind.Log),
            new ServerEngineConsole("attach", "Attach", ServerEngineConsoleKind.Terminal),
            new ServerEngineConsole("shell", "Shell", ServerEngineConsoleKind.Terminal),
        ], consoles);
    }

    [Fact]
    public void AServerNotSetUpYetOffersNone()
    {
        Assert.Empty(ContainerConsoles.For(new DockerServerSettings()));
        Assert.Empty(ContainerConsoles.For(new DockerServerSettings { HostId = Guid.NewGuid() }));
    }

    [Fact]
    public void ALogLineIsSplitFromItsTimestamp()
    {
        var line = ContainerConsoles.ToLogLine("2026-10-31T17:04:05.123456789Z Map loaded: de_dust2", isError: true);

        Assert.Equal("Map loaded: de_dust2", line.Text);
        Assert.Equal(new DateTime(2026, 10, 31, 17, 4, 5, DateTimeKind.Utc).AddTicks(1234567), line.Timestamp);
        Assert.Equal(DateTimeKind.Utc, line.Timestamp!.Value.Kind);
        Assert.True(line.IsError);
    }

    [Fact]
    public void ALogLineWithoutATimestampIsKeptWhole()
    {
        var line = ContainerConsoles.ToLogLine("Server ready on 27015", isError: false);

        Assert.Equal("Server ready on 27015", line.Text);
        Assert.Null(line.Timestamp);
    }

    [Fact]
    public void BareLineFeedsGetCarriageReturns()
    {
        Assert.Equal("a\r\nb\r\nc\r\n"u8.ToArray(), LineTerminal.WithCarriageReturns("a\nb\r\nc\n"u8));
    }

    [Fact]
    public async Task ALineIsEchoedEditedAndSentOnEnter()
    {
        var container = new FakeTerminal();
        await using var terminal = new LineTerminal(container, canWrite: true);

        await terminal.WriteAsync("sayy"u8.ToArray(), CancellationToken.None);
        await terminal.WriteAsync("\x7f hi\r"u8.ToArray(), CancellationToken.None);

        Assert.Equal(["say hi\n"], container.Written);
        Assert.Equal("sayy\b \b hi\r\n", await ReadAvailableAsync(terminal));
    }

    [Fact]
    public async Task PastedLinesAreSentOnceEach()
    {
        var container = new FakeTerminal();
        await using var terminal = new LineTerminal(container, canWrite: true);

        await terminal.WriteAsync("status\r\nplayers\r\n"u8.ToArray(), CancellationToken.None);

        Assert.Equal(["status\n", "players\n"], container.Written);
    }

    [Fact]
    public async Task TheServersOutputIsShownWithCarriageReturns()
    {
        var container = new FakeTerminal();
        await using var terminal = new LineTerminal(container, canWrite: true);

        container.Output.Writer.TryWrite("got hi\n"u8.ToArray());

        Assert.Equal("got hi\r\n", await ReadAvailableAsync(terminal));
    }

    [Fact]
    public async Task AContainerWithoutInputSaysSoAndTakesNone()
    {
        var container = new FakeTerminal();
        await using var terminal = new LineTerminal(container, canWrite: false);

        await terminal.WriteAsync("quit\r"u8.ToArray(), CancellationToken.None);

        Assert.Empty(container.Written);
        Assert.Contains("input isn't open", await ReadAvailableAsync(terminal));
    }

    [Fact]
    public async Task TheSessionEndsWhenTheContainersDoes()
    {
        var container = new FakeTerminal();
        await using var terminal = new LineTerminal(container, canWrite: true);

        container.Output.Writer.Complete();

        Assert.Equal(0, await terminal.ReadAsync(new byte[64], CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    /// <summary>Everything the terminal has to show, once it has stopped coming.</summary>
    private static async Task<string> ReadAvailableAsync(IServerEngineTerminal terminal)
    {
        var text = new StringBuilder();
        var buffer = new byte[256];

        while (true)
        {
            using var quiet = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

            try
            {
                var read = await terminal.ReadAsync(buffer, quiet.Token);

                if (read == 0)
                    return text.ToString();

                text.Append(Encoding.UTF8.GetString(buffer, 0, read));
            }
            catch (OperationCanceledException)
            {
                return text.ToString();
            }
        }
    }

    private sealed class FakeTerminal : IServerEngineTerminal
    {
        public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();
        public List<string> Written { get; } = [];

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            if (!await Output.Reader.WaitToReadAsync(cancellationToken) || !Output.Reader.TryRead(out var bytes))
                return 0;

            bytes.CopyTo(buffer);

            return bytes.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
        {
            Written.Add(Encoding.UTF8.GetString(input.Span));

            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Output.Writer.TryComplete();

            return ValueTask.CompletedTask;
        }
    }
}
