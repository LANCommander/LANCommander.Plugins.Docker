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
    public void EachLogFileIsAConsoleBesideTheContainersLog()
    {
        var consoles = ContainerConsoles.For(new DockerServerSettings
        {
            HostId = Guid.NewGuid(),
            ContainerId = "deusex",
            LogFiles =
            [
                new ContainerLogFile { Name = "Server log", Path = "/config/Overlay/System/server.log" },
                new ContainerLogFile { Name = "", Path = " /config/Overlay/System/ucc.log " },
                new ContainerLogFile { Name = "Twice", Path = "/config/Overlay/System/server.log" },
                new ContainerLogFile { Name = "No path", Path = "" },
            ],
        });

        Assert.Equal(
        [
            new ServerEngineConsole("log", "Container log", ServerEngineConsoleKind.Log),
            new ServerEngineConsole("file:/config/Overlay/System/server.log", "Server log", ServerEngineConsoleKind.Log),
            new ServerEngineConsole("file:/config/Overlay/System/ucc.log", "/config/Overlay/System/ucc.log", ServerEngineConsoleKind.Log),
            new ServerEngineConsole("attach", "Attach", ServerEngineConsoleKind.Terminal),
            new ServerEngineConsole("shell", "Shell", ServerEngineConsoleKind.Terminal),
        ], consoles);
    }

    [Fact]
    public void OnlyTheServersOwnLogFilesAreRead()
    {
        var consoles = new ContainerConsoles(TestHosts.Create());
        var settings = new DockerServerSettings { HostId = Guid.NewGuid(), ContainerId = "deusex", LogFiles = [new ContainerLogFile { Path = "/config/Overlay/System/server.log" }] };

        var error = Assert.Throws<LANCommander.Plugins.Docker.Hosts.DockerHostException>(() =>
            consoles.ReadLogAsync(settings, "file:/etc/shadow", new ServerEngineLogRequest(Follow: false), CancellationToken.None));

        Assert.Contains("isn't one of the server's log files", error.Message);
    }

    [Theory]
    [InlineData("one\ntwo\nthree\nfour\n", 2, new[] { "three", "four" })]
    [InlineData("one\r\ntwo", 5, new[] { "one", "two" })]
    [InlineData("", 5, new string[0])]
    [InlineData("one\ntwo", 0, new string[0])]
    public async Task TheEndOfAFileIsItsLastLines(string text, int tail, string[] lines)
    {
        var read = await ContainerConsoles.TailOfAsync(new MemoryStream(Encoding.UTF8.GetBytes(text)), tail);

        Assert.Equal(lines, read.Select(l => l.Text));
        Assert.All(read, l => Assert.False(l.IsError));
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
