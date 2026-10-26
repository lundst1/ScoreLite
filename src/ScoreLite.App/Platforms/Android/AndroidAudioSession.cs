using Android.App;
using Android.Content;
using Android.Media;
using ScoreLite.App.Services;

namespace ScoreLite.App.Platforms.AndroidAudio;

/// <summary>
/// R4: ljudfokus och utdragna hörlurar. Förlorat fokus (samtal, annan app) och <c>ACTION_AUDIO_BECOMING_NOISY</c>
/// ger <see cref="Interrupted"/>, som <c>ScoreViewModel</c> svarar på med Pause.
/// </summary>
public sealed class AndroidAudioSession : Java.Lang.Object, IAudioInterruptions, AudioManager.IOnAudioFocusChangeListener
{
    private readonly AudioManager? _audio;
    private readonly Context _context;
    private AudioFocusRequestClass? _request;
    private NoisyReceiver? _receiver;

    public AndroidAudioSession()
    {
        _context = Android.App.Application.Context;
        _audio = _context.GetSystemService(Context.AudioService) as AudioManager;
        Acquire();
    }

    public event Action? Interrupted;

    private void Acquire()
    {
        if (_audio == null) return;
        var attrs = new AudioAttributes.Builder()!.SetUsage(AudioUsageKind.Media)!.SetContentType(AudioContentType.Music)!.Build()!;
        _request = new AudioFocusRequestClass.Builder(AudioFocus.Gain)!
            .SetAudioAttributes(attrs)!
            .SetOnAudioFocusChangeListener(this)!
            .Build();
        _audio.RequestAudioFocus(_request!);

        _receiver = new NoisyReceiver(() => Interrupted?.Invoke());
        _context.RegisterReceiver(_receiver, new IntentFilter(AudioManager.ActionAudioBecomingNoisy));
    }

    public void OnAudioFocusChange(AudioFocus focusChange)
    {
        // Allt utom att behålla eller återfå fokus pausar. Uppspelningen återupptas inte av sig själv.
        if (focusChange is AudioFocus.Loss or AudioFocus.LossTransient or AudioFocus.LossTransientCanDuck)
            Interrupted?.Invoke();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_receiver != null) { try { _context.UnregisterReceiver(_receiver); } catch (Java.Lang.IllegalArgumentException) { } _receiver.Dispose(); _receiver = null; }
            if (_audio != null && _request != null) _audio.AbandonAudioFocusRequest(_request);
        }
        base.Dispose(disposing);
    }

    private sealed class NoisyReceiver : BroadcastReceiver
    {
        private readonly Action _onNoisy;
        public NoisyReceiver(Action onNoisy) => _onNoisy = onNoisy;
        public override void OnReceive(Context? context, Intent? intent)
        {
            if (intent?.Action == AudioManager.ActionAudioBecomingNoisy) _onNoisy();
        }
    }
}
