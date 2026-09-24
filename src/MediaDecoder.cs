using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

namespace AbletonManager
{
    /// <summary>
    /// The envelope for everything our own RIFF parser could not read. We have no decoder of
    /// our own and nowhere to take one from — the project builds with bare csc and no packages
    /// — so Windows decodes it through Media Foundation: mp3, m4a, wma, flac and wav of any
    /// flavour, which is everything that ever turns up among renders.
    /// </summary>
    static class MediaDecoder
    {
        /// <summary>How many frames collapse into one peak — the envelope's source
        /// resolution.</summary>
        const int Block = 1024;

        public static Waveform Read(string path, int buckets)
        {
            Waveform w = new Waveform();
            bool started = false;
            IMFSourceReader reader = null;

            try
            {
                if (Mf.MFStartup(Mf.Version, 0) != 0)
                { w.Note = "no decoder"; return w; }
                started = true;

                int channels, bits, rate;
                long durationMs;
                if (!Mf.OpenPcm(path, out reader, out channels, out bits, out rate, out durationMs))
                { w.Note = "cannot decode"; return w; }

                // OpenPcm always converts to float32 — a fixed layout with no packing variants,
                // so none of the "bits" from the attributes are needed here.
                const int bytesPerSample = 4;
                int frameSize = bytesPerSample * channels;
                if (frameSize <= 0) return w;

                List<float> mins = new List<float>(4096), maxs = new List<float>(4096);
                float bMin = 1f, bMax = -1f;
                int inBlock = 0;
                byte[] copy = new byte[1 << 16];

                while (true)
                {
                    uint streamIndex, flags;
                    long timestamp;
                    IMFSample sample;
                    if (reader.ReadSample(Mf.FirstAudioStream, 0, out streamIndex, out flags,
                                          out timestamp, out sample) != 0) break;
                    if ((flags & Mf.EndOfStream) != 0) { Mf.Release(sample); break; }
                    if (sample == null) continue;

                    IMFMediaBuffer buffer = null;
                    try
                    {
                        if (sample.ConvertToContiguousBuffer(out buffer) != 0 || buffer == null) continue;

                        IntPtr ptr; int maxLen, curLen;
                        if (buffer.Lock(out ptr, out maxLen, out curLen) != 0) continue;
                        try
                        {
                            if (copy.Length < curLen) copy = new byte[curLen];
                            Marshal.Copy(ptr, copy, 0, curLen);
                        }
                        finally { buffer.Unlock(); }

                        for (int off = 0; off + frameSize <= curLen; off += frameSize)
                        {
                            float sum = 0f;
                            for (int c = 0; c < channels; c++)
                                sum += BitConverter.ToSingle(copy, off + c * bytesPerSample);
                            float v = sum / channels;
                            if (v < bMin) bMin = v;
                            if (v > bMax) bMax = v;

                            if (++inBlock >= Block)
                            {
                                mins.Add(bMin); maxs.Add(bMax);
                                bMin = 1f; bMax = -1f; inBlock = 0;
                            }
                        }
                    }
                    finally { Mf.Release(buffer); Mf.Release(sample); }
                }

                if (inBlock > 0 && bMax >= bMin) { mins.Add(bMin); maxs.Add(bMax); }
                if (mins.Count == 0) { w.Note = "silent or empty"; return w; }

                Resample(mins, maxs, buckets, w);
                w.Ok = true;
                return w;
            }
            catch { w.Note = "cannot decode"; return w; }
            finally
            {
                Mf.Release(reader);
                if (started) { try { Mf.MFShutdown(); } catch { } }
            }
        }

        /// <summary>Folds block peaks down to the number of columns in the picture — we take
        /// the extremes of each range.</summary>
        static void Resample(List<float> mins, List<float> maxs, int buckets, Waveform w)
        {
            int n = mins.Count;
            if (buckets > n) buckets = n;
            if (buckets < 1) buckets = 1;

            float[] mn = new float[buckets], mx = new float[buckets];
            for (int i = 0; i < buckets; i++)
            {
                int from = (int)((long)i * n / buckets);
                int to = (int)((long)(i + 1) * n / buckets);
                if (to <= from) to = from + 1;
                if (to > n) to = n;

                float lo = 1f, hi = -1f;
                for (int k = from; k < to; k++)
                {
                    if (mins[k] < lo) lo = mins[k];
                    if (maxs[k] > hi) hi = maxs[k];
                }
                if (hi < lo) { lo = 0f; hi = 0f; }
                mn[i] = lo; mx[i] = hi;
            }
            w.Min = mn; w.Max = mx;
        }

        /// <summary>
        /// What a sample is, for the details panel — "44.1 kHz · 24-bit · stereo" — and its
        /// length. WAV and AIFF from their headers; everything else asks Media Foundation, which
        /// knows the rate and the channels but converts to float and so cannot tell the bits.
        /// </summary>
        public static string Describe(string path, out int durationMs)
        {
            durationMs = 0;
            string ext = (System.IO.Path.GetExtension(path) ?? "").TrimStart('.').ToUpperInvariant();
            if (ext == "AIF" || ext == "AIFC") ext = "AIFF";
            int channels = 0, rate = 0, bits = 0;
            try
            {
                if (AiffReader.IsAiffName(path))
                {
                    using (AiffReader a = AiffReader.Open(path))
                        if (a != null) { channels = a.Channels; rate = a.Rate; bits = a.Bits; durationMs = a.DurationMs; }
                }
                else if (ext == "WAV") WaveReader.RiffFormat(path, out channels, out rate, out bits, out durationMs);

                if (rate == 0 && Mf.MFStartup(Mf.Version, 0) == 0)
                {
                    IMFSourceReader reader = null;
                    try
                    {
                        int floatBits;
                        long ms;
                        if (Mf.OpenPcm(path, out reader, out channels, out floatBits, out rate, out ms))
                            durationMs = (int)ms;
                    }
                    finally
                    {
                        Mf.Release(reader);
                        try { Mf.MFShutdown(); } catch { }
                    }
                }
            }
            catch { }

            List<string> parts = new List<string>();
            if (rate > 0) parts.Add((rate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz");
            if (bits > 0) parts.Add(bits + "-bit");
            if (channels == 1) parts.Add("mono");
            else if (channels == 2) parts.Add("stereo");
            else if (channels > 2) parts.Add(channels + " channels");
            // The container is in the file's own name, and with it the line did not fit the
            // panel at 125%: it is said only when nothing else could be read.
            if (parts.Count == 0 && ext.Length > 0) parts.Add(ext);
            return string.Join(" · ", parts.ToArray());
        }

        public static float Pcm(byte[] b, int i, int bits)
        {
            switch (bits)
            {
                case 8: return (b[i] - 128) / 128f;
                case 16: return (short)(b[i] | (b[i + 1] << 8)) / 32768f;
                case 24:
                {
                    int v = b[i] | (b[i + 1] << 8) | (b[i + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);
                    return v / 8388608f;
                }
                case 32: return BitConverter.ToInt32(b, i) / 2147483648f;
            }
            return 0f;
        }
    }
}
