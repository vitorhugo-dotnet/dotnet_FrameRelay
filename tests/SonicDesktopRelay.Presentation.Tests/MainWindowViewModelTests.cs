using SonicDesktopRelay.Media;
using SonicDesktopRelay.Signaling;
using Xunit;

namespace SonicDesktopRelay.Presentation.Tests;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public void A_new_window_opens_on_the_share_page()
    {
        var viewModel = new MainWindowViewModel();

        Assert.Equal(Page.Share, viewModel.CurrentPage);
    }

    [Fact]
    public void An_idle_app_can_start_either_role()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(SessionSnapshot.Idle);

        Assert.True(viewModel.CanShare);
        Assert.True(viewModel.CanWatch);
    }

    [Fact]
    public void While_sharing_neither_role_can_be_started_again()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Sharing, "AB12CD",
            Guid.NewGuid(), 2, SignalingState.Connected, null));

        Assert.False(viewModel.CanShare);
        Assert.False(viewModel.CanWatch);
    }

    [Fact]
    public void While_busy_neither_role_can_be_started()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Joining, null, null, 0,
            SignalingState.Connecting, null));

        Assert.False(viewModel.CanShare);
        Assert.False(viewModel.CanWatch);
    }

    [Fact]
    public void A_failure_is_reported_in_words_rather_than_as_an_error_code()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Failed, null, null, 0,
            SignalingState.Disconnected, "device_type_not_allowed"));

        Assert.Equal("This session only accepts Windows computers running SonicDesktopRelay.",
            viewModel.StatusText);
    }

    [Fact]
    public void An_invalid_code_is_reported_without_hinting_at_which_part_was_wrong()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Failed, null, null, 0,
            SignalingState.Disconnected, "invalid_code"));

        Assert.Equal("That code is not valid, or the session has ended.", viewModel.StatusText);
    }

    [Fact]
    public void A_media_failure_points_at_Diagnostics_where_the_reason_actually_is()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Failed, null, null, 0,
            SignalingState.Disconnected, "media_unavailable"));

        Assert.Equal("Screen capture or the video encoder could not start. See Diagnostics.",
            viewModel.StatusText);
    }

    [Fact]
    public void An_unrecognised_error_code_still_produces_a_usable_message()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Failed, null, null, 0,
            SignalingState.Disconnected, "something_new"));

        Assert.Equal("Something went wrong. Try again.", viewModel.StatusText);
    }

    [Fact]
    public void Sharing_reports_the_viewer_count()
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Sharing, "AB12CD",
            Guid.NewGuid(), 2, SignalingState.Connected, null));

        Assert.Equal("Sharing — 2 watching", viewModel.StatusText);
    }

    [Fact]
    public void Diagnostics_viewer_count_is_available_only_while_sharing()
    {
        var viewModel = new MainWindowViewModel();

        Assert.Equal("---", viewModel.DiagnosticsViewerCount);

        viewModel.Apply(new SessionSnapshot(SessionPhase.Sharing, "AB12CD",
            Guid.NewGuid(), 2, SignalingState.Connected, null));
        Assert.Equal("2", viewModel.DiagnosticsViewerCount);

        viewModel.Apply(new SessionSnapshot(SessionPhase.Watching, null,
            Guid.NewGuid(), 0, SignalingState.Connected, null, Watching: WatchState.Waiting));
        Assert.Equal("---", viewModel.DiagnosticsViewerCount);

        viewModel.Apply(SessionSnapshot.Idle);
        Assert.Equal("---", viewModel.DiagnosticsViewerCount);
    }

    [Fact]
    public void A_viewer_negotiation_failure_replaces_the_generic_waiting_message()
    {
        var viewModel = new MainWindowViewModel();
        const string failure = "WebRTC negotiation failed at setRemoteDescription: VideoIncompatible";

        viewModel.Apply(new SessionSnapshot(SessionPhase.Watching, null, Guid.NewGuid(), 0,
            SignalingState.Connected, failure, Watching: WatchState.Waiting));

        Assert.Equal(failure, viewModel.StatusText);
    }

    [Theory]
    [InlineData(WatchState.Waiting, "Connected — waiting for the first frame")]
    [InlineData(WatchState.Receiving, "Watching")]
    [InlineData(WatchState.Stalled, "Connected — video stalled, showing last received frame")]
    [InlineData(WatchState.Failed, "The picture could not be decoded")]
    public void A_viewer_is_told_what_the_media_is_doing(WatchState state, string expected)
    {
        var viewModel = new MainWindowViewModel();

        viewModel.Apply(new SessionSnapshot(SessionPhase.Watching, null, Guid.NewGuid(), 0,
            SignalingState.Connected, null, Watching: state));

        // A stall is not a disconnection. Saying "disconnected" would send the user to check
        // their network when the publisher's screen is the thing that has gone quiet.
        Assert.Equal(expected, viewModel.StatusText);
        Assert.DoesNotContain("isconnect", viewModel.StatusText);
    }
}
