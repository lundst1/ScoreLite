using ScoreLite.App.Bridge;

namespace ScoreLite.App.Services;

/// <summary>
/// IF-5 över <see cref="HybridWebView"/> (V11). C# -> JS: <c>SendRawMessage(json)</c>, som i JS tas emot som
/// händelsen <c>HybridWebViewMessageReceived</c> och vidarebefordras till <c>window.bridge.receive</c>.
/// JS -> C#: <c>HybridWebView.SendRawMessage(json)</c> blir <see cref="HybridWebView.RawMessageReceived"/>.
/// Data skickas alltid som JSON-text, aldrig som kod (S4).
/// </summary>
public sealed class HybridWebViewScoreBridge : JsonScoreBridge, IDisposable
{
    private readonly HybridWebView _view;

    public HybridWebViewScoreBridge(HybridWebView view)
    {
        _view = view;
        _view.RawMessageReceived += OnRaw;
    }

    // HybridWebView anropar RawMessageReceived på en bakgrundstråd (Androids shouldInterceptRequest), och SendRawMessage
    // kräver UI-tråden. Båda riktningarna går därför via huvudtråden (IScoreBridge: händelser på UI-tråden).
    protected override void Transport(string json)
    {
        if (MainThread.IsMainThread) _view.SendRawMessage(json);
        else MainThread.BeginInvokeOnMainThread(() => _view.SendRawMessage(json));
    }

    private void OnRaw(object? sender, HybridWebViewRawMessageReceivedEventArgs e)
    {
        string? message = e.Message;
        if (MainThread.IsMainThread) OnJsMessage(message);
        else MainThread.BeginInvokeOnMainThread(() => OnJsMessage(message));
    }

    public void Dispose() => _view.RawMessageReceived -= OnRaw;
}
