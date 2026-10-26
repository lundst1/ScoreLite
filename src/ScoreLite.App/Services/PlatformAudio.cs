using ScoreLite.Audio;

namespace ScoreLite.App.Services;

/// <summary>Plattformens ljudutgång och avbrottskälla (IF-4, R4).</summary>
public static class PlatformAudio
{
    public static IAudioOutput CreateOutput()
    {
#if ANDROID
        return new Platforms.AndroidAudio.AndroidAudioOutput();
#elif IOS
        return new Platforms.IosAudio.IosAudioOutput();
#else
        throw new PlatformNotSupportedException();
#endif
    }

    public static IAudioInterruptions? CreateInterruptions()
    {
#if ANDROID
        return new Platforms.AndroidAudio.AndroidAudioSession();
#elif IOS
        return new Platforms.IosAudio.IosAudioSession();
#else
        return null;
#endif
    }
}
