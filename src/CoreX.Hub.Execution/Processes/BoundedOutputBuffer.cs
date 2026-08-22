using System.Text;

namespace CoreX.Hub.Execution.Processes;

/// <summary>
/// Captures stream output up to a ceiling while continuing to account for everything the
/// process produced.
///
/// The ceiling bounds memory only. The reader that feeds this buffer must keep draining
/// the operating system pipe after the ceiling is reached: stopping storage is fine,
/// stopping consumption reintroduces the deadlock this whole component exists to avoid
/// (Phase 2 brief section 16).
/// </summary>
internal sealed class BoundedOutputBuffer(int maxCharacters)
{
    private readonly StringBuilder _builder = new();
    private readonly int _maxCharacters = maxCharacters > 0
        ? maxCharacters
        : throw new ArgumentOutOfRangeException(nameof(maxCharacters));

    private long _observedCharacters;
    private bool _truncated;

    public long ObservedCharacters => Interlocked.Read(ref _observedCharacters);

    public bool Truncated => Volatile.Read(ref _truncated);

    public void Append(ReadOnlySpan<char> chunk)
    {
        if (chunk.IsEmpty)
        {
            return;
        }

        Interlocked.Add(ref _observedCharacters, chunk.Length);

        lock (_builder)
        {
            var remaining = _maxCharacters - _builder.Length;
            if (remaining <= 0)
            {
                Volatile.Write(ref _truncated, true);
                return;
            }

            if (chunk.Length > remaining)
            {
                _builder.Append(chunk[..remaining]);
                Volatile.Write(ref _truncated, true);
                return;
            }

            _builder.Append(chunk);
        }
    }

    public string ToText()
    {
        lock (_builder)
        {
            return _builder.ToString();
        }
    }
}
