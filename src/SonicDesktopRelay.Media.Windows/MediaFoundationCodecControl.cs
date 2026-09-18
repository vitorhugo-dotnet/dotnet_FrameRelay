using System.Runtime.InteropServices;

namespace SonicDesktopRelay.Media.Windows;

internal interface IMediaFoundationCodecControl
{
    bool TryForceNextKeyFrame();
}

internal enum EncoderKeyFrameAction
{
    None,
    CodecApi,
    ReconfigureFallback
}

/// <summary>
/// Coalesces encoder-level keyframe requests until the next input sample.
/// Recovery should use ICodecAPI when available; rebuilding the MFT is a correctness fallback only.
/// </summary>
internal sealed class EncoderKeyFramePolicy(IMediaFoundationCodecControl codecControl)
{
    private bool _pending;

    public void Request() => _pending = true;

    public EncoderKeyFrameAction BeforeNextInput()
    {
        if (!_pending)
            return EncoderKeyFrameAction.None;

        _pending = false;
        return codecControl.TryForceNextKeyFrame()
            ? EncoderKeyFrameAction.CodecApi
            : EncoderKeyFrameAction.ReconfigureFallback;
    }
}

/// <summary>
/// Minimal raw COM bridge for the one ICodecAPI operation FrameRelay needs.
/// Vortice.MediaFoundation 3.8.3 does not expose ICodecAPI publicly, so this keeps the native
/// vtable/VARIANT detail inside Media.Windows instead of leaking another interop package upward.
/// </summary>
internal sealed unsafe class MediaFoundationCodecControl : IMediaFoundationCodecControl, IDisposable
{
    // Windows SDK: ICodecAPI {901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA}.
    private static readonly Guid CodecApiInterfaceId =
        new("901DB4C7-31CE-41A2-85DC-8FA0BF41B8DA");

    // Windows SDK codecapi.h: CODECAPI_AVEncVideoForceKeyFrame.
    private static readonly Guid ForceKeyFrameProperty =
        new("398C1B98-8353-475A-9EF2-8F265D260345");

    // IUnknown occupies slots 0..2; ICodecAPI::SetValue is the seventh ICodecAPI method.
    private const int SetValueVtableIndex = 9;
    private const int S_OK = 0;

    private nint _codecApi;

    public MediaFoundationCodecControl(nint transform)
    {
        if (transform == 0)
            return;

        var iid = CodecApiInterfaceId;
        var result = Marshal.QueryInterface(transform, ref iid, out _codecApi);
        if (result < 0)
            _codecApi = 0;
    }

    public bool TryForceNextKeyFrame()
    {
        var codecApi = _codecApi;
        if (codecApi == 0)
            return false;

        var property = ForceKeyFrameProperty;
        var value = CodecApiVariant.FromUInt32(1);

        var vtable = *(nint**)codecApi;
        var setValue =
            (delegate* unmanaged[Stdcall]<nint, Guid*, CodecApiVariant*, int>)vtable[SetValueVtableIndex];

        // Microsoft documents S_FALSE as "read-only", so only S_OK means the request was accepted.
        return setValue(codecApi, &property, &value) == S_OK;
    }

    public void Dispose()
    {
        var codecApi = Interlocked.Exchange(ref _codecApi, 0);
        if (codecApi != 0)
            Marshal.Release(codecApi);
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct CodecApiVariant
    {
        [FieldOffset(0)]
        private ushort _type;

        [FieldOffset(8)]
        private uint _uint32;

        public static CodecApiVariant FromUInt32(uint value) => new()
        {
            _type = (ushort)VarEnum.VT_UI4,
            _uint32 = value
        };
    }
}
