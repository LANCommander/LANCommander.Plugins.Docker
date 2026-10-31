using System.Text;
using System.Threading.Channels;
using LANCommander.Server.Plugins;

namespace LANCommander.Plugins.Docker.Engine;

/// <summary>
/// Attaches to a container without a terminal of its own, whose server reads whole lines: the
/// administrator's line is echoed and edited here and sent when Enter is pressed, and the server's
/// bare line feeds are given the carriage returns a terminal needs.
/// </summary>
internal sealed class LineTerminal : IServerEngineTerminal
{
    private static readonly byte[] ReadOnlyNotice =
        "\x1b[2mThe container's input isn't open, so this console only shows what it writes.\x1b[0m\r\n"u8.ToArray();

    private readonly IServerEngineTerminal _inner;
    private readonly bool _canWrite;

    // The server's output and the echoes of what is typed, in the order they are to be shown
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _pump;

    private readonly SemaphoreSlim _inputLock = new(1, 1);
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _line = new();
    private bool _afterReturn;

    private byte[] _pending = [];
    private int _pendingOffset;

    public LineTerminal(IServerEngineTerminal inner, bool canWrite)
    {
        _inner = inner;
        _canWrite = canWrite;

        if (!canWrite)
            _output.Writer.TryWrite(ReadOnlyNotice);

        _pump = Task.Run(PumpAsync);
    }

    private async Task PumpAsync()
    {
        var buffer = new byte[8 * 1024];
        Exception? error = null;

        try
        {
            while (true)
            {
                var read = await _inner.ReadAsync(buffer, _closing.Token);

                if (read == 0)
                    break;

                _output.Writer.TryWrite(WithCarriageReturns(buffer.AsSpan(0, read)));
            }
        }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }

        _output.Writer.TryComplete(error);
    }

    /// <summary>A bare line feed moves a terminal's cursor down without taking it back to the left.</summary>
    internal static byte[] WithCarriageReturns(ReadOnlySpan<byte> output)
    {
        var converted = new List<byte>(output.Length + 16);

        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] == (byte)'\n' && (i == 0 || output[i - 1] != (byte)'\r'))
                converted.Add((byte)'\r');

            converted.Add(output[i]);
        }

        return converted.ToArray();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_pendingOffset >= _pending.Length)
        {
            if (!await _output.Reader.WaitToReadAsync(cancellationToken) || !_output.Reader.TryRead(out var next))
                return 0;

            _pending = next;
            _pendingOffset = 0;
        }

        var count = Math.Min(buffer.Length, _pending.Length - _pendingOffset);

        _pending.AsMemory(_pendingOffset, count).CopyTo(buffer);
        _pendingOffset += count;

        return count;
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> input, CancellationToken cancellationToken)
    {
        if (!_canWrite)
            return;

        await _inputLock.WaitAsync(cancellationToken);

        try
        {
            var chars = new char[_decoder.GetCharCount(input.Span, flush: false)];

            _decoder.GetChars(input.Span, chars, flush: false);

            foreach (var c in chars)
            {
                // Pasted text ends its lines with \r\n, which is one Enter, not two
                var afterReturn = _afterReturn;

                _afterReturn = c == '\r';

                if (c == '\n' && afterReturn)
                    continue;

                switch (c)
                {
                    case '\r':
                    case '\n':
                        var line = Encoding.UTF8.GetBytes(_line + "\n");

                        _line.Clear();
                        Echo("\r\n");

                        await _inner.WriteAsync(line, cancellationToken);
                        break;

                    case '\x7f':
                    case '\b':
                        if (_line.Length > 0)
                        {
                            _line.Length--;
                            Echo("\b \b");
                        }
                        break;

                    case '\x03':
                        _line.Clear();
                        Echo("^C\r\n");
                        break;

                    default:
                        if (!char.IsControl(c))
                        {
                            _line.Append(c);
                            Echo(c.ToString());
                        }
                        break;
                }
            }
        }
        finally
        {
            _inputLock.Release();
        }
    }

    private void Echo(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public async ValueTask DisposeAsync()
    {
        await _closing.CancelAsync();
        await _inner.DisposeAsync();

        try
        {
            await _pump;
        }
        catch (Exception)
        {
            // Reported to the reader, if it was still reading
        }

        _closing.Dispose();
    }
}
