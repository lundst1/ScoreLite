using ScoreLite.Core;

namespace ScoreLite.App.ViewModels;

/// <summary>§12.3. Felkod till meddelande för användaren.</summary>
public static class ErrorMessages
{
    public static string For(string code) => code switch
    {
        "NOT_A_SCORE" => "Filen verkar inte vara ett MuseScore-partitur.",
        "CORRUPT_ARCHIVE" => "Filen är skadad och kunde inte öppnas.",
        "LIMIT_EXCEEDED" => "Partituret är för stort för att öppnas i appen.",
        "EMPTY_SCORE" => "Partituret innehåller inga noter.",
        "UNSUPPORTED_VERSION" => "Den här filversionen stöds inte fullt ut. Vissa delar kan saknas.",
        "RENDER_FAILED" => "Notbilden kunde inte ritas. Uppspelning fungerar fortfarande.",
        "AUDIO_INIT_FAILED" or "SOUNDFONT_LOAD_FAILED" => "Ljudet kunde inte startas.",
        "PLAYORDER_FALLBACK" => "Repriser och hopp kunde inte tolkas. Stycket spelas rakt igenom.",
        "PLAYORDER_LIMIT" => "Repriser och hopp blev för många. Stycket spelas rakt igenom.",
        "UNKNOWN_ELEMENTS" => "Vissa delar av filen känns inte igen och hoppades över.",
        "TICK_ROUNDING" => "Vissa notlängder (tuplets) fick avrundade positioner.",
        "TRUNCATED_XML" => "Filen slutar oväntat. Det som hann läsas används.",
        _ => "Ett fel uppstod när partituret skulle öppnas.",
    };

    /// <summary>UI-9: vilka diagnostikposter som visas som varningar. CURSOR_MISSING_GEOMETRY visas aldrig.</summary>
    public static bool IsUserVisible(Diagnostic d) =>
        d.Code != "CURSOR_MISSING_GEOMETRY" && d.Code != "VOICE_LENGTH_MISMATCH" &&
        (d.Severity >= DiagSeverity.Warning || d.Code == "UNKNOWN_ELEMENTS");
}
