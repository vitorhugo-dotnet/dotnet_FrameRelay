using System.ComponentModel;
using System.Runtime.CompilerServices;
using SonicDesktopRelay.Media;

namespace SonicDesktopRelay.Presentation;

public enum Page
{
    Home,
    Share,
    Watch,
    Settings,
    Diagnostics
}

/// <summary>
/// The shell's projection of <see cref="SessionSnapshot"/>. It holds no state of its own
/// beyond the selected page: everything else is derived, so the UI cannot disagree with the
/// runtime about what is happening.
/// </summary>
public sealed class MainWindowViewModel : INotifyPropertyChanged
{
    private SessionSnapshot _snapshot = SessionSnapshot.Idle;
    private Page _currentPage = Page.Home;

    public event PropertyChangedEventHandler? PropertyChanged;

    public Page CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            Raise();
        }
    }

    /// <summary>The snapshot this view model is projecting, for callers that need the raw values.</summary>
    public SessionSnapshot Snapshot => _snapshot;

    public bool CanShare => _snapshot.Phase is SessionPhase.Idle or SessionPhase.Failed;

    public bool CanWatch => CanShare;

    public bool CanStop => _snapshot.Phase is SessionPhase.Sharing or SessionPhase.Watching;

    public string? Code => _snapshot.Code;

    public string StatusText => _snapshot.Phase switch
    {
        SessionPhase.Idle => "Ready",
        SessionPhase.Preparing => "Preparing to share…",
        SessionPhase.Sharing => $"Sharing — {_snapshot.ViewerCount} watching",
        SessionPhase.Joining => "Joining…",
        SessionPhase.Watching => _snapshot.Error ?? WatchingText(_snapshot.Watching),
        SessionPhase.Ending => "Ending…",
        SessionPhase.Failed => FailureText(_snapshot.Error),
        _ => "Ready"
    };

    public void Apply(SessionSnapshot snapshot)
    {
        _snapshot = snapshot;
        Raise(nameof(Snapshot));
        Raise(nameof(CanShare));
        Raise(nameof(CanWatch));
        Raise(nameof(CanStop));
        Raise(nameof(Code));
        Raise(nameof(StatusText));
    }

    /// <summary>
    /// What a viewer is told. A stall is never called a disconnection: the session and the
    /// peer connection can both be perfectly healthy while the picture has stopped, and the
    /// two have different causes and different fixes.
    /// </summary>
    private static string WatchingText(WatchState? state) => state switch
    {
        WatchState.Waiting => "Connected — waiting for the first frame",
        WatchState.Stalled => "Connected, but no picture is arriving",
        WatchState.Failed => "The picture could not be decoded",
        _ => "Watching"
    };

    // The API's codes are deliberately vague about *why* a code failed, and so is this: a
    // message that distinguished "expired" from "wrong" would help someone guessing codes.
    private static string FailureText(string? code) => code switch
    {
        "device_type_not_allowed" => "This session only accepts Windows computers running SonicDesktopRelay.",
        "invalid_code" => "That code is not valid, or the session has ended.",
        "not_paired" => "That code is not valid, or the session has ended.",
        "session_full" => "That session is already full.",
        // Deliberately vague in the UI and precise in Diagnostics: the reason is a codec name
        // or a missing DLL path, which means nothing on a Share button.
        "media_unavailable" => "Screen capture or the video encoder could not start. See Diagnostics.",
        _ => "Something went wrong. Try again."
    };

    private void Raise([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
