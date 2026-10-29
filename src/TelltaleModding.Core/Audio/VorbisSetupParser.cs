namespace TelltaleModding.Core.Audio;

/// <summary>
/// Walks a Vorbis setup header (codebooks, floors, residues, mappings, modes) far
/// enough to learn its exact length and each mode's block flag, which is what an
/// Ogg muxer needs to compute packet durations.
/// </summary>
internal static class VorbisSetupParser
{
    private sealed class BitReader
    {
        private readonly byte[] _data;
        private readonly int _start, _end;
        private long _bit;

        public BitReader(byte[] data, int start, int end)
        {
            _data = data;
            _start = start;
            _end = end;
        }

        public long BitPosition => _bit;

        public uint Read(int count)
        {
            uint value = 0;
            for (int i = 0; i < count; i++)
            {
                long byteIndex = _start + (_bit >> 3);
                if (byteIndex >= _end) throw new EndOfStreamException();
                uint b = (uint)(_data[byteIndex] >> (int)(_bit & 7)) & 1;
                value |= b << i;
                _bit++;
            }
            return value;
        }

        public bool Bit() => Read(1) != 0;

        public void Skip(long bits)
        {
            if (_start + ((_bit + bits + 7) >> 3) > _end) throw new EndOfStreamException();
            _bit += bits;
        }
    }

    internal static int ILog(uint v)
    {
        int n = 0;
        while (v != 0) { n++; v >>= 1; }
        return n;
    }

    private static long Lookup1Values(long entries, int dims)
    {
        long r = (long)Math.Floor(Math.Pow(entries, 1.0 / dims));
        while (Pow(r + 1, dims) <= entries) r++;
        while (r > 0 && Pow(r, dims) > entries) r--;
        return r;

        static long Pow(long b, int e)
        {
            long v = 1;
            for (int i = 0; i < e; i++) { v *= b; if (v > int.MaxValue) return long.MaxValue; }
            return v;
        }
    }

    /// <summary>
    /// Parses a setup packet starting at <paramref name="start"/> (at its 0x05 'vorbis' signature).
    /// Returns the packet length in bytes and the per-mode block flags, or false if it is malformed.
    /// </summary>
    internal static bool TryParse(byte[] data, int start, int end, int channels, out int length, out bool[] modeBlockFlags)
    {
        length = 0;
        modeBlockFlags = Array.Empty<bool>();
        if (end - start < 8 || data[start] != 5 || data[start + 1] != (byte)'v' || data[start + 6] != (byte)'s')
            return false;
        try
        {
            var r = new BitReader(data, start + 7, end);

            int books = (int)r.Read(8) + 1;
            for (int b = 0; b < books; b++)
            {
                if (r.Read(24) != 0x564342) return false;
                int dims = (int)r.Read(16);
                long entries = r.Read(24);
                if (r.Bit())
                {
                    // Ordered: runs of increasing length.
                    r.Read(5);
                    long cur = 0;
                    while (cur < entries)
                    {
                        cur += r.Read(ILog((uint)(entries - cur)));
                    }
                    if (cur > entries) return false;
                }
                else
                {
                    bool sparse = r.Bit();
                    for (long e = 0; e < entries; e++)
                    {
                        if (!sparse || r.Bit()) r.Read(5);
                    }
                }
                uint lookup = r.Read(4);
                if (lookup is 1 or 2)
                {
                    r.Skip(64);
                    int valueBits = (int)r.Read(4) + 1;
                    r.Read(1);
                    long values = lookup == 1 ? Lookup1Values(entries, dims) : entries * dims;
                    r.Skip(values * valueBits);
                }
                else if (lookup != 0) return false;
            }

            int times = (int)r.Read(6) + 1;
            for (int i = 0; i < times; i++)
                if (r.Read(16) != 0) return false;

            int floors = (int)r.Read(6) + 1;
            for (int i = 0; i < floors; i++)
            {
                uint type = r.Read(16);
                if (type == 0)
                {
                    r.Skip(8 + 16 + 16 + 6 + 8);
                    int n = (int)r.Read(4) + 1;
                    r.Skip(8L * n);
                }
                else if (type == 1)
                {
                    int partitions = (int)r.Read(5);
                    var classList = new int[partitions];
                    int maxClass = -1;
                    for (int p = 0; p < partitions; p++)
                    {
                        classList[p] = (int)r.Read(4);
                        maxClass = Math.Max(maxClass, classList[p]);
                    }
                    var classDims = new int[maxClass + 1];
                    for (int c = 0; c <= maxClass; c++)
                    {
                        classDims[c] = (int)r.Read(3) + 1;
                        int sub = (int)r.Read(2);
                        if (sub != 0) r.Read(8);
                        r.Skip(8L * (1 << sub));
                    }
                    r.Read(2);
                    int rangeBits = (int)r.Read(4);
                    for (int p = 0; p < partitions; p++)
                        r.Skip((long)classDims[classList[p]] * rangeBits);
                }
                else return false;
            }

            int residues = (int)r.Read(6) + 1;
            for (int i = 0; i < residues; i++)
            {
                if (r.Read(16) > 2) return false;
                r.Skip(24 + 24 + 24);
                int classifications = (int)r.Read(6) + 1;
                r.Read(8);
                var cascade = new uint[classifications];
                for (int c = 0; c < classifications; c++)
                {
                    uint low = r.Read(3);
                    uint high = r.Bit() ? r.Read(5) : 0;
                    cascade[c] = high * 8 + low;
                }
                for (int c = 0; c < classifications; c++)
                    for (int j = 0; j < 8; j++)
                        if ((cascade[c] & (1u << j)) != 0) r.Read(8);
            }

            int mappings = (int)r.Read(6) + 1;
            int chBits = ILog((uint)Math.Max(0, channels - 1));
            for (int i = 0; i < mappings; i++)
            {
                if (r.Read(16) != 0) return false;
                int submaps = r.Bit() ? (int)r.Read(4) + 1 : 1;
                if (r.Bit())
                {
                    int steps = (int)r.Read(8) + 1;
                    r.Skip((long)steps * chBits * 2);
                }
                if (r.Read(2) != 0) return false;
                if (submaps > 1) r.Skip(4L * channels);
                r.Skip(24L * submaps);
            }

            int modes = (int)r.Read(6) + 1;
            modeBlockFlags = new bool[modes];
            for (int i = 0; i < modes; i++)
            {
                modeBlockFlags[i] = r.Bit();
                if (r.Read(16) != 0 || r.Read(16) != 0) return false;
                if (r.Read(8) >= mappings) return false;
            }
            if (!r.Bit()) return false; // framing

            length = 7 + (int)((r.BitPosition + 7) >> 3);
            return true;
        }
        catch (EndOfStreamException)
        {
            return false;
        }
    }
}
