using ScoreLite.Core;

// Exporterar MEI för varje .mscz i en mapp till en målmapp. Används av run.mjs (Verovio-spiken, V10).
string src = args[0], dst = args[1];
Directory.CreateDirectory(dst);
foreach (var f in Directory.EnumerateFiles(src, "*.mscz"))
{
    using var fs = File.OpenRead(f);
    var r = new ScoreLoader().Load(fs, Path.GetFileName(f));
    if (r.Score == null) { Console.Error.WriteLine("hoppar över " + f); continue; }
    string name = Path.GetFileNameWithoutExtension(f);
    File.WriteAllText(Path.Combine(dst, name + ".mei"), MeiExporter.Export(r.Score));
    File.WriteAllText(Path.Combine(dst, name + ".json"),
        System.Text.Json.JsonSerializer.Serialize(new { notes = r.Score.Notes.Count, measures = r.Score.Measures.Count, parts = r.Score.Parts.Count }));
}
