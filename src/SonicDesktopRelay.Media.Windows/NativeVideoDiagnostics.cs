namespace SonicDesktopRelay.Media.Windows;

/// <summary>
/// Credential-free projection of the native H.264 transform that is actually serving
/// the current media session. It never probes or activates a second codec instance.
/// </summary>
public sealed record NativeVideoDiagnostics(
    string Backend,
    string TransformName,
    Guid TransformClsid,
    bool IsHardware,
    string InputFormat,
    string OutputFormat,
    int Width,
    int Height,
    int FramesPerSecond,
    int Bitrate,
    IReadOnlyList<string> RejectionReasons)
{
    public string Acceleration => IsHardware ? "hardware" : "software";

    public override string ToString() =>
        $"{Backend}: {TransformName} ({Acceleration}), {InputFormat}->{OutputFormat}, " +
        $"{Width}x{Height}@{FramesPerSecond}, {Bitrate} bps";
}