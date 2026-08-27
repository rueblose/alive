using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using AbletonManager;

namespace Reel
{
    /// <summary>Одно устройство в цепочке трека.</summary>
    public sealed class DeviceEntry
    {
        public string Class = "";      // имя XML-узла: PluginDevice, Compressor2, Eq8...
        public string Display = "";    // что видит человек: имя плагина либо имя устройства Live
        public bool On = true;
        public bool IsPlugin;

        public string Label { get { return Display.Length > 0 ? Display : Pretty(Class); } }

        /// <summary>«AudioEffectGroupDevice» -> «Audio Effect Rack», «Eq8» -> «EQ Eight».</summary>
        internal static string Pretty(string cls)
        {
            switch (cls)
            {
                case "AudioEffectGroupDevice": return "Audio Effect Rack";
                case "InstrumentGroupDevice": return "Instrument Rack";
                case "MidiEffectGroupDevice": return "MIDI Effect Rack";
                case "DrumGroupDevice": return "Drum Rack";
                case "OriginalSimpler": return "Simpler";
                case "MultiSampler": return "Sampler";
                case "Eq8": return "EQ Eight";
                case "Compressor2": return "Compressor";
                case "FilterEQ3": return "EQ Three";
                case "MxDeviceAudioEffect":
                case "MxDeviceInstrument":
                case "MxDeviceMidiEffect": return "Max for Live device";
            }
            // CamelCase -> «Camel Case»: имена узлов Live почти всегда читаемы как есть.
            StringBuilder sb = new StringBuilder(cls.Length + 4);
            for (int i = 0; i < cls.Length; i++)
            {
                if (i > 0 && char.IsUpper(cls[i]) && !char.IsUpper(cls[i - 1])) sb.Append(" ");
                sb.Append(cls[i]);
            }
            return sb.ToString();
        }
    }

    /// <summary>Сэмпл, на который ссылается трек.</summary>
    public sealed class SampleRefEntry
    {
        public string Name = "";     // только имя файла — его и показываем
        public string Path = "";     // как записано в сете, для подсказки
        public long Size;            // OriginalFileSize, если Live его записала

        /// <summary>
        /// Чем сэмпл опознаётся между версиями — именем файла, и только им. Оба
        /// очевидных кандидата на роль ключа проверены на живых сетах и оба врут:
        ///
        ///   - ПУТЬ. Live переписывает ссылку на один и тот же файл по-разному от версии
        ///     к версии: 12.4.3 при пересохранении выбрасывает RelativePath целиком
        ///     (RelativePathType 1 -> 0) и оставляет только абсолютный. По пути сет
        ///     выглядел бы как «все сэмплы заменены на одноимённые».
        ///
        ///   - РАЗМЕР. OriginalFileSize записывается не всегда: у одного и того же
        ///     Kanun C3.aif из Core Library в одном сете стоит 852036, в другом 0.
        ///
        /// Имя не различает два разных файла с одинаковым именем — но ровно это же имя
        /// стоит и в строке разницы, так что точность ключа равна точности показанного.
        /// </summary>
        public string Key { get { return Name.ToLowerInvariant(); } }
    }

    /// <summary>Трек со всем, по чему имеет смысл сравнивать две версии сета.</summary>
    public sealed class TrackEntry
    {
        public int Id = -1;
        public string Kind = "";           // audio | midi | group | return | main
        public string Name = "";
        public int GroupId = -1;
        public double Volume = double.NaN; // линейный коэффициент, ровно как в .als
        public double Pan;
        public bool Muted;
        public int ArrClips, SessionClips, Notes;

        public readonly List<DeviceEntry> Devices = new List<DeviceEntry>();
        public readonly List<SampleRefEntry> Samples = new List<SampleRefEntry>();
        internal readonly Dictionary<string, SampleRefEntry> SampleSet =
            new Dictionary<string, SampleRefEntry>(StringComparer.OrdinalIgnoreCase);

        public string Label { get { return Name.Length > 0 ? Name : Kind + " " + Id; } }

        /// <summary>
        /// Чем трек опознаётся между сохранениями. Id живёт вместе с треком и переживает
        /// переименование — иначе «Bass» -> «Reese» выглядело бы как удаление трека и
        /// добавление нового, а это самая частая правка вообще.
        /// </summary>
        public string Key { get { return Kind + "#" + Id; } }

        public static string Db(double linear)
        {
            if (double.IsNaN(linear)) return "?";
            if (linear <= 0.0000001) return "-inf";
            return (20.0 * Math.Log10(linear)).ToString("0.0", CultureInfo.InvariantCulture) + " dB";
        }
    }

    /// <summary>
    /// Структурный слепок сета: треки, их устройства, клипы, ноты, сэмплы, микшер.
    ///
    /// Зачем отдельно от AlsFile: тот собирает сводку по сету целиком (сколько треков,
    /// какие плагины, какие файлы) — этого хватает каталогу, но не хватает разнице между
    /// версиями. «Serum пропал» бесполезно, пока не сказано, с какого трека. Поэтому тут
    /// тот же однопроходный XmlReader, но контекст держится по трекам.
    /// </summary>
    public sealed class SetModel
    {
        public string Path = "";
        public string Creator = "";
        public double Tempo;
        public int ScaleRoot = -1, ScaleIndex = -1;
        public bool PreferFlat;
        public readonly List<TrackEntry> Tracks = new List<TrackEntry>();
        public string Error;

        public string Key { get { return Scales.Format(ScaleRoot, ScaleIndex, PreferFlat); } }

        public int TotalClips
        {
            get { int n = 0; foreach (TrackEntry t in Tracks) n += t.ArrClips + t.SessionClips; return n; }
        }

        public int TotalNotes
        {
            get { int n = 0; foreach (TrackEntry t in Tracks) n += t.Notes; return n; }
        }

        public int DeviceCount
        {
            get { int n = 0; foreach (TrackEntry t in Tracks) n += t.Devices.Count; return n; }
        }

        public static SetModel Read(string path)
        {
            SetModel m = new SetModel();
            m.Path = path;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite, 64 * 1024))
                using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                using (XmlReader r = XmlReader.Create(gz, AlsFile.XmlSettings()))
                    Parse(r, m);
            }
            catch (Exception ex)
            {
                m.Error = ex.Message;
            }
            return m;
        }

        static void Parse(XmlReader r, SetModel m)
        {
            List<string> stack = new List<string>();

            int trackDepth = -1; TrackEntry track = null;
            int nameDepth = -1;                    // <Name><EffectiveName/></Name> самого трека
            int devicesDepth = -1; bool devicesTaken = false;
            int deviceDepth = -1; DeviceEntry device = null;
            int pluginInfoDepth = -1, onDepth = -1;
            int mixerDepth = -1, volDepth = -1, panDepth = -1, speakerDepth = -1;
            int tempoDepth = -1;
            int arrangerDepth = -1;                // внутри аранжировки, а не сессии
            int fileRefDepth = -1; bool fileRefIsSample = false;
            string fileRel = null, fileAbs = null; long fileSize = 0;
            int songScaleDepth = -1;

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    int d = stack.Count - 1;
                    if (d >= 0) stack.RemoveAt(d);
                    int now = stack.Count;

                    if (fileRefDepth >= 0 && now <= fileRefDepth)
                    {
                        if (fileRefIsSample && track != null)
                        {
                            string s = !string.IsNullOrEmpty(fileRel) ? fileRel : fileAbs;
                            if (!string.IsNullOrEmpty(s))
                            {
                                SampleRefEntry sample = new SampleRefEntry();
                                sample.Path = s;
                                sample.Size = fileSize;
                                try { sample.Name = System.IO.Path.GetFileName(s.Replace('/', '\\')); }
                                catch { sample.Name = s; }

                                if (!track.SampleSet.ContainsKey(sample.Key))
                                {
                                    track.SampleSet[sample.Key] = sample;
                                    track.Samples.Add(sample);
                                }
                            }
                        }
                        fileRefDepth = -1; fileRefIsSample = false;
                        fileRel = null; fileAbs = null; fileSize = 0;
                    }
                    if (onDepth >= 0 && now <= onDepth) onDepth = -1;
                    if (pluginInfoDepth >= 0 && now <= pluginInfoDepth) pluginInfoDepth = -1;
                    if (deviceDepth >= 0 && now <= deviceDepth) { device = null; deviceDepth = -1; }
                    if (devicesDepth >= 0 && now <= devicesDepth) devicesDepth = -1;
                    if (volDepth >= 0 && now <= volDepth) volDepth = -1;
                    if (panDepth >= 0 && now <= panDepth) panDepth = -1;
                    if (speakerDepth >= 0 && now <= speakerDepth) speakerDepth = -1;
                    if (mixerDepth >= 0 && now <= mixerDepth) mixerDepth = -1;
                    if (tempoDepth >= 0 && now <= tempoDepth) tempoDepth = -1;
                    if (arrangerDepth >= 0 && now <= arrangerDepth) arrangerDepth = -1;
                    if (nameDepth >= 0 && now <= nameDepth) nameDepth = -1;
                    if (trackDepth >= 0 && now <= trackDepth)
                    {
                        track = null; trackDepth = -1; devicesTaken = false; devicesDepth = -1;
                    }
                    if (songScaleDepth >= 0 && now <= songScaleDepth) songScaleDepth = -1;
                    continue;
                }

                if (r.NodeType != XmlNodeType.Element) continue;

                string name = r.Name;
                bool empty = r.IsEmptyElement;
                string parent = stack.Count > 0 ? stack[stack.Count - 1] : "";
                int depth = stack.Count;

                // ---------------------------------------------------------- сет целиком
                if (name == "Ableton") m.Creator = r.GetAttribute("Creator") ?? "";
                else if (name == "ScaleInformation" && !empty && parent == "LiveSet") songScaleDepth = depth;
                else if (name == "PreferFlatRootNote" && parent == "LiveSet")
                    m.PreferFlat = string.Equals(r.GetAttribute("Value"), "true", StringComparison.OrdinalIgnoreCase);

                if (songScaleDepth >= 0 && depth == songScaleDepth + 1)
                {
                    if (name == "Root") m.ScaleRoot = IntAttr(r);
                    else if (name == "Name") m.ScaleIndex = IntAttr(r);
                }

                // ---------------------------------------------------------- начало трека
                if (track == null && !empty)
                {
                    string kind = TrackKind(name);
                    if (kind != null)
                    {
                        track = new TrackEntry();
                        track.Kind = kind;
                        int id; if (int.TryParse(r.GetAttribute("Id"), out id)) track.Id = id;
                        trackDepth = depth;
                        m.Tracks.Add(track);
                    }
                }

                if (track != null)
                {
                    // имя: <Name><EffectiveName Value="Bass"/></Name> прямо у трека
                    if (name == "Name" && !empty && depth == trackDepth + 1) nameDepth = depth;
                    else if (nameDepth >= 0 && depth == nameDepth + 1 && name == "EffectiveName")
                        track.Name = Attr(r, track.Name);
                    else if (name == "TrackGroupId" && depth == trackDepth + 1) track.GroupId = IntAttr(r);

                    // микшер: громкость, панорама, выключенный Speaker = mute
                    if (name == "Mixer" && !empty && mixerDepth < 0) mixerDepth = depth;
                    if (mixerDepth >= 0 && depth == mixerDepth + 1)
                    {
                        if (name == "Volume" && !empty) volDepth = depth;
                        else if (name == "Pan" && !empty) panDepth = depth;
                        else if (name == "Speaker" && !empty) speakerDepth = depth;
                        else if (name == "Tempo" && !empty && track.Kind == "main") tempoDepth = depth;
                    }
                    if (name == "Manual")
                    {
                        if (volDepth >= 0 && depth == volDepth + 1) track.Volume = DoubleAttr(r, track.Volume);
                        else if (panDepth >= 0 && depth == panDepth + 1) track.Pan = DoubleAttr(r, track.Pan);
                        else if (speakerDepth >= 0 && depth == speakerDepth + 1)
                            track.Muted = !string.Equals(r.GetAttribute("Value"), "true", StringComparison.OrdinalIgnoreCase);
                        else if (tempoDepth >= 0 && depth == tempoDepth + 1)
                        {
                            double t = DoubleAttr(r, 0);
                            if (t > 0) m.Tempo = t;
                        }
                    }

                    // ------------------------------------------------------ устройства
                    //
                    // Берём ТОЛЬКО первый узел Devices трека и только его прямых детей.
                    // Внутри рэков лежат свои Devices — они глубже, и их содержимое сюда
                    // не попадает: рэк в разнице показывается одной строкой, как его и
                    // видит человек в цепочке.
                    if (name == "Devices" && !empty && !devicesTaken)
                    {
                        devicesDepth = depth; devicesTaken = true;
                    }
                    else if (devicesDepth >= 0 && depth == devicesDepth + 1 && !empty)
                    {
                        device = new DeviceEntry();
                        device.Class = name;
                        deviceDepth = depth;
                        track.Devices.Add(device);
                    }

                    if (device != null)
                    {
                        if (depth == deviceDepth + 1)
                        {
                            if (name == "On" && !empty) onDepth = depth;
                            else if (name == "UserName")
                            {
                                string v = Attr(r, "");
                                if (v.Length > 0) device.Display = v;
                            }
                        }
                        else if (onDepth >= 0 && depth == onDepth + 1 && name == "Manual")
                            device.On = string.Equals(r.GetAttribute("Value"), "true", StringComparison.OrdinalIgnoreCase);

                        if (name == "Vst3PluginInfo" || name == "VstPluginInfo" || name == "AuPluginInfo")
                        {
                            pluginInfoDepth = depth; device.IsPlugin = true;
                        }
                        else if (pluginInfoDepth >= 0 && depth == pluginInfoDepth + 1
                                 && (name == "PlugName" || name == "Name"))
                        {
                            // имя, данное пользователем, важнее имени из плагина
                            string v = Attr(r, "");
                            if (v.Length > 0 && device.Display.Length == 0) device.Display = v;
                        }
                    }

                    // ------------------------------------------------------ клипы и ноты
                    if (name == "ArrangerAutomation" && !empty && arrangerDepth < 0) arrangerDepth = depth;
                    if (name == "MidiClip" || name == "AudioClip")
                    {
                        if (arrangerDepth >= 0) track.ArrClips++; else track.SessionClips++;
                    }
                    else if (name == "MidiNoteEvent") track.Notes++;

                    // ------------------------------------------------------ сэмплы трека
                    if (name == "FileRef" && !empty && fileRefDepth < 0)
                    {
                        fileRefDepth = depth;
                        // настоящая зависимость — только сэмпл клипа, всё прочее Live
                        // хранит внутри сета (см. FORMAT.md)
                        fileRefIsSample = string.Equals(parent, "SampleRef", StringComparison.Ordinal);
                    }
                    else if (fileRefDepth >= 0 && depth == fileRefDepth + 1)
                    {
                        if (name == "RelativePath") fileRel = Attr(r, fileRel);
                        else if (name == "Path") fileAbs = Attr(r, fileAbs);
                        else if (name == "OriginalFileSize")
                        {
                            long v;
                            if (long.TryParse(r.GetAttribute("Value"), NumberStyles.Integer,
                                              CultureInfo.InvariantCulture, out v)) fileSize = v;
                        }
                    }
                }

                if (!empty) stack.Add(name);
            }
        }

        static string TrackKind(string node)
        {
            switch (node)
            {
                case "AudioTrack": return "audio";
                case "MidiTrack": return "midi";
                case "GroupTrack": return "group";
                case "ReturnTrack": return "return";
                case "MainTrack":
                case "MasterTrack": return "main";   // до Live 12 назывался MasterTrack
            }
            return null;
        }

        static string Attr(XmlReader r, string fallback)
        {
            string v = r.GetAttribute("Value");
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        static int IntAttr(XmlReader r) { int v; int.TryParse(r.GetAttribute("Value"), out v); return v; }

        static double DoubleAttr(XmlReader r, double fallback)
        {
            double v;
            if (double.TryParse(r.GetAttribute("Value"), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out v)) return v;
            return fallback;
        }
    }
}
