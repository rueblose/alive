using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace AbletonManager
{
    /// <summary>
    /// Проигрывание файла: Media Foundation декодирует в PCM, waveOut его выводит.
    /// Напрашивавшийся MCI (mciSendString) пришлось выбросить — на реальной библиотеке
    /// он открывал 4 файла из 26: спотыкался о дефисы и скобки в именах, о теги в mp3
    /// и просто не находил драйвер. Media Foundation берёт те же файлы все до одного,
    /// а раз декодированный поток и так у нас в руках, выводить его дальше проще всего
    /// самой простой из звуковых подсистем.
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
            // WAVE_FORMAT_IEEE_FLOAT: Mf.OpenPcm всегда отдаёт декодированный поток
            // как float32 — см. комментарий там про упаковку 24-битных источников.
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

        // Восемь буферов по 16 КБ — около 0,7 с запаса при 44,1 кГц стерео. Меньше —
        // рискуем щелчками на загруженной машине, больше — перемотка ощущается вялой.
        const int Buffers = 8;
        const int BufferBytes = 16384;

        // ------------------------------------------------------------- состояние

        /// <summary>
        /// Одно проигрывание: свой поток, своё устройство waveOut, свои буферы, свои
        /// флаги. Всё железо живёт ЗДЕСЬ, а не полями самого AudioPlayer, и это не
        /// аккуратизм ради аккуратизма.
        ///
        /// Раньше поля устройства были общими на весь плеер, а Close() при не успевшем
        /// закончиться потоке звал Thread.Abort. Оба исхода плохи: Abort мог оборвать
        /// поток прямо в finally — и устройство осталось бы открытым до конца работы
        /// программы; а не звать его было нельзя, потому что следующий трек тут же
        /// полез бы выделять буферы в те же самые поля, из которых предыдущий поток
        /// ещё не доосвобождался (это уже порча памяти, а не утечка).
        ///
        /// С отдельным комплектом на каждое проигрывание выбор исчезает: застрявший
        /// поток спокойно доубирает СВОЁ, ни с кем ничего не деля, а новый трек играет,
        /// не дожидаясь его.
        /// </summary>
        sealed class Run
        {
            public readonly string Path;
            public readonly Thread Thread;

            // Gate защищает Hwo от гонки «интерфейс спрашивает позицию ровно в тот
            // момент, когда поток закрывает устройство».
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
            public volatile int KnownMs;   // сколько уже точно есть, когда длина неизвестна заранее

            // Декодер отдаёт куски произвольной длины, а устройству нужен ровно буфер,
            // поэтому остаток от предыдущего чтения переносим в следующий.
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
                // COM-объекты Media Foundation не любят переезжать между апартаментами,
                // а поток вывода всё равно нужен свой.
                Thread.SetApartmentState(ApartmentState.MTA);
            }
        }

        volatile Run _run;
        float _volume = 0.8f;

        public string Path = "";

        /// <summary>Почему не заиграло. Пусто, пока всё в порядке.</summary>
        public string Error { get { Run r = _run; return r != null ? r.Error : ""; } }

        /// <summary>Длительность в миллисекундах; 0, пока она вообще неизвестна.</summary>
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

        /// <summary>Файл ещё открывается. Пока так, транспорт трогать бессмысленно.</summary>
        public bool IsOpening { get { Run r = _run; return r != null && r.Opening; } }

        /// <summary>
        /// Не заиграло — причина в <see cref="Error"/>. Смотрим именно на текст ошибки,
        /// а не на «не открыт и уже не открывается»: доигравший до конца трек тоже
        /// в конце концов перестаёт быть открытым, и по такому признаку успешное
        /// проигрывание было бы объявлено неудачей.
        /// </summary>
        public bool OpenFailed
        {
            get { Run r = _run; return r != null && !r.Opening && r.Error.Length > 0; }
        }

        /// <summary>Трек доиграл до конца сам — сигнал плейлисту переходить дальше.</summary>
        public bool Finished { get { Run r = _run; return r != null && r.Done; } }

        public bool IsPlaying
        {
            get { Run r = _run; return r != null && r.OpenOk && !r.Paused && !r.Done; }
        }

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

        // ---------------------------------------------------------------- открыть

        /// <summary>
        /// Начинает проигрывание. НЕ ЖДЁТ, пока файл откроется: раньше здесь стоял
        /// _opened.WaitOne(8000), и весь интерфейс замирал ровно на столько, сколько
        /// системный декодер возился с файлом — на медленном или сетевом диске это
        /// секунды намертво замершего окна по одному нажатию play.
        ///
        /// Чем всё кончилось, спрашивают потом: IsOpening / IsOpen / OpenFailed.
        /// Плеер и так опрашивает состояние своим таймером, так что отдельный
        /// механизм оповещения не нужен.
        /// </summary>
        public void Open(string path, bool autoStart)
        {
            Close();
            Path = path;

            Run r = new Run(path, _volume, autoStart, Pump);
            _run = r;

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                r.Error = L.S("File is gone", "Файла больше нет");
                r.Opening = false;          // поток не запускаем — открывать нечего
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
        /// Просим проигрывание закончиться. Thread.Abort тут больше нет — см. комментарий
        /// у Run. Не уложился в срок (обычно это застрявшее чтение с сетевого диска) —
        /// отпускаем: поток фоновый, Stop у него стоит, и, освободившись, он закроет своё
        /// устройство и освободит свои буферы. Делить ему с новым проигрыванием нечего.
        ///
        /// Ждём коротко и только ради одного: чтобы старый трек успел замолчать раньше,
        /// чем зазвучит новый (здоровый поток видит Stop не позже чем через 20 мс, так
        /// что этого с запасом хватает). Прежние две секунды тут были бы прямым
        /// возвратом к тому, от чего уходили, — Close() зовётся из Open(), и весь
        /// выигрыш от неблокирующего открытия съедался бы ожиданием предыдущего.
        /// </summary>
        static void StopRun(Run r)
        {
            if (r == null) return;
            r.Stop = true;
            if (r.Thread == null || !r.Thread.IsAlive) return;
            try { r.Thread.Join(300); } catch { }
        }

        // -------------------------------------------------------------- транспорт

        public void Play()
        {
            Run r = _run;
            if (r == null) return;
            if (r.Done) r.SeekRequest = 0;
            // Снимаем паузу даже пока идёт открытие: поток, дойдя до готовности,
            // сам увидит Paused=false и начнёт играть. Иначе нажатие play в первую
            // секунду после выбора трека молча пропадало бы.
            r.Paused = false;
        }

        public void PlayFrom(int ms)
        {
            Seek(ms);
            Play();
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

        // ------------------------------------------------------- поток вывода

        void Pump(object arg)
        {
            Run r = (Run)arg;
            bool mfStarted = false;
            IMFSourceReader reader = null;
            byte[] staging = new byte[BufferBytes];

            try
            {
                if (Mf.MFStartup(Mf.Version, 0) != 0)
                { r.Error = L.S("Media Foundation is unavailable", "Media Foundation недоступна"); return; }
                mfStarted = true;

                long durationMs;
                if (!Mf.OpenPcm(r.Path, out reader, out r.Channels, out r.Bits, out r.Rate, out durationMs))
                { r.Error = L.S("No decoder for this file", "Для этого файла нет декодера"); return; }

                // Часть WAV (одиночные сэмплы, файлы без индекса) длительности не
                // сообщает — тогда берём её прямо из заголовка. Если и там пусто,
                // длина дорастёт по мере декодирования: ниже в ReadPcm.
                r.LengthMs = (int)durationMs;
                if (r.LengthMs <= 0) r.LengthMs = WaveReader.RiffDurationMs(r.Path);

                r.FrameSize = r.Bits / 8 * r.Channels;
                if (r.FrameSize <= 0)
                { r.Error = L.S("Unsupported audio format", "Неподдерживаемый формат"); return; }

                WaveFormat fmt = new WaveFormat();
                fmt.Channels = (short)r.Channels;
                fmt.SamplesPerSec = r.Rate;
                fmt.BitsPerSample = (short)r.Bits;
                fmt.BlockAlign = (short)r.FrameSize;
                fmt.AvgBytesPerSec = r.Rate * r.FrameSize;

                IntPtr hwo;
                if (waveOutOpen(out hwo, -1 /* WAVE_MAPPER */, fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0)
                { r.Error = L.S("The sound device is busy", "Звуковое устройство занято"); return; }
                lock (r.Gate) r.Hwo = hwo;

                AllocBuffers(r);
                ApplyVolume(r);
                waveOutPause(hwo);           // ждём команды Play, а не стартуем сами

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
                // Сперва прячем хендл от читателей (Position спрашивает позицию 25 раз
                // в секунду), и только потом закрываем устройство.
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
                r.Opening = false;   // на случай падения до готовности — иначе ждали бы вечно
            }
        }

        static void DoSeek(Run r, IMFSourceReader reader, int ms)
        {
            waveOutReset(r.Hwo);                // сбрасывает очередь и обнуляет позицию
            for (int i = 0; i < Buffers; i++) r.Queued[i] = false;
            r.PendingLen = r.PendingOff = 0;
            Mf.SetPosition(reader, ms);
            r.SeekBaseFrames = (long)ms * r.Rate / 1000;
            r.DecodedFrames = 0;
            r.Eof = false;
            r.Done = false;
        }

        // ---------------------------------------------------------- буферы waveOut

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
            // Под Gate, потому что зовут это из потока интерфейса, а поток вывода в это
            // же время может закрывать устройство: без замка легко спросить позицию у
            // уже закрытого хендла.
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

        // ------------------------------------------------------------ чтение PCM

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

            // Отдаём только целые кадры — половина кадра сдвинула бы каналы местами.
            if (r.FrameSize > 0) written -= written % r.FrameSize;

            // Длина неизвестна ни из метаданных, ни из заголовка — считаем её по факту,
            // чтобы полоса прогресса хотя бы к концу трека стала правдой.
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

    /// <summary>Огибающая: минимум и максимум сигнала в каждом столбце картинки.</summary>
    public sealed class Waveform
    {
        public float[] Min = new float[0];
        public float[] Max = new float[0];
        public bool Ok;
        public string Note = "";
    }

    /// <summary>
    /// Огибающая. WAV разбираем сами — это просто и не поднимает COM ради каждого
    /// трека; всё остальное (и WAV, который нам не дался) уходит в системный декодер.
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
        /// Длительность WAV прямо из заголовка. Нужна потому, что Media Foundation
        /// у части файлов (одиночные сэмплы, записи без индекса) длину не сообщает,
        /// а без неё нечем показать ни время, ни положение на волне.
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
                                format = r.ReadInt16();    // настоящий формат из GUID
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
                    if (format != 1 && format != 3) return w;      // сжатый — пусть его берёт MF

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

        /// <summary>Идентификатор чанка — строго четыре байта, а не четыре символа:
        /// BinaryReader.ReadChars в UTF-8 на мусорном чанке съел бы больше.</summary>
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
