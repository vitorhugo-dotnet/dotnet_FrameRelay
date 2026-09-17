namespace SonicDesktopRelay.Media.Windows;

public sealed record MediaFoundationTransformInfo(
    string Name,
    Guid Clsid,
    bool IsHardware);
