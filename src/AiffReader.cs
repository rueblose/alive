using System;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>
    /// AIFF and AIFF-C, read by hand. Media Foundation has no AIFF source, and the packs Live
    /// ships are almost all AIFF — 30,586 of them on the development machine. Without this the
    /// preview would stay silent on exactly the samples everybody has.
    ///
    /// Uncompressed PCM only: plain AIFF, AIFC "NONE"/"twos" (big-endian), "sowt"
    /// (little-endian) and "fl32" (32-bit float). Frames come out as float32 — the shape
    /// Mf.OpenPcm gives — so the player and the envelope do not care where they came from.
    /// </summary>
    public sealed class AiffReader : IDisposable
    {
        public int Channels, Rate, Bits;
        public long Frames;

        readonly FileStream _fs;
        long _dataStart, _frame;
        bool _little, _float;
        int _bytesPerSample, _frameBytes;
        byte[] _raw = new byte[0];
        float[] _pcm = new float[0];
        readonly byte[] _four = new byte[4];

        AiffReader(FileStream fs) { _fs = fs; }

        public int DurationMs { get { return Rate > 0 ? (int)(Frames * 1000L / Rate) : 0; } }

        public static bool IsAiffName(string path)
        {
            string ext = Path.GetExtension(path ?? "");
            return ext.Equals(".aif", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".aiff", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".aifc", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>null — not an AIFF, broken, or a compression we do not read.</summary>
        public static AiffReader Open(string path)
        {
            FileStream fs = null;
            try
            {
                fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024);
                AiffReader a = new AiffReader(fs);
                if (a.ReadHeader()) return a;
            }
            catch { }
            if (fs != null) fs.Dispose();
            return null;
        }

        /// <summary>
        /// Whether the preview can play this AIFF. Most of the samples in Live's packs are AIFC
        /// compressed with Ableton's own codec ("able" — 2,480 of the first 3,000 in the packs on
        /// the development machine): only Live plays those, and the walk marks them so the play
        /// button is not a promise that fails.
        /// </summary>
        public static bool CanRead(string path)
        {
            using (AiffReader a = Open(path)) return a != null;
        }

        bool ReadHeader()
        {
            BinaryReader r = new BinaryReader(_fs);      // not disposed: it would close the stream
            if (Tag(r) != "FORM") return false;
            r.ReadBytes(4);
            string form = Tag(r);
            bool aifc = form == "AIFC";
            if (form != "AIFF" && !aifc) return false;

            string compression = "NONE";
            bool comm = false;
            long ssnd = -1;
            // Every pass consumes at least the eight bytes of a chunk header, so a broken size
            // cannot hold the loop in place.
            while (_fs.Position + 8 <= _fs.Length)
            {
                string id = Tag(r);
                long size = BE32(r);
                long body = _fs.Position;
                if (id == "COMM")
                {
                    Channels = BE16(r);
                    Frames = BE32(r);
                    Bits = BE16(r);
                    Rate = (int)Math.Round(Extended(r.ReadBytes(10)));
                    if (aifc && size >= 22) compression = Tag(r);
                    comm = true;
                }
                else if (id == "SSND")
                {
                    long offset = BE32(r);
                    ssnd = body + 8 + offset;
                }
                _fs.Position = body + size + (size & 1);
            }

            if (!comm || ssnd < 0 || Channels <= 0 || Rate <= 0) return false;
            switch (compression)
            {
                case "NONE": case "twos": break;
                case "sowt": _little = true; break;
                case "fl32": case "FL32": _float = true; Bits = 32; break;
                default: return false;
            }
            if (!_float && Bits != 8 && Bits != 16 && Bits != 24 && Bits != 32) return false;

            _bytesPerSample = (Bits + 7) / 8;
            _frameBytes = _bytesPerSample * Channels;
            _dataStart = ssnd;
            // A file cut short claims more frames than it holds — the bytes are what counts.
            long have = Math.Max(0, (_fs.Length - _dataStart) / _frameBytes);
            if (have < Frames) Frames = have;
            Seek(0);
            return true;
        }

        /// <summary>Fills dst with whole float32 frames; returns the bytes written, 0 at the end.</summary>
        public int Read(byte[] dst)
        {
            long left = Frames - _frame;
            int frames = (int)Math.Min(dst.Length / (Channels * 4), left);
            if (frames <= 0) return 0;

            int want = frames * _frameBytes;
            if (_raw.Length < want) _raw = new byte[want];
            int got = 0;
            while (got < want)
            {
                int n = _fs.Read(_raw, got, want - got);
                if (n <= 0) break;
                got += n;
            }
            frames = got / _frameBytes;
            int samples = frames * Channels;
            if (_pcm.Length < samples) _pcm = new float[samples];
            for (int i = 0, o = 0; i < samples; i++, o += _bytesPerSample) _pcm[i] = Sample(_raw, o);
            Buffer.BlockCopy(_pcm, 0, dst, 0, samples * 4);
            _frame += frames;
            return samples * 4;
        }

        float Sample(byte[] b, int i)
        {
            if (_float)
            {
                _four[0] = b[i + 3]; _four[1] = b[i + 2]; _four[2] = b[i + 1]; _four[3] = b[i];
                return BitConverter.ToSingle(_four, 0);
            }
            switch (_bytesPerSample)
            {
                case 1:
                    return (sbyte)b[i] / 128f;
                case 2:
                    return (short)(_little ? b[i] | (b[i + 1] << 8) : (b[i] << 8) | b[i + 1]) / 32768f;
                case 3:
                {
                    int v = _little ? b[i] | (b[i + 1] << 8) | (b[i + 2] << 16)
                                    : (b[i] << 16) | (b[i + 1] << 8) | b[i + 2];
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    return v / 8388608f;
                }
                default:
                {
                    int v = _little ? b[i] | (b[i + 1] << 8) | (b[i + 2] << 16) | (b[i + 3] << 24)
                                    : (b[i] << 24) | (b[i + 1] << 16) | (b[i + 2] << 8) | b[i + 3];
                    return v / 2147483648f;
                }
            }
        }

        public void Seek(long frame)
        {
            _frame = Math.Max(0, Math.Min(Frames, frame));
            _fs.Position = _dataStart + _frame * _frameBytes;
        }

        public void Dispose() { _fs.Dispose(); }

        /// <summary>The envelope by the same rule as WaveReader's for WAV: the mono mix, the
        /// minimum and maximum in each column.</summary>
        public static Waveform Envelope(string path, int buckets)
        {
            Waveform w = new Waveform();
            using (AiffReader a = Open(path))
            {
                if (a == null || a.Frames <= 0) { w.Note = "cannot decode"; return w; }
                float[] mn = new float[buckets], mx = new float[buckets];
                for (int i = 0; i < buckets; i++) { mn[i] = 1f; mx[i] = -1f; }

                int frameSize = a.Channels * 4;
                byte[] buf = new byte[frameSize * 8192];
                long frame = 0;
                int n;
                while ((n = a.Read(buf)) > 0)
                {
                    for (int off = 0; off + frameSize <= n; off += frameSize, frame++)
                    {
                        float sum = 0f;
                        for (int c = 0; c < a.Channels; c++) sum += BitConverter.ToSingle(buf, off + c * 4);
                        float v = sum / a.Channels;
                        int bucket = (int)(frame * buckets / a.Frames);
                        if (bucket >= buckets) bucket = buckets - 1;
                        if (v < mn[bucket]) mn[bucket] = v;
                        if (v > mx[bucket]) mx[bucket] = v;
                    }
                }
                for (int i = 0; i < buckets; i++)
                    if (mn[i] > mx[i]) { mn[i] = 0f; mx[i] = 0f; }
                w.Min = mn;
                w.Max = mx;
                w.Ok = true;
                return w;
            }
        }

        static string Tag(BinaryReader r)
        {
            byte[] b = r.ReadBytes(4);
            return b.Length == 4 ? Encoding.ASCII.GetString(b) : "";
        }

        static int BE16(BinaryReader r)
        {
            byte[] b = r.ReadBytes(2);
            return b.Length == 2 ? (short)((b[0] << 8) | b[1]) : 0;
        }

        static long BE32(BinaryReader r)
        {
            byte[] b = r.ReadBytes(4);
            return b.Length == 4 ? ((long)b[0] << 24) | ((long)b[1] << 16) | ((long)b[2] << 8) | b[3] : 0;
        }

        /// <summary>The sample rate is an 80-bit IEEE extended: a sign, a 15-bit exponent and a
        /// 64-bit mantissa with its integer bit spelled out.</summary>
        static double Extended(byte[] b)
        {
            if (b == null || b.Length < 10) return 0;
            int exp = ((b[0] & 0x7F) << 8) | b[1];
            ulong mant = 0;
            for (int i = 2; i < 10; i++) mant = (mant << 8) | b[i];
            if (exp == 0 && mant == 0) return 0;
            double v = mant * Math.Pow(2, exp - 16383 - 63);
            return (b[0] & 0x80) != 0 ? -v : v;
        }
    }
}
