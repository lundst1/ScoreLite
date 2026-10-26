namespace ScoreLite.Audio;

/// <summary>
/// IF-4 / DR-06. Lås-fri ringbuffert med en skrivare (audio worker) och en läsare (driver-callback).
/// Skrivaren lämnar hela block märkta med epok. <see cref="Flush"/> höjer lägsta tillåtna epok; läsaren hoppar över
/// block med äldre epok. Vid epokbyte och när data kastas mjukas hoppet upp med en kort övertoning (declick, 64 ramar).
/// Läsaren allokerar aldrig och tar inga lås.
/// </summary>
public sealed class RingBuffer
{
    public const int DeclickFrames = 64;
    private const int MaxBlocks = 1024;

    private readonly float[] _data;
    private readonly int _capacity;
    private readonly int _channels;

    // Block-kö: skrivaren äger _qTail, läsaren _qHead.
    private readonly int[] _blockEpoch = new int[MaxBlocks];
    private readonly int[] _blockFrames = new int[MaxBlocks];
    private int _qHead, _qTail;

    private long _writeFrames;   // skrivaren
    private long _readFrames;    // läsaren (inklusive kasserade ramar)
    private int _minEpoch;       // skrivaren

    // Läsarens tillstånd
    private int _curEpoch;
    private long _framesInEpoch;
    private int _blockRemaining;
    private int _blockEpochCur;
    private bool _expectData;
    private long _underruns;
    private int _endOfStream;
    private int _fadeLeft;
    private readonly float[] _hold;
    private readonly float[] _last;

    public RingBuffer(int capacityFrames, int channels)
    {
        if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
        if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
        _capacity = capacityFrames;
        _channels = channels;
        _data = new float[capacityFrames * channels];
        _hold = new float[channels];
        _last = new float[channels];
    }

    public int Channels => _channels;
    public int CapacityFrames => _capacity;

    /// <summary>Lediga ramar för skrivaren. 0 om blockkön är full.</summary>
    public int WritableFrames
    {
        get
        {
            int queued = Volatile.Read(ref _qTail) - Volatile.Read(ref _qHead);
            if (queued >= MaxBlocks) return 0;
            long used = Volatile.Read(ref _writeFrames) - Volatile.Read(ref _readFrames);
            return (int)(_capacity - used);
        }
    }

    public int CurrentEpoch => Volatile.Read(ref _curEpoch);
    public long FramesInCurrentEpoch => Interlocked.Read(ref _framesInEpoch);
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Skrivaren. Ett block per anrop. Returnerar false om det inte får plats.</summary>
    public bool TryWrite(ReadOnlySpan<float> interleaved, int epoch)
    {
        int frames = interleaved.Length / _channels;
        if (frames == 0) return true;
        if (frames > WritableFrames) return false;
        Volatile.Write(ref _endOfStream, 0);

        int tail = _qTail;
        int pos = (int)(_writeFrames % _capacity);
        int first = Math.Min(frames, _capacity - pos);
        interleaved.Slice(0, first * _channels).CopyTo(_data.AsSpan(pos * _channels));
        if (first < frames) interleaved.Slice(first * _channels).CopyTo(_data.AsSpan(0));

        _blockEpoch[tail % MaxBlocks] = epoch;
        _blockFrames[tail % MaxBlocks] = frames;
        Volatile.Write(ref _qTail, tail + 1);                          // publicera blocket
        Volatile.Write(ref _writeFrames, _writeFrames + frames);
        return true;
    }

    /// <summary>Skrivaren. Inga fler block kommer i den här epoken: tystnad efter det sista blocket är inget underskott.</summary>
    public void CompleteEpoch() => Volatile.Write(ref _endOfStream, 1);

    /// <summary>Skrivaren. Kasta allt buffrat: läsaren hoppar block med epok &lt; <paramref name="newEpoch"/>.</summary>
    public void Flush(int newEpoch)
    {
        Volatile.Write(ref _endOfStream, 0);
        Volatile.Write(ref _minEpoch, newEpoch);
    }

    /// <summary>Läsaren (driver). Fyller <paramref name="dest"/> och tystnad vid underskott. Anrop allokerar inte.</summary>
    public int Read(Span<float> dest, out int epoch)
    {
        int ch = _channels;
        int frames = dest.Length / ch;
        int done = 0;
        int fadeFrom = _fadeLeft > 0 ? 0 : frames;
        int minEpoch = Volatile.Read(ref _minEpoch);

        while (done < frames)
        {
            if (_blockRemaining == 0)
            {
                int head = _qHead;
                if (head == Volatile.Read(ref _qTail)) break;           // tomt
                int e = _blockEpoch[head % MaxBlocks];
                int f = _blockFrames[head % MaxBlocks];
                if (e < Volatile.Read(ref _minEpoch))
                {
                    Discard(f);                                        // helt block är gammalt
                    _qHead = head + 1;
                    BeginFade(dest, done, ref fadeFrom);
                    _expectData = false;
                    continue;
                }
                if (e != _curEpoch)
                {
                    BeginFade(dest, done, ref fadeFrom);
                    Volatile.Write(ref _curEpoch, e);
                    Interlocked.Exchange(ref _framesInEpoch, 0);
                }
                _expectData = true;
                _blockEpochCur = e;
                _blockRemaining = f;
            }
            else if (_blockEpochCur < Volatile.Read(ref _minEpoch))
            {
                // Resten av pågående block hör till en kasserad epok.
                Discard(_blockRemaining);
                _blockRemaining = 0;
                _qHead++;
                BeginFade(dest, done, ref fadeFrom);
                _expectData = false;
                continue;
            }

            int n = Math.Min(_blockRemaining, frames - done);
            int rpos = (int)(_readFrames % _capacity);
            n = Math.Min(n, _capacity - rpos);                          // en sammanhängande bit
            _data.AsSpan(rpos * ch, n * ch).CopyTo(dest.Slice(done * ch, n * ch));
            Volatile.Write(ref _readFrames, _readFrames + n);
            Interlocked.Add(ref _framesInEpoch, n);
            _blockRemaining -= n;
            done += n;
            if (_blockRemaining == 0) _qHead++;
        }

        if (done < frames)
        {
            dest.Slice(done * ch).Clear();
            if (_expectData && minEpoch <= _curEpoch && Volatile.Read(ref _endOfStream) == 0) Interlocked.Increment(ref _underruns);
        }

        // Övertoning: ut med föregående signal, in med den nya.
        for (int i = fadeFrom; i < frames && _fadeLeft > 0; i++, _fadeLeft--)
        {
            float d = _fadeLeft / (float)DeclickFrames;
            for (int c = 0; c < ch; c++)
                dest[i * ch + c] = dest[i * ch + c] * (1f - d) + _hold[c] * d;
        }
        if (frames > 0)
            for (int c = 0; c < ch; c++) _last[c] = dest[(frames - 1) * ch + c];

        epoch = _curEpoch;
        return frames;
    }

    private void Discard(int frames) => Volatile.Write(ref _readFrames, _readFrames + frames);

    private void BeginFade(Span<float> dest, int doneFrames, ref int fadeFrom)
    {
        for (int c = 0; c < _channels; c++)
            _hold[c] = doneFrames > 0 ? dest[(doneFrames - 1) * _channels + c] : _last[c];
        bool silent = true;
        for (int c = 0; c < _channels; c++) if (_hold[c] != 0f) silent = false;
        if (silent) return;   // ingen föregående signal att blanda från: ingen övertoning behövs
        _fadeLeft = DeclickFrames;
        fadeFrom = doneFrames;
    }
}
