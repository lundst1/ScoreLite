using System.Text;

/// <summary>
/// Bygger en minimal SF2 i minnet: en looppad sinus (100 ramar per period vid 44100 Hz, alltså 441 Hz för tangent 69)
/// som preset 0 i bank 0 och bank 128. Gör att ljudtesterna inte behöver en riktig ljudfont (K14).
/// </summary>
public static class TestSoundFont
{
    public const double Hz69 = 441.0;
    public static double FreqOf(int key) => Hz69 * Math.Pow(2, (key - 69) / 12.0);

    public static byte[] Build()
    {
        const int period = 100, periods = 4, n = period * periods, pad = 46;
        var smpl = new byte[(n + pad) * 2];
        for (int i = 0; i < n; i++)
        {
            short v = (short)(Math.Sin(2 * Math.PI * (i % period) / period) * 20000);
            BitConverter.GetBytes(v).CopyTo(smpl, i * 2);
        }

        byte[] Info()
        {
            var ms = new MemoryStream();
            Chunk(ms, "ifil", new byte[] { 2, 0, 1, 0 });
            Chunk(ms, "isng", Z("EMU8000"));
            Chunk(ms, "INAM", Z("ScoreLite test sine"));
            return List("INFO", ms.ToArray());
        }
        byte[] Sdta() { var ms = new MemoryStream(); Chunk(ms, "smpl", smpl); return List("sdta", ms.ToArray()); }

        byte[] Pdta()
        {
            var ms = new MemoryStream();
            // Presets: (bank 0, program 0) och (bank 128, program 0), båda med en zon mot instrument 0.
            var phdr = new MemoryStream();
            Phdr(phdr, "Sine", 0, 0, 0); Phdr(phdr, "Sine drums", 0, 128, 1); Phdr(phdr, "EOP", 0, 0, 2);
            Chunk(ms, "phdr", phdr.ToArray());
            Chunk(ms, "pbag", Words(0, 0, 1, 0, 2, 0));
            Chunk(ms, "pmod", new byte[10]);
            Chunk(ms, "pgen", Words(41, 0, 41, 0, 0, 0));
            var inst = new MemoryStream();
            Name(inst, "Sine"); inst.Write(Words(0)); Name(inst, "EOI"); inst.Write(Words(1));
            Chunk(ms, "inst", inst.ToArray());
            Chunk(ms, "ibag", Words(0, 0, 2, 0));
            Chunk(ms, "imod", new byte[10]);
            Chunk(ms, "igen", Words(54, 1, 53, 0, 0, 0));   // sampleModes = loop, sampleID = 0
            var shdr = new MemoryStream();
            Shdr(shdr, "sine", 0, n, 0, n, 44100, 69);
            Shdr(shdr, "EOS", 0, 0, 0, 0, 0, 0);
            Chunk(ms, "shdr", shdr.ToArray());
            return List("pdta", ms.ToArray());
        }

        var body = new MemoryStream();
        body.Write(Encoding.ASCII.GetBytes("sfbk"));
        body.Write(Info()); body.Write(Sdta()); body.Write(Pdta());
        var file = new MemoryStream();
        file.Write(Encoding.ASCII.GetBytes("RIFF"));
        file.Write(BitConverter.GetBytes((int)body.Length));
        file.Write(body.ToArray());
        return file.ToArray();
    }

    private static byte[] Z(string s) { var b = Encoding.ASCII.GetBytes(s + "\0"); return b.Length % 2 == 0 ? b : b.Concat(new byte[] { 0 }).ToArray(); }

    private static void Chunk(Stream s, string id, byte[] data)
    {
        s.Write(Encoding.ASCII.GetBytes(id));
        s.Write(BitConverter.GetBytes(data.Length));
        s.Write(data);
        if (data.Length % 2 == 1) s.WriteByte(0);
    }

    private static byte[] List(string type, byte[] content)
    {
        var ms = new MemoryStream();
        ms.Write(Encoding.ASCII.GetBytes("LIST"));
        ms.Write(BitConverter.GetBytes(content.Length + 4));
        ms.Write(Encoding.ASCII.GetBytes(type));
        ms.Write(content);
        return ms.ToArray();
    }

    private static byte[] Words(params int[] w)
    {
        var b = new byte[w.Length * 2];
        for (int i = 0; i < w.Length; i++) BitConverter.GetBytes((ushort)w[i]).CopyTo(b, i * 2);
        return b;
    }

    private static void Name(Stream s, string name)
    {
        var b = new byte[20];
        Encoding.ASCII.GetBytes(name).CopyTo(b, 0);
        s.Write(b);
    }

    private static void Phdr(Stream s, string name, int preset, int bank, int bagIndex)
    {
        Name(s, name);
        s.Write(Words(preset, bank, bagIndex));
        s.Write(new byte[12]);   // library, genre, morphology
    }

    private static void Shdr(Stream s, string name, int start, int end, int loopStart, int loopEnd, int rate, int pitch)
    {
        Name(s, name);
        s.Write(BitConverter.GetBytes(start)); s.Write(BitConverter.GetBytes(end));
        s.Write(BitConverter.GetBytes(loopStart)); s.Write(BitConverter.GetBytes(loopEnd));
        s.Write(BitConverter.GetBytes(rate));
        s.WriteByte((byte)pitch); s.WriteByte(0);   // originalPitch, pitchCorrection
        s.Write(Words(0, 1));                        // sampleLink, sampleType (mono)
    }
}
