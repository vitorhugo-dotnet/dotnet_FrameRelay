using SonicDesktopRelay.Media.Windows;
using Windows.Graphics.Capture;
using Windows.Security.Authorization.AppCapabilityAccess;

namespace SonicDesktopRelay.Media.Windows.Tests;

public sealed class BorderlessCapturePolicyTests
{
    [Fact]
    public async Task Grant_disables_border()
    {
        var platform = new FakeBorderlessCapturePlatform
        {
            IsAvailable = true,
            AccessStatus = AppCapabilityAccessStatus.Allowed
        };

        var result = await new BorderlessCapturePolicy(platform)
            .TryEnableAsync(null!, CancellationToken.None);

        Assert.True(result.IsEnabled);
        Assert.Equal("granted", result.Outcome);
        Assert.Equal(1, platform.RequestCount);
        Assert.Equal(new[] { false }, platform.RequiredValues);
    }

    [Fact]
    public async Task Denial_keeps_default_border()
    {
        var platform = new FakeBorderlessCapturePlatform
        {
            IsAvailable = true,
            AccessStatus = AppCapabilityAccessStatus.DeniedByUser
        };

        var result = await new BorderlessCapturePolicy(platform)
            .TryEnableAsync(null!, CancellationToken.None);

        Assert.False(result.IsEnabled);
        Assert.Equal("denied", result.Outcome);
        Assert.Equal("Borderless capture access was denied by the user.", result.Reason);
        Assert.Equal(1, platform.RequestCount);
        Assert.Empty(platform.RequiredValues);
    }

    [Fact]
    public async Task Missing_package_capability_is_reported_as_a_declaring_problem()
    {
        var platform = new FakeBorderlessCapturePlatform
        {
            IsAvailable = true,
            AccessStatus = AppCapabilityAccessStatus.NotDeclaredByApp
        };

        var result = await new BorderlessCapturePolicy(platform)
            .TryEnableAsync(null!, CancellationToken.None);

        Assert.False(result.IsEnabled);
        Assert.Equal("denied", result.Outcome);
        Assert.Equal("The package manifest does not declare graphicsCaptureWithoutBorder.", result.Reason);
        Assert.Empty(platform.RequiredValues);
    }

    [Fact]
    public async Task Unsupported_api_skips_access_request()
    {
        var platform = new FakeBorderlessCapturePlatform { IsAvailable = false };

        var result = await new BorderlessCapturePolicy(platform)
            .TryEnableAsync(null!, CancellationToken.None);

        Assert.False(result.IsEnabled);
        Assert.Equal("unsupported", result.Outcome);
        Assert.Equal(0, platform.RequestCount);
        Assert.Empty(platform.RequiredValues);
    }

    [Fact]
    public async Task Access_request_failure_falls_back_with_reason()
    {
        var platform = new FakeBorderlessCapturePlatform
        {
            IsAvailable = true,
            RequestException = new InvalidOperationException("package capability unavailable")
        };

        var result = await new BorderlessCapturePolicy(platform)
            .TryEnableAsync(null!, CancellationToken.None);

        Assert.False(result.IsEnabled);
        Assert.Equal("request_failed", result.Outcome);
        Assert.Equal("package capability unavailable", result.Reason);
        Assert.Empty(platform.RequiredValues);
    }

    [Fact]
    public async Task Setting_border_property_failure_falls_back_with_reason()
    {
        var platform = new FakeBorderlessCapturePlatform
        {
            IsAvailable = true,
            AccessStatus = AppCapabilityAccessStatus.Allowed,
            SetBorderException = new InvalidOperationException("property unavailable")
        };

        var result = await new BorderlessCapturePolicy(platform)
            .TryEnableAsync(null!, CancellationToken.None);

        Assert.False(result.IsEnabled);
        Assert.Equal("apply_failed", result.Outcome);
        Assert.Equal("property unavailable", result.Reason);
        Assert.Equal(new[] { false }, platform.RequiredValues);
    }

    [Fact]
    public async Task Cancellation_while_request_is_pending_is_propagated()
    {
        var pendingRequest = new TaskCompletionSource<AppCapabilityAccessStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var platform = new FakeBorderlessCapturePlatform
        {
            IsAvailable = true,
            PendingRequest = pendingRequest.Task
        };
        using var cancellation = new CancellationTokenSource();
        var policy = new BorderlessCapturePolicy(platform);

        var start = policy.TryEnableAsync(null!, cancellation.Token);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => start);
        Assert.Empty(platform.RequiredValues);
    }

    private sealed class FakeBorderlessCapturePlatform : IBorderlessCapturePlatform
    {
        public bool IsAvailable { get; init; }
        public AppCapabilityAccessStatus AccessStatus { get; init; } = AppCapabilityAccessStatus.Allowed;
        public Exception? RequestException { get; init; }
        public Exception? SetBorderException { get; init; }
        public Task<AppCapabilityAccessStatus>? PendingRequest { get; init; }
        public int RequestCount { get; private set; }
        public List<bool> RequiredValues { get; } = [];

        public Task<AppCapabilityAccessStatus> RequestAccessAsync()
        {
            RequestCount++;
            if (RequestException is not null) throw RequestException;
            return PendingRequest ?? Task.FromResult(AccessStatus);
        }

        public void SetBorderRequired(GraphicsCaptureSession session, bool required)
        {
            RequiredValues.Add(required);
            if (SetBorderException is not null) throw SetBorderException;
        }
    }
}
