namespace ScoreLite.Core;

/// <summary>IF-2. Fångar alla undantag och returnerar dem som <see cref="Diagnostic"/> (R1, P5).</summary>
public sealed class ScoreLoader : IScoreLoader
{
    private readonly LoadLimits _limits;

    public ScoreLoader(LoadLimits? limits = null) => _limits = limits ?? LoadLimits.Default;

    public Task<LoadResult> LoadAsync(Stream mscz, string? fileNameHint, CancellationToken ct) =>
        Task.Run(() => Load(mscz, fileNameHint, ct), ct);

    public LoadResult Load(Stream mscz, string? fileNameHint, CancellationToken ct = default)
    {
        var diagnostics = new List<Diagnostic>();
        try
        {
            using var source = MsczReader.Open(mscz, fileNameHint, _limits);
            ct.ThrowIfCancellationRequested();
            var parsed = MscxParser.Parse(source.Stream, diagnostics, _limits);
            ct.ThrowIfCancellationRequested();

            var order = PlayOrderBuilder.Build(parsed.Measures, parsed.Marks, diagnostics);
            var tempo = TempoMap.Build(order, parsed.Measures, parsed.TempoEvents);

            var score = new Score(parsed.Title, parsed.Parts, parsed.Measures, parsed.Marks, parsed.Notes,
                parsed.Items, parsed.Dynamics, parsed.TempoEvents, order, tempo, parsed.FormatVersion);
            return new LoadResult(score, diagnostics);
        }
        catch (OperationCanceledException) { throw; }
        catch (ScoreLoadException e)
        {
            diagnostics.Add(new Diagnostic(DiagSeverity.Error, e.Code, e.Message));
            return new LoadResult(null, diagnostics);
        }
        catch (OutOfMemoryException)
        {
            diagnostics.Add(new Diagnostic(DiagSeverity.Error, "LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen."));
            return new LoadResult(null, diagnostics);
        }
        catch (Exception e)
        {
            // R1: inget undantag når användaren som en krasch.
            diagnostics.Add(new Diagnostic(DiagSeverity.Error, "NOT_A_SCORE", "Filen verkar inte vara ett MuseScore-partitur.", e.GetType().Name));
            return new LoadResult(null, diagnostics);
        }
    }
}
