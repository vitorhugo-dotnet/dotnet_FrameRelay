using SonicDesktopRelay.Signaling;

namespace SonicDesktopRelay.Presentation;

public enum SignalingDirection
{
    RX,
    TX
}

/// <summary>
/// Metadata-only trace of one signaling frame. Deliberately contains no signaling payload so
/// SDP, ICE candidates, credentials and media can never leak into the diagnostics stream.
/// </summary>
public sealed record SignalingDiagnosticEntry(
    DateTimeOffset Timestamp,
    SignalingDirection Direction,
    string Type,
    SessionPhase Phase,
    SignalingState Signaling,
    Guid? From,
    Guid? To,
    bool? Handled);

public sealed class SignalingDiagnosticBuffer
{
    public const int DefaultCapacity = 200;

    private readonly object _gate = new();
    private readonly Queue<SignalingDiagnosticEntry> _entries;
    private readonly int _capacity;

    public SignalingDiagnosticBuffer(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _entries = new Queue<SignalingDiagnosticEntry>(capacity);
    }

    public event Action<SignalingDiagnosticEntry>? Added;

    public IReadOnlyList<SignalingDiagnosticEntry> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Add(SignalingDiagnosticEntry entry)
    {
        Action<SignalingDiagnosticEntry>? added;
        lock (_gate)
        {
            if (_entries.Count == _capacity)
                _entries.Dequeue();

            _entries.Enqueue(entry);
            added = Added;
        }

        added?.Invoke(entry);
    }
}
