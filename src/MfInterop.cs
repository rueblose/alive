using System;
using System.Runtime.InteropServices;

namespace AbletonManager
{
    /// <summary>
    /// Media Foundation by hand, without a type library. The order of methods in the interfaces
    /// is strictly vertical: a vtable slot is counted from the declaration, so the Slot* stubs
    /// have to stay exactly where they are — one extra or missing method means calling the
    /// wrong address. The stubs are never called.
    /// </summary>
    static class Mf
    {
        public const int Version = 0x00020070;
        public const uint FirstAudioStream = 0xFFFFFFFD;
        public const uint AllStreams = 0xFFFFFFFE;
        public const uint MediaSource = 0xFFFFFFFF;
        public const uint EndOfStream = 0x00000002;

        public static Guid MT_MAJOR_TYPE = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        public static Guid MT_SUBTYPE = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        public static Guid MediaType_Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        public static Guid AudioFormat_Float = new Guid("00000003-0000-0010-8000-00AA00389B71");
        public static Guid MT_AUDIO_NUM_CHANNELS = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        public static Guid MT_AUDIO_BITS_PER_SAMPLE = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        public static Guid MT_AUDIO_SAMPLES_PER_SECOND = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        public static Guid PD_DURATION = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        public static Guid TimeFormat_Null = Guid.Empty;

        [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] public static extern int MFShutdown();
        [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IMFMediaType type);

        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes,
                                                             out IMFSourceReader reader);

        [DllImport("ole32.dll")] public static extern int PropVariantClear(IntPtr pv);

        public static void Release(object com)
        {
            if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
        }

        /// <summary>
        /// Opens a file and asks for it as 32-bit float PCM. Asking for PCM at the source bit
        /// depth turned out to be a trap: a 24-bit source (common for WAV and especially for
        /// FLAC) comes back from MF with the same "bits=24" attribute, but the actual samples
        /// are packed differently from three tight bytes per channel — computing the frame
        /// stride yourself then walks the read off course on every sample, and out comes
        /// exactly that stuttering noise. Float32 is the one format MF returns consistently and
        /// with no packing variants whatever the source file is, which is why the conversion to
        /// it always happens, not only for compressed formats.
        /// </summary>
        public static bool OpenPcm(string path, out IMFSourceReader reader,
                                   out int channels, out int bits, out int rate, out long durationMs)
        {
            reader = null; channels = 0; bits = 0; rate = 0; durationMs = 0;

            if (MFCreateSourceReaderFromURL(path, IntPtr.Zero, out reader) != 0 || reader == null)
                return false;

            reader.SetStreamSelection(AllStreams, false);
            reader.SetStreamSelection(FirstAudioStream, true);

            IMFMediaType want = null, actual = null;
            try
            {
                if (MFCreateMediaType(out want) != 0 || want == null) return false;
                want.SetGUID(ref MT_MAJOR_TYPE, ref MediaType_Audio);
                want.SetGUID(ref MT_SUBTYPE, ref AudioFormat_Float);
                if (reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, want) != 0) return false;
                if (reader.GetCurrentMediaType(FirstAudioStream, out actual) != 0 || actual == null)
                    return false;

                actual.GetUINT32(ref MT_AUDIO_NUM_CHANNELS, out channels);
                actual.GetUINT32(ref MT_AUDIO_BITS_PER_SAMPLE, out bits);
                actual.GetUINT32(ref MT_AUDIO_SAMPLES_PER_SECOND, out rate);
                if (channels <= 0) channels = 2;
                if (bits <= 0) bits = 32;
                if (rate <= 0) rate = 44100;

                durationMs = Duration100Ns(reader) / 10000;
                return true;
            }
            finally { Release(actual); Release(want); }
        }

        /// <summary>Duration in 100-nanosecond ticks; 0 if the source does not know
        /// it.</summary>
        public static long Duration100Ns(IMFSourceReader reader)
        {
            IntPtr pv = Marshal.AllocCoTaskMem(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                if (reader.GetPresentationAttribute(MediaSource, ref PD_DURATION, pv) != 0) return 0;
                long v = Marshal.ReadInt64(pv, 8);
                PropVariantClear(pv);
                return v > 0 ? v : 0;
            }
            catch { return 0; }
            finally { Marshal.FreeCoTaskMem(pv); }
        }

        /// <summary>Seeks the source. The PROPVARIANT with VT_I8 is assembled by hand — for the
        /// sake of one field.</summary>
        public static void SetPosition(IMFSourceReader reader, long ms)
        {
            IntPtr pv = Marshal.AllocCoTaskMem(24);
            try
            {
                for (int i = 0; i < 24; i++) Marshal.WriteByte(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 20);            // VT_I8
                Marshal.WriteInt64(pv, 8, ms * 10000L);   // in 100 ns ticks
                reader.SetCurrentPosition(ref TimeFormat_Null, pv);
            }
            catch { }
            finally { Marshal.FreeCoTaskMem(pv); }
        }
    }

    [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSourceReader
    {
        [PreserveSig] int GetStreamSelection(uint stream, out bool selected);
        [PreserveSig] int SetStreamSelection(uint stream, bool selected);
        [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
        [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
        [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
        [PreserveSig] int SetCurrentPosition(ref Guid format, IntPtr position);
        [PreserveSig] int ReadSample(uint stream, uint flags, out uint actualStream,
                                     out uint streamFlags, out long timestamp, out IMFSample sample);
        [PreserveSig] int Flush(uint stream);
        [PreserveSig] int GetServiceForStream(uint stream, ref Guid service, ref Guid iid,
                                              out IntPtr obj);
        [PreserveSig] int GetPresentationAttribute(uint stream, ref Guid key, IntPtr value);
    }

    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaType
    {
        [PreserveSig] int Slot01(); [PreserveSig] int Slot02(); [PreserveSig] int Slot03();
        [PreserveSig] int Slot04();
        [PreserveSig] int GetUINT32(ref Guid key, out int value);
        [PreserveSig] int Slot06(); [PreserveSig] int Slot07(); [PreserveSig] int Slot08();
        [PreserveSig] int Slot09(); [PreserveSig] int Slot10(); [PreserveSig] int Slot11();
        [PreserveSig] int Slot12(); [PreserveSig] int Slot13(); [PreserveSig] int Slot14();
        [PreserveSig] int Slot15(); [PreserveSig] int Slot16(); [PreserveSig] int Slot17();
        [PreserveSig] int Slot18(); [PreserveSig] int Slot19(); [PreserveSig] int Slot20();
        [PreserveSig] int Slot21();
        [PreserveSig] int SetGUID(ref Guid key, ref Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        // The 30 IMFAttributes methods, then eight of its own — and only then the one we need.
        [PreserveSig] int Slot01(); [PreserveSig] int Slot02(); [PreserveSig] int Slot03();
        [PreserveSig] int Slot04(); [PreserveSig] int Slot05(); [PreserveSig] int Slot06();
        [PreserveSig] int Slot07(); [PreserveSig] int Slot08(); [PreserveSig] int Slot09();
        [PreserveSig] int Slot10(); [PreserveSig] int Slot11(); [PreserveSig] int Slot12();
        [PreserveSig] int Slot13(); [PreserveSig] int Slot14(); [PreserveSig] int Slot15();
        [PreserveSig] int Slot16(); [PreserveSig] int Slot17(); [PreserveSig] int Slot18();
        [PreserveSig] int Slot19(); [PreserveSig] int Slot20(); [PreserveSig] int Slot21();
        [PreserveSig] int Slot22(); [PreserveSig] int Slot23(); [PreserveSig] int Slot24();
        [PreserveSig] int Slot25(); [PreserveSig] int Slot26(); [PreserveSig] int Slot27();
        [PreserveSig] int Slot28(); [PreserveSig] int Slot29(); [PreserveSig] int Slot30();
        [PreserveSig] int GetSampleFlags(out uint flags);
        [PreserveSig] int SetSampleFlags(uint flags);
        [PreserveSig] int GetSampleTime(out long time);
        [PreserveSig] int SetSampleTime(long time);
        [PreserveSig] int GetSampleDuration(out long duration);
        [PreserveSig] int SetSampleDuration(long duration);
        [PreserveSig] int GetBufferCount(out uint count);
        [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
        [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
        [PreserveSig] int Unlock();
        [PreserveSig] int GetCurrentLength(out int length);
        [PreserveSig] int SetCurrentLength(int length);
        [PreserveSig] int GetMaxLength(out int length);
    }
}
