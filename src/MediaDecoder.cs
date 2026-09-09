using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AbletonManager
{
    /// <summary>
    /// Огибающая для всего, что не разобрал наш RIFF-парсер. Своего декодера у нас нет
    /// и взять его неоткуда — проект собирается голым csc без пакетов, — поэтому
    /// декодирует сама Windows через Media Foundation: mp3, m4a, wma, flac и wav
    /// любого вида, то есть всё, что вообще попадается среди рендеров.
    /// </summary>
    static class MediaDecoder
    {
        /// <summary>Сколько кадров сворачивается в один пик — исходное разрешение огибающей.</summary>
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

                // OpenPcm всегда конвертирует в float32 — фиксированная раскладка без
                // вариаций упаковки, никаких «bits» из атрибутов тут не нужно.
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

        /// <summary>Сводит пики блоков к числу столбцов картинки — берём крайние значения диапазона.</summary>
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
