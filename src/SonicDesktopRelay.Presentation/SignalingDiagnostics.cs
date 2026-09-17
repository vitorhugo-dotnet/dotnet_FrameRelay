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
    private readonly object _gate = new();
    private readonly List<SignalingDiagnosticEntry> _entries = [];

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
            _entries.Add(entry);
            added = Added;
        }

        added?.Invoke(entry);
    }
}
