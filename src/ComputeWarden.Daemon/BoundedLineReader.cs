using System.Text;

namespace ComputeWarden.Daemon;

/// <summary>
/// Reads newline-delimited lines while enforcing a maximum length *as data arrives*.
/// <see cref="TextReader.ReadLineAsync()"/> buffers an entire line before the caller can check
/// its size, so a client that never sends a newline could grow memory without bound.
/// </summary>
public sealed class BoundedLineReader
{
    private readonly TextReader _reader;
    private readonly int _maxChars;
    private readonly char[] _buffer = new char[1024];
    private int _pos;
    private int _len;

    public BoundedLineReader(TextReader reader, int maxChars)
    {
        _reader = reader;
        _maxChars = maxChars;
    }

    /// <summary>
    /// Returns the next line (without its terminator), or null at end of stream. A line longer
    /// than the limit is consumed and discarded up to its newline — memory stays bounded, the
    /// sender isn't left blocked mid-write, and the stream stays in sync — and reported via
    /// <c>TooLong</c> with a null line. Use a cancellation token to bound a sender that never
    /// sends a newline.
    /// </summary>
    public async Task<(string? Line, bool TooLong)> ReadLineAsync(CancellationToken ct)
    {
        var line = new StringBuilder();
        var overflow = false;
        while (true)
        {
            if (_pos == _len)
            {
                _len = await _reader.ReadAsync(_buffer.AsMemory(), ct);
                _pos = 0;
                if (_len == 0)
                    return overflow ? (null, true) : (line.Length > 0 ? line.ToString() : null, false);
            }

            var newline = Array.IndexOf(_buffer, '\n', _pos, _len - _pos);
            var end = newline < 0 ? _len : newline;
            if (!overflow)
            {
                line.Append(_buffer, _pos, end - _pos);
                if (line.Length > _maxChars)
                {
                    overflow = true;
                    line.Clear();
                }
            }
            _pos = newline < 0 ? _len : newline + 1;

            if (newline >= 0)
            {
                if (overflow) return (null, true);
                if (line.Length > 0 && line[^1] == '\r') line.Length--;
                return (line.ToString(), false);
            }
        }
    }
}
