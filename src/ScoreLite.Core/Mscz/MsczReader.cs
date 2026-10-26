using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;

namespace ScoreLite.Core;

/// <summary>Gränser för indata (§12.1, S2). Värdena är [DEFAULT].</summary>
public sealed record LoadLimits
{
    public long MaxFileBytes { get; init; } = 50L * 1024 * 1024;
    public long MaxMscxBytes { get; init; } = 150L * 1024 * 1024;
    public int MaxCompressionRatio { get; init; } = 100;
    public int MaxEntries { get; init; } = 1000;
    public int MaxParts { get; init; } = 128;
    public int MaxMeasures { get; init; } = 5000;
    public int MaxNotes { get; init; } = 500_000;

    public static readonly LoadLimits Default = new();
}

/// <summary>Fatalt laddningsfel med felkod enligt §12.3.</summary>
public sealed class ScoreLoadException : Exception
{
    public string Code { get; }
    public ScoreLoadException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
}

/// <summary>Resultat av att öppna filen: en ström med själva .mscx-innehållet.</summary>
public sealed class MscxSource : IDisposable
{
    public required Stream Stream { get; init; }
    public required string EntryName { get; init; }
    public bool WasZip { get; init; }
    public void Dispose() => Stream.Dispose();
}

/// <summary>
/// AL-01 steg 1–2. Läser alltid i minnet (S1): ingenting packas upp till disk och postnamn används
/// aldrig som sökväg.
/// </summary>
public static class MsczReader
{
    public static MscxSource Open(Stream input, string? fileNameHint, LoadLimits? limits = null)
    {
        limits ??= LoadLimits.Default;
        byte[] data = ReadAllBounded(input, limits.MaxFileBytes);

        if (data.Length >= 4 && data[0] == (byte)'P' && data[1] == (byte)'K')
            return OpenZip(data, fileNameHint, limits);

        if (LooksLikeXml(data))
            return new MscxSource { Stream = new MemoryStream(data, writable: false), EntryName = fileNameHint ?? "score.mscx", WasZip = false };

        throw new ScoreLoadException("NOT_A_SCORE", "Filen verkar inte vara ett MuseScore-partitur.");
    }

    private static MscxSource OpenZip(byte[] data, string? fileNameHint, LoadLimits limits)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(new MemoryStream(data, writable: false), ZipArchiveMode.Read); }
        catch (Exception e) when (e is InvalidDataException or IOException or NotSupportedException)
        { throw new ScoreLoadException("CORRUPT_ARCHIVE", "Filen är skadad och kunde inte öppnas.", e); }

        if (zip.Entries.Count > limits.MaxEntries)
            throw new ScoreLoadException("LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen.");

        // Huvudfil: (a) container.xml, (b) <namn>.mscx, (c) score.mscx, (d) första .mscx i roten. Excerpts/ ignoreras (DR-29).
        ZipArchiveEntry? main = null;
        var rootMscx = zip.Entries.Where(e => IsRootMscx(e.FullName)).ToList();

        string? containerPath = TryReadContainerRootfile(zip);
        if (containerPath != null)
            main = zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, containerPath, StringComparison.Ordinal) && !IsExcerpt(e.FullName));

        if (main == null && !string.IsNullOrEmpty(fileNameHint))
        {
            string stem = Path.GetFileNameWithoutExtension(fileNameHint);
            main = rootMscx.FirstOrDefault(e => string.Equals(e.FullName, stem + ".mscx", StringComparison.OrdinalIgnoreCase));
        }
        main ??= rootMscx.FirstOrDefault(e => string.Equals(e.FullName, "score.mscx", StringComparison.OrdinalIgnoreCase));
        main ??= rootMscx.FirstOrDefault();

        if (main == null)
            throw new ScoreLoadException("NOT_A_SCORE", "Filen verkar inte vara ett MuseScore-partitur.");

        if (main.Length > limits.MaxMscxBytes ||
            (main.CompressedLength > 0 && main.Length / Math.Max(1, main.CompressedLength) > limits.MaxCompressionRatio))
            throw new ScoreLoadException("LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen.");

        // Läs posten med räknande gräns, eftersom ZIP-huvudets storlek inte får litas på (S2).
        using var es = main.Open();
        var ms = new MemoryStream();
        var buf = new byte[81920];
        long total = 0;
        int n;
        try
        {
            while ((n = es.Read(buf, 0, buf.Length)) > 0)
            {
                total += n;
                if (total > limits.MaxMscxBytes)
                    throw new ScoreLoadException("LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen.");
                ms.Write(buf, 0, n);
            }
        }
        catch (InvalidDataException e)
        { throw new ScoreLoadException("CORRUPT_ARCHIVE", "Filen är skadad och kunde inte öppnas.", e); }

        ms.Position = 0;
        return new MscxSource { Stream = ms, EntryName = main.FullName, WasZip = true };
    }

    private static bool IsExcerpt(string name) => name.StartsWith("Excerpts/", StringComparison.OrdinalIgnoreCase);

    private static bool IsRootMscx(string name) =>
        !name.Contains('/') && !name.Contains('\\') && name.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase);

    /// <summary>A4: <c>META-INF/container.xml</c> pekar ut huvudfilen via <c>rootfile full-path</c>.</summary>
    private static string? TryReadContainerRootfile(ZipArchive zip)
    {
        var e = zip.GetEntry("META-INF/container.xml");
        if (e == null || e.Length > 1_000_000) return null;
        try
        {
            using var s = e.Open();
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            using var xr = XmlReader.Create(s, settings);
            var doc = XDocument.Load(xr);
            // MuseScore 4 listar även stilfil, miniatyr och JSON som rootfile (score_style.mss först): ta första .mscx.
            string? p = doc.Descendants().Where(x => x.Name.LocalName == "rootfile")
                .Select(x => (string?)x.Attribute("full-path"))
                .FirstOrDefault(v => v != null && v.EndsWith(".mscx", StringComparison.OrdinalIgnoreCase));
            return string.IsNullOrWhiteSpace(p) ? null : p;
        }
        catch (XmlException) { return null; }
    }

    private static bool LooksLikeXml(byte[] data)
    {
        int i = 0;
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF) i = 3;
        while (i < data.Length && (data[i] == ' ' || data[i] == '\t' || data[i] == '\r' || data[i] == '\n')) i++;
        return i < data.Length && data[i] == (byte)'<';
    }

    private static byte[] ReadAllBounded(Stream input, long max)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = input.Read(buf, 0, buf.Length)) > 0)
        {
            total += n;
            if (total > max)
                throw new ScoreLoadException("LIMIT_EXCEEDED", "Partituret är för stort för att öppnas i appen.");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }
}
