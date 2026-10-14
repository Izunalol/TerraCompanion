namespace TerraCompanion.Core.Assets;

/// <summary>
/// LZX decompressor (Microsoft's LZ77 + Huffman format) as used by XNA's compressed .xnb files.
/// Written from the published format description. Decoder state (window, trees, repeated offsets)
/// persists across frames, exactly like the reference implementation.
/// </summary>
public sealed class LzxDecoder
{
    const int MinMatch = 2;
    const int NumChars = 256;
    const int BlockVerbatim = 1, BlockAligned = 2, BlockUncompressed = 3;
    const int PretreeSymbols = 20, AlignedSymbols = 8, NumSecondaryLengths = 249;

    static readonly int[] ExtraBits = new int[52];
    static readonly int[] PositionBase = new int[52];

    static LzxDecoder()
    {
        for (int i = 0, j = 0; i < 52; i += 2)
        {
            ExtraBits[i] = j; ExtraBits[i + 1] = j;
            if (i != 0 && j < 17) j++;
        }
        for (int i = 0, j = 0; i < 52; i++) { PositionBase[i] = j; j += 1 << ExtraBits[i]; }
    }

    readonly byte[] _window;
    readonly int _windowSize;
    readonly int _mainElements;
    int _windowPos;
    uint _r0 = 1, _r1 = 1, _r2 = 1;
    bool _headerRead;
    int _blockType, _blockRemaining, _blockLength;
    int _intelFileSize, _framesRead;
    bool _intelStarted;
    int _overrun; // bytes a match produced past the previous frame's end

    readonly byte[] _mainLens, _lengthLens, _alignedLens = new byte[AlignedSymbols], _preLens = new byte[PretreeSymbols];
    readonly Huffman _main = new(), _length = new(), _aligned = new(), _pre = new();

    // bit reader state (reset at the start of every frame)
    byte[] _in = Array.Empty<byte>();
    int _inPos, _inEnd;
    uint _bitBuf;
    int _bitsLeft;

    public LzxDecoder(int windowBits = 16)
    {
        if (windowBits < 15 || windowBits > 21) throw new ArgumentOutOfRangeException(nameof(windowBits));
        _windowSize = 1 << windowBits;
        _window = new byte[_windowSize];
        int posnSlots = windowBits == 20 ? 42 : windowBits == 21 ? 50 : windowBits << 1;
        _mainElements = NumChars + (posnSlots << 3);
        _mainLens = new byte[_mainElements];
        _lengthLens = new byte[NumSecondaryLengths + 1];
    }

    // ------------------------------------------------------------ bit reading

    void InitBits() { _bitBuf = 0; _bitsLeft = 0; }

    void EnsureBits(int n)
    {
        while (_bitsLeft < n)
        {
            int lo = _inPos < _inEnd ? _in[_inPos] : 0;
            int hi = _inPos + 1 < _inEnd ? _in[_inPos + 1] : 0;
            _inPos += 2;
            _bitBuf |= (uint)((hi << 8) | lo) << (16 - _bitsLeft);
            _bitsLeft += 16;
        }
    }

    uint PeekBits(int n) => _bitBuf >> (32 - n);
    void RemoveBits(int n) { _bitBuf <<= n; _bitsLeft -= n; }

    uint ReadBits(int n)
    {
        if (n == 0) return 0;
        EnsureBits(n);
        uint v = PeekBits(n);
        RemoveBits(n);
        return v;
    }

    int ReadSym(Huffman h)
    {
        EnsureBits(16);
        int code = (int)PeekBits(16);
        int len = h.Len[code];
        if (len == 0) throw new InvalidDataException("LZX: invalid Huffman code.");
        RemoveBits(len);
        return h.Sym[code];
    }

    // ------------------------------------------------------------ huffman tables

    sealed class Huffman
    {
        // Direct 16-bit lookup: simple and robust, LZX code lengths never exceed 16.
        public readonly ushort[] Sym = new ushort[1 << 16];
        public readonly byte[] Len = new byte[1 << 16];

        public void Build(byte[] lengths, int count)
        {
            Array.Clear(Len);
            int code = 0;
            for (int bitLen = 1; bitLen <= 16; bitLen++)
            {
                for (int sym = 0; sym < count; sym++)
                {
                    if (lengths[sym] != bitLen) continue;
                    int fill = 1 << (16 - bitLen);
                    int start = code << (16 - bitLen);
                    if (start + fill > 1 << 16) throw new InvalidDataException("LZX: oversubscribed Huffman table.");
                    for (int i = 0; i < fill; i++) { Sym[start + i] = (ushort)sym; Len[start + i] = (byte)bitLen; }
                    code++;
                }
                code <<= 1;
            }
        }
    }

    void ReadLengths(byte[] lens, int first, int last)
    {
        for (int i = 0; i < PretreeSymbols; i++) _preLens[i] = (byte)ReadBits(4);
        _pre.Build(_preLens, PretreeSymbols);
        for (int x = first; x < last;)
        {
            int z = ReadSym(_pre);
            if (z == 17)
            {
                int y = (int)ReadBits(4) + 4;
                while (y-- > 0 && x < last) lens[x++] = 0;
            }
            else if (z == 18)
            {
                int y = (int)ReadBits(5) + 20;
                while (y-- > 0 && x < last) lens[x++] = 0;
            }
            else if (z == 19)
            {
                int y = (int)ReadBits(1) + 4;
                z = ReadSym(_pre);
                z = lens[x] - z; if (z < 0) z += 17;
                while (y-- > 0 && x < last) lens[x++] = (byte)z;
            }
            else
            {
                z = lens[x] - z; if (z < 0) z += 17;
                lens[x++] = (byte)z;
            }
        }
    }

    // ------------------------------------------------------------ decoding

    /// <summary>Decompresses one frame: <paramref name="inLen"/> bytes of input into <paramref name="outLen"/> bytes of output.</summary>
    public void Decompress(byte[] input, int inPos, int inLen, byte[] output, int outPos, int outLen)
    {
        _in = input; _inPos = inPos; _inEnd = inPos + inLen;
        InitBits();

        int frameStart = (_windowPos - _overrun) & (_windowSize - 1);

        if (!_headerRead)
        {
            if (ReadBits(1) != 0)
            {
                uint hi = ReadBits(16), lo = ReadBits(16);
                _intelFileSize = (int)((hi << 16) | lo);
            }
            _headerRead = true;
        }

        int togo = outLen - _overrun;
        _overrun = 0;
        while (togo > 0)
        {
            if (_blockRemaining == 0)
            {
                if (_blockType == BlockUncompressed)
                {
                    if ((_blockLength & 1) == 1) _inPos++; // padding byte after odd-sized raw blocks
                    InitBits();
                }
                _blockType = (int)ReadBits(3);
                uint hi = ReadBits(16), lo = ReadBits(8);
                _blockRemaining = _blockLength = (int)((hi << 8) | lo);

                switch (_blockType)
                {
                    case BlockAligned:
                        for (int i = 0; i < AlignedSymbols; i++) _alignedLens[i] = (byte)ReadBits(3);
                        _aligned.Build(_alignedLens, AlignedSymbols);
                        goto case BlockVerbatim;
                    case BlockVerbatim:
                        ReadLengths(_mainLens, 0, NumChars);
                        ReadLengths(_mainLens, NumChars, _mainElements);
                        _main.Build(_mainLens, _mainElements);
                        if (_mainLens[0xE8] != 0) _intelStarted = true;
                        ReadLengths(_lengthLens, 0, NumSecondaryLengths);
                        _length.Build(_lengthLens, NumSecondaryLengths + 1);
                        break;
                    case BlockUncompressed:
                        _intelStarted = true;
                        EnsureBits(16);
                        if (_bitsLeft > 16) _inPos -= 2; // give back the word we read ahead
                        _bitBuf = 0; _bitsLeft = 0;
                        _r0 = ReadRawU32(); _r1 = ReadRawU32(); _r2 = ReadRawU32();
                        break;
                    default:
                        throw new InvalidDataException($"LZX: bad block type {_blockType}.");
                }
            }

            int thisRun = Math.Min(_blockRemaining, togo);
            togo -= thisRun;
            _blockRemaining -= thisRun;
            _windowPos &= _windowSize - 1;

            if (_blockType == BlockUncompressed)
            {
                for (int i = 0; i < thisRun; i++)
                {
                    _window[_windowPos] = _inPos < _inEnd ? _in[_inPos] : (byte)0;
                    _inPos++;
                    _windowPos = (_windowPos + 1) & (_windowSize - 1);
                }
                continue;
            }

            while (thisRun > 0)
            {
                int mainElement = ReadSym(_main);
                if (mainElement < NumChars)
                {
                    _window[_windowPos] = (byte)mainElement;
                    _windowPos = (_windowPos + 1) & (_windowSize - 1);
                    thisRun--;
                    continue;
                }
                mainElement -= NumChars;
                int matchLength = mainElement & 7;
                if (matchLength == 7) matchLength += ReadSym(_length);
                matchLength += MinMatch;

                uint matchOffset = (uint)(mainElement >> 3);
                if (matchOffset > 2)
                {
                    if (_blockType == BlockVerbatim)
                    {
                        if (matchOffset != 3)
                        {
                            int extra = ExtraBits[matchOffset];
                            matchOffset = (uint)(PositionBase[matchOffset] - 2) + ReadBits(extra);
                        }
                        else matchOffset = 1;
                    }
                    else
                    {
                        int extra = ExtraBits[matchOffset];
                        matchOffset = (uint)(PositionBase[matchOffset] - 2);
                        if (extra > 3)
                        {
                            extra -= 3;
                            matchOffset += ReadBits(extra) << 3;
                            matchOffset += (uint)ReadSym(_aligned);
                        }
                        else if (extra == 3) matchOffset += (uint)ReadSym(_aligned);
                        else if (extra > 0) matchOffset += ReadBits(extra);
                        else matchOffset = 1;
                    }
                    _r2 = _r1; _r1 = _r0; _r0 = matchOffset;
                }
                else if (matchOffset == 0) matchOffset = _r0;
                else if (matchOffset == 1) { matchOffset = _r1; _r1 = _r0; _r0 = matchOffset; }
                else { matchOffset = _r2; _r2 = _r0; _r0 = matchOffset; }

                int src = (int)((_windowPos - matchOffset) & (uint)(_windowSize - 1));
                for (int i = 0; i < matchLength; i++)
                {
                    _window[_windowPos] = _window[src];
                    _windowPos = (_windowPos + 1) & (_windowSize - 1);
                    src = (src + 1) & (_windowSize - 1);
                }
                thisRun -= matchLength;
            }

            if (thisRun < 0)
            {
                // A match ran past this frame. Those bytes belong to the next frame.
                int over = -thisRun;
                if (over > _blockRemaining) throw new InvalidDataException("LZX: match overran block.");
                _blockRemaining -= over;
                if (togo >= over) togo -= over;
                else { _overrun = over - togo; togo = 0; }
            }
        }

        for (int i = 0; i < outLen; i++)
            output[outPos + i] = _window[(frameStart + i) & (_windowSize - 1)];

        if (_intelFileSize != 0 && _intelStarted && outLen > 10 && _framesRead < 32768)
            UndoE8(output, outPos, outLen);
        _framesRead++;
    }

    uint ReadRawU32()
    {
        uint v = 0;
        for (int i = 0; i < 4; i++) v |= (uint)(_inPos < _inEnd ? _in[_inPos++] : 0) << (8 * i);
        return v;
    }

    void UndoE8(byte[] data, int start, int len)
    {
        int curPos = _framesRead * 0x8000;
        int end = start + len - 10;
        for (int i = start; i < end;)
        {
            if (data[i++] != 0xE8) { curPos++; continue; }
            int absOff = data[i] | (data[i + 1] << 8) | (data[i + 2] << 16) | (data[i + 3] << 24);
            if (absOff >= -curPos && absOff < _intelFileSize)
            {
                int relOff = absOff > 0 ? absOff - curPos : absOff + _intelFileSize;
                data[i] = (byte)relOff; data[i + 1] = (byte)(relOff >> 8); data[i + 2] = (byte)(relOff >> 16); data[i + 3] = (byte)(relOff >> 24);
            }
            i += 4;
            curPos += 5;
        }
    }
}
