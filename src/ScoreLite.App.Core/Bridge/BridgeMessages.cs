using System.Text.Json.Serialization;
using ScoreLite.Core;

namespace ScoreLite.App.Bridge;

/// <summary>IF-5. Varje meddelande har fälten <c>v</c> (protokollversion, nu 1) och <c>t</c> (typ).</summary>
public abstract record BridgeMessage
{
    public const int ProtocolVersion = 1;

    [JsonPropertyName("v")] public int V { get; init; } = ProtocolVersion;
    [JsonPropertyName("t")] public abstract string T { get; }
}

// ---------------------------------------------------------------- C# -> JS

public sealed record LoadScoreMessage(int RequestId, string Mei, int ViewportWidthPx, int ZoomLevel, string Theme) : BridgeMessage
{
    public override string T => "loadScore";
}

/// <summary><c>entries</c>: lista av <c>[playMs, x, sys]</c> sorterad på <c>playMs</c>.</summary>
public sealed record SetTimelineMessage(int LayoutGen, IReadOnlyList<double[]> Entries) : BridgeMessage
{
    public override string T => "setTimeline";
}

/// <summary><c>state</c>: stopped, countingIn, playing eller paused.</summary>
public sealed record SetClockMessage(int Epoch, string State, double PlayMs, double BridgeDelayMs) : BridgeMessage
{
    public override string T => "setClock";
}

public sealed record SetMarkerMessage(string AnchorId, int Measure, int TickInMeasure) : BridgeMessage
{
    public override string T => "setMarker";
}

public sealed record SetFollowMessage(bool Enabled) : BridgeMessage { public override string T => "setFollow"; }
public sealed record SetZoomMessage(int ZoomLevel) : BridgeMessage { public override string T => "setZoom"; }
public sealed record PingMessage(int Id) : BridgeMessage { public override string T => "ping"; }

// ---------------------------------------------------------------- JS -> C#

public sealed record ReadyMessage(string VerovioVersion) : BridgeMessage { public override string T => "ready"; }

public readonly record struct SystemGeometry(int Sys, double Y, double H);

public sealed record LayoutReadyMessage(int RequestId, int LayoutGen, IReadOnlyList<SystemGeometry> Systems,
    IReadOnlyList<LayoutItem> Items) : BridgeMessage
{
    public override string T => "layoutReady";
}

public sealed record TapMessage(string? Id, int Measure, double X) : BridgeMessage { public override string T => "tap"; }
public sealed record FollowChangedMessage(bool Enabled) : BridgeMessage { public override string T => "followChanged"; }
public sealed record PongMessage(int Id) : BridgeMessage { public override string T => "pong"; }
public sealed record RenderErrorMessage(string Code, string Message) : BridgeMessage { public override string T => "renderError"; }

/// <summary>Okänd <c>t</c>: ignoreras och loggas (IF-5).</summary>
public sealed record UnknownMessage(string Type) : BridgeMessage { public override string T => Type; }
