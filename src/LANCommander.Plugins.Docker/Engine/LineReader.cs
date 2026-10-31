using System.Text;

namespace LANCommander.Plugins.Docker.Engine;

/// <summary>Splits a stream's bytes into lines as they arrive, decoding UTF-8 across reads.</summary>
internal sealed class LineReader(Action<string> line)
{
    private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
    private readonly StringBuilder _pending = new();
    private char[] _chars = new char[1024];

    public void Append(byte[] bytes, int count)
    {
        var needed = _decoder.GetCharCount(bytes, 0, count);

        if (needed > _chars.Length)
            _chars = new char[needed];

        var decoded = _decoder.GetChars(bytes, 0, count, _chars, 0);

        for (var i = 0; i < decoded; i++)
        {
            var c = _chars[i];

            if (c == '\n')
                Emit();
            else if (c != '\r')
                _pending.Append(c);
        }
    }

    public void Flush()
    {
        if (_pending.Length > 0)
            Emit();
    }

    private void Emit()
    {
        line(_pending.ToString());
        _pending.Clear();
    }
}
