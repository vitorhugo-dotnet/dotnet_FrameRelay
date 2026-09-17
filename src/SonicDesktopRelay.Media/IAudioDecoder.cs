namespace SonicDesktopRelay.Media;

public interface IAudioDecoder : IDisposable
{
    string Name { get; }

    AudioFrame? Decode(EncodedAudioSample sample);
}
