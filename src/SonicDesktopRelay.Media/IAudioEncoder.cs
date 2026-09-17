namespace SonicDesktopRelay.Media;

public interface IAudioEncoder : IDisposable
{
    string Name { get; }

    EncodedAudioSample? Encode(AudioFrame frame);
}
