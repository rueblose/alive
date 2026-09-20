using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AbletonManager
{
    /// <summary>
    /// Playing a file: Media Foundation decodes it into PCM, waveOut sends it out. The obvious
    /// MCI (mciSendString) had to be thrown out — on a real library it opened 4 files out of
    /// 26: it stumbled over hyphens and brackets in names, over tags in mp3s, and simply failed
    /// to find a driver. Media Foundation takes those same files every one, and since the
    /// decoded stream is in our hands anyway, the easiest way to send it out further is the
    /// simplest of the audio subsystems.
    /// </summary>
    public sealed class AudioPlayer : IDisposable
    {
        // ------------------------------------------------------------- waveOut
        [DllImport("winmm.dll")]
        static extern int waveOutOpen(out IntPtr hwo, int deviceId, WaveFormat fmt,
                                      IntPtr callback, IntPtr instance, int flags);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutPause(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutRestart(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutSetVolume(IntPtr hwo, uint volume);
        [DllImport("winmm.dll")] static extern int waveOutGetPosition(IntPtr hwo, ref MmTime time, int size);

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        sealed class WaveFormat
        {
            // WAVE_FORMAT_IEEE_FLOAT: Mf.OpenPcm always returns the decoded stream as float32 —
            // see the comment there about the packing of 24-bit sources.
            public short FormatTag = 3;
            public short Channels;
            public int SamplesPerSec;
            public int AvgBytesPerSec;
            public short BlockAlign;
            public short BitsPerSample;
            public short Size;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WaveHdr
        {
            public IntPtr Data;
            public int BufferLength;
            public int BytesRecorded;
            public IntPtr User;
            public int Flags;
            public int Loops;
            public IntPtr Next;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MmTime
        {
            public int Type;
            public int Value;
            public int Pad1;
            public int Pad2;
        }

        const int WhdrDone = 0x00000001;
        const int TimeSamples = 0x0002;

        // Eight buffers of 16 KB — about 0.7 s of headroom at 44.1 kHz stereo. Less and we risk
        // clicks on a loaded machine; more and seeking feels sluggish.
        const int Buffers = 8;
        const int BufferBytes = 16384;

        // ------------------------------------------------------------------ state

        /// <summary>
        /// One playback: its own thread, its own waveOut device, its own buffers, its own
        /// flags. All the hardware lives HERE rather than in fields of AudioPlayer itself, and
        /// that is not tidiness for tidiness' sake.
        ///
        /// The device fields used to be shared across the whole player, and Close(), with the
        /// thread not yet finished, called Thread.Abort. Both outcomes are bad: Abort could cut
        /// the thread off right inside a finally — and the device would stay open until the
        /// program ended; while not calling it was impossible, because the next track would
        /// immediately start allocating buffers into the very fields the previous thread had
        /// not finished releasing (that is memory corruption, not a leak).
        ///
        /// With a separate kit per playback the choice disappears: a stuck thread calmly
        /// finishes tidying ITS OWN, sharing nothing with anyone, and a new track plays without
        /// waiting for it.
        /// </summary>
        sealed class Run
        {
            public readonly string Path;
            public readonly Thread Thread;

            // Gate protects Hwo from the race where "the interface asks for the position at the
            // very moment the thread is closing the device".
            public readonly object Gate = new object();

            public volatile bool Stop, Paused, Eof, Done, OpenOk, Opening;
            public volatile int SeekRequest = -1;
            public volatile string Error = "";
            public volatile float Volume;

            public IntPtr Hwo = IntPtr.Zero;
            public readonly IntPtr[] Hdr = new IntPtr[Buffers];
            public readonly IntPtr[] Mem = new IntPtr[Buffers];
            public readonly bool[] Queued = new bool[Buffers];

            public int Rate, Channels, Bits, FrameSize;
            public long SeekBaseFrames, DecodedFrames;
            public int LengthMs;
            public volatile int KnownMs;   // how much there certainly is already, when the length is not known in advance

            // The decoder returns chunks of arbitrary length while the device needs exactly a
            // buffer, so what is left over from the previous read is carried into the next.
            public byte[] Pending = new byte[1 << 16];
            public int PendingOff, PendingLen;

            public Run(string path, float volume, bool autoStart, ParameterizedThreadStart pump)
            {
                Path = path;
                Volume = volume;
                Paused = !autoStart;
                Opening = true;
                Thread = new Thread(pump);
                Thread.IsBackground = true;
                // Media Foundation's COM objects do not like moving between apartments, and the
                // output thread has to be our own anyway.
                Thread.SetApartmentState(ApartmentState.MTA);
            }
        }

        volatile Run _run;
        float _volume = 0.8f;

        public string Path = "";

        /// <summary>Why it did not start playing. Empty while all is well.</summary>
        public string Error { get { Run r = _run; return r != null ? r.Error : ""; } }

        /// <summary>Duration in milliseconds; 0 while it is not known at all.</summary>
        public int Length
        {
            get
            {
                Run r = _run;
                if (r == null) return 0;
                return r.LengthMs > 0 ? r.LengthMs : r.KnownMs;
            }
        }

        public bool IsOpen { get { Run r = _run; return r != null && r.OpenOk; } }

        /// <summary>The file is still opening. While it is, touching the transport is
        /// pointless.</summary>
        public bool IsOpening { get { Run r = _run; return r != null && r.Opening; } }

        /// <summary>
        /// It did not start playing — the reason is in <see cref="Error"/>. We look at the
        /// error text rather than at "not open and no longer opening": a track that has played
        /// to the end also stops being open eventually, and by such a sign a successful
        /// playback would be declared a failure.
        /// </summary>
        public bool OpenFailed
        {
            get { Run r = _run; return r != null && !r.Opening && r.Error.Length > 0; }
        }

        /// <summary>The track reached its end by itself — a signal for the playlist to move
        /// on.</summary>
        public bool Finished { get { Run r = _run; return r != null && r.Done; } }

        public bool IsPlaying
        {
            get { Run r = _run; return r != null && r.OpenOk && !r.Paused && !r.Done; }
        }

        /// <summary>
        /// The transport is standing on pause. This is what was ASKED for, and unlike
        /// <see cref="IsPlaying"/> it does not wait for the device: while a file is opening
        /// nothing is sounding yet, and "paused" is told apart from "about to start" only
        /// here. Everything that decides what a press of play/pause means, or whether a
        /// finished track may pull the next one in, asks this and not the device.
        /// </summary>
        public bool IsPaused { get { Run r = _run; return r != null && r.Paused; } }

        public int Position
        {
            get
            {
                Run r = _run;
                if (r == null || !r.OpenOk || r.Rate <= 0) return 0;
                long frames = r.SeekBaseFrames + PlayedFrames(r);
                int ms = (int)(frames * 1000L / r.Rate);
                int len = Length;
                if (len > 0 && ms > len) ms = len;
                return ms;
            }
        }

        public float Volume
        {
            get { return _volume; }
            set
            {
                _volume = Math.Max(0f, Math.Min(1f, value));
                Run r = _run;
                if (r == null) return;
                r.Volume = _volume;
                ApplyVolume(r);
            }
        }

        // ------------------------------------------------------------------ opening

        /// <summary>
        /// Starts playback. It does NOT WAIT for the file to open: there used to be an
        /// _opened.WaitOne(8000) here, and the whole interface froze for exactly as long as the
        /// system decoder fussed with the file — on a slow or network drive that is seconds of
        /// a dead-frozen window from one press of play.
        ///
        /// How it ended is asked afterwards: IsOpening / IsOpen / OpenFailed. The player polls
        /// the state with its own timer anyway, so a separate notification mechanism is not
        /// needed.
        /// </summary>
        public void Open(string path, bool autoStart)
        {
            Close();
            Path = path;

            Run r = new Run(path, _volume, autoStart, Pump);
            _run = r;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                r.Error = "File is gone";
                r.Opening = false;          // the thread is not started — there is nothing to open
                return;
            }
            r.Thread.Start(r);
        }

        public void Close()
        {
            Run r = _run;
            _run = null;
            StopRun(r);
        }

        /// <summary>
        /// Ask the playback to finish. Thread.Abort is gone from here — see the comment on Run.
        /// Missed the deadline (usually a read stuck on a network drive) — we let go: the
        /// thread is a background one, Stop is set on it, and once free it will close its
        /// device and release its buffers. It has nothing to share with a new playback.
        ///
        /// We wait briefly and for one reason only: so the old track falls silent before the
        /// new one sounds (a healthy thread sees Stop within 20 ms at most, so this is plenty).
        /// The former two seconds here would be a straight return to what we were getting away
        /// from — Close() is called from Open(), and the whole gain from non-blocking opening
        /// would be eaten by waiting for the previous one.
        /// </summary>
        static void StopRun(Run r)
        {
            if (r == null) return;
            r.Stop = true;
            if (r.Thread == null || !r.Thread.IsAlive) return;
            try { r.Thread.Join(300); } catch { }
        }

        // ------------------------------------------------------------------ transport

        public void Play()
        {
            Run r = _run;
            if (r == null) return;
            if (r.Done) r.SeekRequest = 0;
            // We lift the pause even while opening is under way: the thread, on reaching
            // readiness, will see Paused=false itself and start playing. Otherwise a press of
            // play in the first second after picking a track would vanish silently.
            r.Paused = false;
        }

        public void Pause() { Run r = _run; if (r != null) r.Paused = true; }

        public void Stop() { Run r = _run; if (r != null) { r.Paused = true; Seek(0); } }

        public void Seek(int ms)
        {
            Run r = _run;
            if (r == null) return;
            if (ms < 0) ms = 0;
            int len = Length;
            if (len > 0 && ms > len) ms = len;
            r.SeekRequest = ms;
        }

        // ------------------------------------------------------- the output thread

        void Pump(object arg)
        {
            Run r = (Run)arg;
            bool mfStarted = false;
            IMFSourceReader reader = null;
            byte[] staging = new byte[BufferBytes];

            try
            {
                if (Mf.MFStartup(Mf.Version, 0) != 0)
                { r.Error = "Media Foundation is unavailable"; return; }
                mfStarted = true;

                long durationMs;
                if (!Mf.OpenPcm(r.Path, out reader, out r.Channels, out r.Bits, out r.Rate, out durationMs))
                { r.Error = "No decoder for this file"; return; }

                // Some WAVs (single samples, files with no index) do not report a duration — we
                // then take it straight from the header. If that is empty too, the length grows
                // as decoding proceeds: below, in ReadPcm.
                r.LengthMs = (int)durationMs;
                if (r.LengthMs <= 0) r.LengthMs = WaveReader.RiffDurationMs(r.Path);

                r.FrameSize = r.Bits / 8 * r.Channels;
                if (r.FrameSize <= 0)
                { r.Error = "Unsupported audio format"; return; }

                WaveFormat fmt = new WaveFormat();
                fmt.Channels = (short)r.Channels;
                fmt.SamplesPerSec = r.Rate;
                fmt.BitsPerSample = (short)r.Bits;
                fmt.BlockAlign = (short)r.FrameSize;
                fmt.AvgBytesPerSec = r.Rate * r.FrameSize;

                IntPtr hwo;
                if (waveOutOpen(out hwo, -1 /* WAVE_MAPPER */, fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                { r.Error = "The sound device is busy"; return; }
                lock (r.Gate) r.Hwo = hwo;

                AllocBuffers(r);
                ApplyVolume(r);
                waveOutPause(hwo);           // we wait for the Play command rather than starting by ourselves

                r.OpenOk = true;
                r.Opening = false;

                bool devicePaused = true;
                while (!r.Stop)
                {
                    int seek = r.SeekRequest;
                    if (seek >= 0)
                    {
                        r.SeekRequest = -1;
                        DoSeek(r, reader, seek);
                        if (r.Paused) waveOutPause(hwo); else waveOutRestart(hwo);
                        devicePaused = r.Paused;
                    }

                    if (r.Paused != devicePaused)
                    {
                        if (r.Paused) waveOutPause(hwo); else waveOutRestart(hwo);
                        devicePaused = r.Paused;
                    }

                    if (r.Eof)
                    {
                        if (AllDrained(r)) r.Done = true;
                        Thread.Sleep(20);
                        continue;
                    }

                    int slot = FreeSlot(r);
                    if (slot < 0) { Thread.Sleep(6); continue; }

                    int n = ReadPcm(r, reader, staging);
                    if (n <= 0) { r.Eof = true; continue; }
                    Submit(r, slot, staging, n);
                }
            }
            catch (Exception ex) { if (r.Error.Length == 0) r.Error = ex.Message; }
            finally
            {
                r.OpenOk = false;
                // First we hide the handle from readers (Position asks for the position 25
                // times a second), and only then close the device.
                IntPtr h;
                lock (r.Gate) { h = r.Hwo; r.Hwo = IntPtr.Zero; }
                if (h != IntPtr.Zero)
                {
                    try
                    {
                        waveOutReset(h);
                        FreeBuffers(r, h);
                        waveOutClose(h);
                    }
                    catch { }
                }
                Mf.Release(reader);
                if (mfStarted) { try { Mf.MFShutdown(); } catch { } }
                r.Opening = false;   // in case of a crash before readiness — otherwise we would wait forever
            }
        }

        static void DoSeek(Run r, IMFSourceReader reader, int ms)
        {
            waveOutReset(r.Hwo);                // drops the queue and zeroes the position
            for (int i = 0; i < Buffers; i++) r.Queued[i] = false;
            r.PendingLen = r.PendingOff = 0;
            Mf.SetPosition(reader, ms);
            r.SeekBaseFrames = (long)ms * r.Rate / 1000;
            r.DecodedFrames = 0;
            r.Eof = false;
            r.Done = false;
        }

        // ------------------------------------------------------- waveOut buffers

        static void AllocBuffers(Run r)
        {
            int hdrSize = Marshal.SizeOf(typeof(WaveHdr));
            for (int i = 0; i < Buffers; i++)
            {
                r.Mem[i] = Marshal.AllocHGlobal(BufferBytes);
                r.Hdr[i] = Marshal.AllocHGlobal(hdrSize);
                WaveHdr h = new WaveHdr();
                h.Data = r.Mem[i];
                h.BufferLength = BufferBytes;
                Marshal.StructureToPtr(h, r.Hdr[i], false);
                waveOutPrepareHeader(r.Hwo, r.Hdr[i], hdrSize);
                r.Queued[i] = false;
            }
        }

        static void FreeBuffers(Run r, IntPtr hwo)
        {
            int hdrSize = Marshal.SizeOf(typeof(WaveHdr));
            for (int i = 0; i < Buffers; i++)
            {
                if (r.Hdr[i] != IntPtr.Zero)
                {
                    waveOutUnprepareHeader(hwo, r.Hdr[i], hdrSize);
                    Marshal.FreeHGlobal(r.Hdr[i]);
                    r.Hdr[i] = IntPtr.Zero;
                }
                if (r.Mem[i] != IntPtr.Zero) { Marshal.FreeHGlobal(r.Mem[i]); r.Mem[i] = IntPtr.Zero; }
            }
        }

        static bool SlotDone(Run r, int i)
        {
            if (!r.Queued[i]) return true;
            int flags = Marshal.ReadInt32(r.Hdr[i], Marshal.OffsetOf(typeof(WaveHdr), "Flags").ToInt32());
            return (flags & WhdrDone) != 0;
        }

        static int FreeSlot(Run r)
        {
            for (int i = 0; i < Buffers; i++) if (SlotDone(r, i)) return i;
            return -1;
        }

        static bool AllDrained(Run r)
        {
            for (int i = 0; i < Buffers; i++) if (!SlotDone(r, i)) return false;
            return true;
        }

        static void Submit(Run r, int slot, byte[] data, int count)
        {
            Marshal.Copy(data, 0, r.Mem[slot], count);
            int lenOff = Marshal.OffsetOf(typeof(WaveHdr), "BufferLength").ToInt32();
            int flagOff = Marshal.OffsetOf(typeof(WaveHdr), "Flags").ToInt32();
            int flags = Marshal.ReadInt32(r.Hdr[slot], flagOff);
            Marshal.WriteInt32(r.Hdr[slot], lenOff, count);
            Marshal.WriteInt32(r.Hdr[slot], flagOff, flags & ~WhdrDone);
            r.Queued[slot] = true;
            waveOutWrite(r.Hwo, r.Hdr[slot], Marshal.SizeOf(typeof(WaveHdr)));
        }

        static long PlayedFrames(Run r)
        {
            // Under Gate, because this is called from the UI thread while the output thread may
            // be closing the device at the same moment: without the lock it is easy to ask an
            // already-closed handle for the position.
            lock (r.Gate)
            {
                if (r.Hwo == IntPtr.Zero) return 0;
                MmTime t = new MmTime();
                t.Type = TimeSamples;
                if (waveOutGetPosition(r.Hwo, ref t, Marshal.SizeOf(typeof(MmTime))) != 0) return 0;
                if (t.Type != TimeSamples) return 0;
                return (uint)t.Value;
            }
        }

        static void ApplyVolume(Run r)
        {
            lock (r.Gate)
            {
                if (r.Hwo == IntPtr.Zero) return;
                uint v = (uint)(r.Volume * 0xFFFF);
                try { waveOutSetVolume(r.Hwo, (v << 16) | v); } catch { }
            }
        }

        // ------------------------------------------------------------ reading PCM

        static int ReadPcm(Run r, IMFSourceReader reader, byte[] dst)
        {
            int written = 0;
            while (written < dst.Length)
            {
                if (r.PendingLen > 0)
                {
                    int take = Math.Min(r.PendingLen, dst.Length - written);
                    Buffer.BlockCopy(r.Pending, r.PendingOff, dst, written, take);
                    written += take; r.PendingOff += take; r.PendingLen -= take;
                    continue;
                }

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
                        if (r.Pending.Length < curLen) r.Pending = new byte[curLen];
                        Marshal.Copy(ptr, r.Pending, 0, curLen);
                        r.PendingOff = 0; r.PendingLen = curLen;
                    }
                    finally { buffer.Unlock(); }
                }
                finally { Mf.Release(buffer); Mf.Release(sample); }
            }

            // We hand over whole frames only — half a frame would swap the channels round.
            if (r.FrameSize > 0) written -= written % r.FrameSize;

            // The length is known neither from the metadata nor from the header — we count it
            // as we go, so that the progress bar becomes true by the end of the track at least.
            if (r.LengthMs <= 0 && r.FrameSize > 0 && r.Rate > 0)
            {
                r.DecodedFrames += written / r.FrameSize;
                int ms = (int)((r.SeekBaseFrames + r.DecodedFrames) * 1000L / r.Rate);
                if (ms > r.KnownMs) r.KnownMs = ms;
            }
            return written;
        }

        public void Dispose() { Close(); }
    }

    /// <summary>The envelope: the minimum and maximum of the signal in each column of the
    /// picture.</summary>
    public sealed class Waveform
    {
        public float[] Min = new float[0];
        public float[] Max = new float[0];
        public bool Ok;
        public string Note = "";
    }

    /// <summary>
    /// The envelope. We parse WAV ourselves — it is simple and does not raise COM for every
    /// track; everything else (and any WAV we could not manage) goes to the system decoder.
    /// </summary>
    public static class WaveReader
    {
        public static Waveform Read(string path, int buckets)
        {
            if (buckets < 16) buckets = 16;

            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
                return MediaDecoder.Read(path, buckets);

            Waveform riff = ReadRiff(path, buckets);
            return riff.Ok ? riff : MediaDecoder.Read(path, buckets);
        }

        /// <summary>
        /// A WAV's duration straight from the header. Needed because Media Foundation does not
        /// report the length for some files (single samples, recordings with no index), and
        /// without it there is nothing to show either the time or the position on the waveform
        /// with.
        /// </summary>
        public static int RiffDurationMs(string path)
        {
            if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return 0;
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (BinaryReader r = new BinaryReader(fs))
                {
                    if (Tag(r) != "RIFF") return 0;
                    r.ReadInt32();
                    if (Tag(r) != "WAVE") return 0;

                    int rate = 0, blockAlign = 0;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id = Tag(r);
                        uint size = r.ReadUInt32();
                        long next = fs.Position + size + (size % 2);

                        if (id == "fmt ")
                        {
                            r.ReadInt16();                 // format
                            r.ReadInt16();                 // channels
                            rate = r.ReadInt32();
                            r.ReadInt32();                 // byte rate
                            blockAlign = r.ReadInt16();
                        }
                        else if (id == "data")
                        {
                            long dataLen = Math.Min(size, fs.Length - fs.Position);
                            if (rate <= 0 || blockAlign <= 0) return 0;
                            return (int)(dataLen / blockAlign * 1000L / rate);
                        }

                        if (next <= fs.Position) break;
                        fs.Position = next;
                    }
                }
            }
            catch { }
            return 0;
        }

        static Waveform ReadRiff(string path, int buckets)
        {
            Waveform w = new Waveform();
            try
            {
                using (FileStream fs = File.OpenRead(path))
                using (BinaryReader r = new BinaryReader(fs))
                {
                    if (Tag(r) != "RIFF") return w;
                    r.ReadInt32();
                    if (Tag(r) != "WAVE") return w;

                    int format = 0, channels = 0, bits = 0;
                    long dataPos = 0, dataLen = 0;

                    while (fs.Position + 8 <= fs.Length)
                    {
                        string id = Tag(r);
                        uint size = r.ReadUInt32();
                        long next = fs.Position + size + (size % 2);

                        if (id == "fmt ")
                        {
                            format = r.ReadInt16();
                            channels = r.ReadInt16();
                            r.ReadInt32();                 // sample rate
                            r.ReadInt32();                 // byte rate
                            r.ReadInt16();                 // block align
                            bits = r.ReadInt16();
                            if (format == 0xFFFE && size >= 40)
                            {
                                r.ReadInt16();             // cbSize
                                r.ReadInt16();             // valid bits
                                r.ReadInt32();             // channel mask
                                format = r.ReadInt16();    // the real format out of the GUID
                            }
                        }
                        else if (id == "data")
                        {
                            dataPos = fs.Position;
                            dataLen = Math.Min(size, fs.Length - dataPos);
                            break;
                        }

                        if (next <= fs.Position) break;
                        fs.Position = next;
                    }

                    if (channels <= 0 || bits <= 0 || dataLen <= 0) return w;
                    if (format != 1 && format != 3) return w;      // compressed — let MF take it

                    int bytesPerSample = bits / 8;
                    int frameSize = bytesPerSample * channels;
                    if (frameSize <= 0) return w;
                    long frames = dataLen / frameSize;
                    if (frames <= 0) return w;

                    float[] mn = new float[buckets], mx = new float[buckets];
                    for (int i = 0; i < buckets; i++) { mn[i] = 1f; mx[i] = -1f; }

                    fs.Position = dataPos;
                    byte[] buf = new byte[Math.Max(frameSize, 1 << 18) / frameSize * frameSize];
                    long frameIndex = 0, left = dataLen;

                    while (left > 0)
                    {
                        int want = (int)Math.Min(buf.Length, left);
                        int got = fs.Read(buf, 0, want);
                        if (got < frameSize) break;
                        got -= got % frameSize;
                        left -= got;

                        for (int off = 0; off + frameSize <= got; off += frameSize, frameIndex++)
                        {
                            float sum = 0f;
                            for (int c = 0; c < channels; c++)
                                sum += Sample(buf, off + c * bytesPerSample, bits, format);
                            float v = sum / channels;

                            int bucket = (int)(frameIndex * buckets / frames);
                            if (bucket < 0) bucket = 0;
                            if (bucket >= buckets) bucket = buckets - 1;
                            if (v < mn[bucket]) mn[bucket] = v;
                            if (v > mx[bucket]) mx[bucket] = v;
                        }
                    }

                    for (int i = 0; i < buckets; i++)
                        if (mn[i] > mx[i]) { mn[i] = 0f; mx[i] = 0f; }

                    w.Min = mn; w.Max = mx; w.Ok = true;
                    return w;
                }
            }
            catch { return w; }
        }

        /// <summary>A chunk identifier — strictly four bytes rather than four characters:
        /// BinaryReader.ReadChars in UTF-8 would eat more on a junk chunk.</summary>
        static string Tag(BinaryReader r)
        {
            byte[] b = r.ReadBytes(4);
            return b.Length == 4 ? Encoding.ASCII.GetString(b) : "";
        }

        static float Sample(byte[] b, int i, int bits, int format)
        {
            if (bits == 32 && format == 3) return BitConverter.ToSingle(b, i);
            if (bits == 64) return (float)BitConverter.ToDouble(b, i);
            return MediaDecoder.Pcm(b, i, bits);
        }
    }
}
