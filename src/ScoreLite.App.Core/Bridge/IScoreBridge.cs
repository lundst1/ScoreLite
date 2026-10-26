using ScoreLite.Core;

namespace ScoreLite.App.Bridge;

/// <summary>
/// §11.3. Tunt lager över WebView-meddelandena i IF-5. Metoderna skickar till JS och returnerar direkt.
/// Händelserna anropas på UI-tråden.
/// </summary>
public interface IScoreBridge
{
    void LoadScore(int requestId, string mei, int viewportWidthPx, int zoomLevel, string theme);
    void SetTimeline(int layoutGen, IReadOnlyList<CursorEntry> entries);
    void SetClock(int epoch, string state, double playMs, double bridgeDelayMs);
    void SetMarker(string anchorId, int measure, int tickInMeasure);
    void SetFollow(bool enabled);
    void SetZoom(int zoomLevel);
    void Ping(int id);

    event Action<ReadyMessage> Ready;
    event Action<LayoutReadyMessage> LayoutReady;
    event Action<TapMessage> Tap;
    event Action<FollowChangedMessage> FollowChanged;
    event Action<PongMessage> Pong;
    event Action<RenderErrorMessage> RenderError;
}

/// <summary>
/// Gemensam logik för riktiga bryggor: serialiserar utgående meddelanden och tolkar inkommande JSON.
/// Plattformsklassen anger bara transporten.
/// </summary>
public abstract class JsonScoreBridge : IScoreBridge
{
    /// <summary>Skickar en JSON-sträng till <c>window.bridge.receive</c> i WebView.</summary>
    protected abstract void Transport(string json);

    public event Action<ReadyMessage>? Ready;
    public event Action<LayoutReadyMessage>? LayoutReady;
    public event Action<TapMessage>? Tap;
    public event Action<FollowChangedMessage>? FollowChanged;
    public event Action<PongMessage>? Pong;
    public event Action<RenderErrorMessage>? RenderError;
    /// <summary>Okända eller trasiga meddelanden (loggas, IF-5).</summary>
    public event Action<string>? Ignored;

    public void LoadScore(int requestId, string mei, int viewportWidthPx, int zoomLevel, string theme) =>
        Send(new LoadScoreMessage(requestId, mei, viewportWidthPx, zoomLevel, theme));
    public void SetTimeline(int layoutGen, IReadOnlyList<CursorEntry> entries) =>
        Send(new SetTimelineMessage(layoutGen, entries.Select(e => e.ToArray()).ToList()));
    public void SetClock(int epoch, string state, double playMs, double bridgeDelayMs) =>
        Send(new SetClockMessage(epoch, state, playMs, bridgeDelayMs));
    public void SetMarker(string anchorId, int measure, int tickInMeasure) =>
        Send(new SetMarkerMessage(anchorId, measure, tickInMeasure));
    public void SetFollow(bool enabled) => Send(new SetFollowMessage(enabled));
    public void SetZoom(int zoomLevel) => Send(new SetZoomMessage(zoomLevel));
    public void Ping(int id) => Send(new PingMessage(id));

    private void Send(BridgeMessage m) => Transport(BridgeJson.Serialize(m));

    /// <summary>Anropas av plattformen med en JSON-sträng från JS (på UI-tråden).</summary>
    public void OnJsMessage(string? json)
    {
        var msg = BridgeJson.Parse(json);
        switch (msg)
        {
            case ReadyMessage m: Ready?.Invoke(m); break;
            case LayoutReadyMessage m: LayoutReady?.Invoke(m); break;
            case TapMessage m: Tap?.Invoke(m); break;
            case FollowChangedMessage m: FollowChanged?.Invoke(m); break;
            case PongMessage m: Pong?.Invoke(m); break;
            case RenderErrorMessage m: RenderError?.Invoke(m); break;
            case UnknownMessage u: Ignored?.Invoke("okänd typ: " + u.Type); break;
            default: Ignored?.Invoke("ogiltigt meddelande"); break;
        }
    }
}

/// <summary>Fejkbrygga för tester: spelar in allt som skickas och låter testet utlösa händelser från JS.</summary>
public sealed class FakeScoreBridge : IScoreBridge
{
    public readonly List<BridgeMessage> Sent = new();

    public event Action<ReadyMessage>? Ready;
    public event Action<LayoutReadyMessage>? LayoutReady;
    public event Action<TapMessage>? Tap;
    public event Action<FollowChangedMessage>? FollowChanged;
    public event Action<PongMessage>? Pong;
    public event Action<RenderErrorMessage>? RenderError;

    public void LoadScore(int requestId, string mei, int viewportWidthPx, int zoomLevel, string theme) =>
        Sent.Add(new LoadScoreMessage(requestId, mei, viewportWidthPx, zoomLevel, theme));
    public void SetTimeline(int layoutGen, IReadOnlyList<CursorEntry> entries) =>
        Sent.Add(new SetTimelineMessage(layoutGen, entries.Select(e => e.ToArray()).ToList()));
    public void SetClock(int epoch, string state, double playMs, double bridgeDelayMs) =>
        Sent.Add(new SetClockMessage(epoch, state, playMs, bridgeDelayMs));
    public void SetMarker(string anchorId, int measure, int tickInMeasure) =>
        Sent.Add(new SetMarkerMessage(anchorId, measure, tickInMeasure));
    public void SetFollow(bool enabled) => Sent.Add(new SetFollowMessage(enabled));
    public void SetZoom(int zoomLevel) => Sent.Add(new SetZoomMessage(zoomLevel));
    public void Ping(int id) => Sent.Add(new PingMessage(id));

    public void RaiseReady(string version = "test") => Ready?.Invoke(new ReadyMessage(version));
    public void RaiseLayoutReady(LayoutReadyMessage m) => LayoutReady?.Invoke(m);
    public void RaiseTap(string? id, int measure, double x) => Tap?.Invoke(new TapMessage(id, measure, x));
    public void RaiseFollowChanged(bool enabled) => FollowChanged?.Invoke(new FollowChangedMessage(enabled));
    public void RaisePong(int id) => Pong?.Invoke(new PongMessage(id));
    public void RaiseRenderError(string code, string message) => RenderError?.Invoke(new RenderErrorMessage(code, message));
}
