using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Xml;

namespace AbletonManager
{
    public enum PluginKind { Vst2, Vst3, AudioUnit, MaxForLive }

    public sealed class PluginRef
    {
        public PluginKind Kind;
        public string Name = "";
        public string Uid = "";           // «vst3:ed57bd72-...» или «vst2:2017543218»
        public string Manufacturer = "";  // из BrowserContentPath, см. AlsFile.ParseBrowserPath
        public bool VendorConfident;      // true только для формы VST3:Вендор:Имя

        internal readonly int[] Vst3Fields = new int[4];
        internal int Vst3FieldCount;
        internal long Vst2UniqueId = long.MinValue;

        public string Key { get { return Kind + "|" + Name.ToLowerInvariant(); } }

        /// <summary>
        /// Собирает Uid в том же виде, в каком его пишет сама Live в PluginScanDb.txt:
        /// четыре Fields.* — это те же 16 байт класса VST3, записанные как четыре int
        /// со старшего байта. Fields «-313016974, 1549813374, -1504849164, 7703407»
        /// превращаются в «ed57bd72-5c60-467e-a64d-d2f400758b6f» — ровно то, что у Live
        /// значится для FabFilter Pro-Q 4.
        /// </summary>
        internal void FinishUid()
        {
            if (Kind == PluginKind.Vst3 && Vst3FieldCount == 4)
            {
                string hex = "";
                for (int i = 0; i < 4; i++)
                    hex += ((uint)Vst3Fields[i]).ToString("x8");
                Uid = "vst3:" + hex.Substring(0, 8) + "-" + hex.Substring(8, 4) + "-"
                    + hex.Substring(12, 4) + "-" + hex.Substring(16, 4) + "-" + hex.Substring(20, 12);
            }
            else if (Kind == PluginKind.Vst2 && Vst2UniqueId != long.MinValue)
            {
                Uid = "vst2:" + Vst2UniqueId.ToString(CultureInfo.InvariantCulture);
            }
        }
    }

    public sealed class FileRefInfo
    {
        public string RelativePath = "";
        public string AbsolutePath = "";
        public string LivePackName = "";
        public int RelativePathType;
        public long OriginalFileSize;

        /// <summary>Имя узла-родителя: SampleRef, FilePresetRef, OriginalFileRef и т.п.</summary>
        public string Container = "";

        /// <summary>
        /// Настоящая зависимость сета — только сэмпл клипа (родитель SampleRef). Всё
        /// прочее (FilePresetRef, AbletonDefaultPresetRef, OriginalFileRef, Max-патчи) —
        /// это происхождение встроенного содержимого: Ableton хранит его прямо в сете и
        /// при открытии на отсутствие файла не жалуется, а путь ведёт в лучшем случае на
        /// чужую машину. Считать такое потерянным — как раз тот баг, что заметен глазом.
        /// </summary>
        public bool IsSampleDependency
        {
            get { return string.Equals(Container, "SampleRef", StringComparison.Ordinal); }
        }

        public string Extension
        {
            get
            {
                string p = RelativePath.Length > 0 ? RelativePath : AbsolutePath;
                try { return System.IO.Path.GetExtension(p).ToLowerInvariant(); }
                catch { return ""; }
            }
        }
    }

    public sealed class AlsInfo
    {
        public string Path = "";
        public string Creator = "";        // "Ableton Live 12.3.5"
        public double Tempo;

        // Общая тональность сета. -1 — версия Live её не сохраняла.
        public int ScaleRoot = -1;
        public int ScaleIndex = -1;
        public bool PreferFlat;

        public string Key { get { return Scales.Format(ScaleRoot, ScaleIndex, PreferFlat); } }
        public int AudioTracks, MidiTracks, GroupTracks;
        public readonly List<PluginRef> Plugins = new List<PluginRef>();
        public readonly List<FileRefInfo> Files = new List<FileRefInfo>();
        public string Error;

        public int TotalTracks { get { return AudioTracks + MidiTracks + GroupTracks; } }
    }

    /// <summary>
    /// Читает .als. Формат: gzip поверх XML — не tar, как иногда пишут.
    /// Разбор потоковый (XmlReader прямо поверх GZipStream): типичный сет — 100 КБ на
    /// диске и под 3 МБ XML после распаковки, а сетов тут больше тысячи, так что
    /// поднимать целиком в память каждый файл нельзя.
    /// </summary>
    public static class AlsFile
    {
        /// <summary>Общие настройки чтения — те же и для разбора аранжировки.</summary>
        internal static XmlReaderSettings XmlSettings()
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Ignore;
            settings.IgnoreComments = true;
            settings.IgnoreWhitespace = true;
            settings.CheckCharacters = false;
            return settings;
        }

        public static AlsInfo Read(string path)
        {
            AlsInfo info = new AlsInfo();
            info.Path = path;
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite, 64 * 1024))
                using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
                using (XmlReader r = XmlReader.Create(gz, XmlSettings()))
                    Parse(r, info);
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }

        static void Parse(XmlReader r, AlsInfo info)
        {
            List<string> stack = new List<string>();

            // Контекст: в каком узле мы сейчас находимся.
            int mainTrackDepth = -1;
            int pluginDepth = -1;
            PluginRef plugin = null;
            int fileRefDepth = -1;
            FileRefInfo fileRef = null;
            bool tempoSeen = false;
            int tempoDepth = -1;

            // BrowserContentPath идёт РАНЬШЕ PluginDesc внутри того же устройства,
            // поэтому запоминаем последний увиденный и отдаём его ближайшему плагину.
            string pendingManufacturer = null;
            bool pendingConfident = false;

            // Узел ScaleInformation есть и у каждого клипа — нас интересует только тот,
            // что лежит прямо в LiveSet: это общая тональность проекта.
            int songScaleDepth = -1;

            while (r.Read())
            {
                if (r.NodeType == XmlNodeType.EndElement)
                {
                    int d = stack.Count - 1;
                    if (d >= 0) stack.RemoveAt(d);

                    if (pluginDepth >= 0 && stack.Count <= pluginDepth)
                    {
                        if (plugin != null && plugin.Name.Length > 0)
                        {
                            plugin.FinishUid();
                            info.Plugins.Add(plugin);
                        }
                        plugin = null; pluginDepth = -1;
                    }
                    if (fileRefDepth >= 0 && stack.Count <= fileRefDepth)
                    {
                        if (fileRef != null) info.Files.Add(fileRef);
                        fileRef = null; fileRefDepth = -1;
                    }
                    if (mainTrackDepth >= 0 && stack.Count <= mainTrackDepth) mainTrackDepth = -1;
                    if (tempoDepth >= 0 && stack.Count <= tempoDepth) tempoDepth = -1;
                    if (songScaleDepth >= 0 && stack.Count <= songScaleDepth) songScaleDepth = -1;
                    continue;
                }

                if (r.NodeType != XmlNodeType.Element) continue;

                string name = r.Name;
                bool empty = r.IsEmptyElement;
                string parent = stack.Count > 0 ? stack[stack.Count - 1] : "";
                int depth = stack.Count;

                switch (name)
                {
                    case "Ableton":
                        info.Creator = r.GetAttribute("Creator") ?? "";
                        break;

                    case "AudioTrack": info.AudioTracks++; break;
                    case "MidiTrack": info.MidiTracks++; break;
                    case "GroupTrack": info.GroupTracks++; break;
                    case "MainTrack":
                    case "MasterTrack":                 // до Live 12 назывался так
                        if (!empty) mainTrackDepth = depth;
                        break;

                    case "BrowserContentPath":
                        bool conf;
                        string m = ParseBrowserPath(r.GetAttribute("Value"), out conf);
                        if (m != null) { pendingManufacturer = m; pendingConfident = conf; }
                        break;

                    case "Vst3PluginInfo":
                    case "VstPluginInfo":
                    case "AuPluginInfo":
                        plugin = new PluginRef();
                        plugin.Kind = name == "Vst3PluginInfo" ? PluginKind.Vst3
                                    : name == "VstPluginInfo" ? PluginKind.Vst2
                                    : PluginKind.AudioUnit;
                        pluginDepth = depth;
                        if (pendingManufacturer != null)
                        {
                            plugin.Manufacturer = pendingManufacturer;
                            plugin.VendorConfident = pendingConfident;
                            pendingManufacturer = null;   // одному плагину — один путь
                            pendingConfident = false;
                        }
                        break;

                    case "FileRef":
                        if (!empty)
                        {
                            fileRef = new FileRefInfo();
                            fileRef.Container = parent;   // SampleRef = реальный сэмпл, остальное — происхождение
                            fileRefDepth = depth;
                        }
                        break;

                    case "Tempo":
                        if (!empty && mainTrackDepth >= 0 && !tempoSeen) tempoDepth = depth;
                        break;

                    case "ScaleInformation":
                        if (!empty && parent == "LiveSet") songScaleDepth = depth;
                        break;

                    case "PreferFlatRootNote":
                        if (parent == "LiveSet")
                            info.PreferFlat = string.Equals(r.GetAttribute("Value"), "true",
                                                           StringComparison.OrdinalIgnoreCase);
                        break;
                }

                // --- значения внутри уже открытых узлов
                if (plugin != null && depth == pluginDepth + 1)
                {
                    if (name == "PlugName" || name == "Name") plugin.Name = Attr(r, plugin.Name);
                    else if (name == "UniqueId") plugin.Vst2UniqueId = LongAttr(r);
                    else if (name == "Manufacturer")
                    {
                        // У Audio Unit вендор записан прямо в узле — источник надёжный.
                        string v = Attr(r, "");
                        if (v.Length > 0) { plugin.Manufacturer = v; plugin.VendorConfident = true; }
                    }
                }

                // <Vst3PluginInfo><Uid><Fields.0../></Uid> — берём только эти четыре,
                // узлы с таким же именем в блоке пресета сюда попадать не должны.
                if (plugin != null && parent == "Uid" && depth == pluginDepth + 2
                    && name.StartsWith("Fields.") && plugin.Vst3FieldCount < 4)
                {
                    int slot;
                    if (int.TryParse(name.Substring(7), out slot) && slot >= 0 && slot < 4)
                    {
                        plugin.Vst3Fields[slot] = IntAttr(r);
                        plugin.Vst3FieldCount++;
                    }
                }

                if (fileRef != null && depth == fileRefDepth + 1)
                {
                    switch (name)
                    {
                        case "RelativePath": fileRef.RelativePath = Attr(r, ""); break;
                        case "Path": fileRef.AbsolutePath = Attr(r, ""); break;
                        case "LivePackName": fileRef.LivePackName = Attr(r, ""); break;
                        case "RelativePathType": fileRef.RelativePathType = IntAttr(r); break;
                        case "OriginalFileSize": fileRef.OriginalFileSize = LongAttr(r); break;
                    }
                }

                if (tempoDepth >= 0 && name == "Manual" && depth == tempoDepth + 1)
                {
                    double t;
                    if (double.TryParse(r.GetAttribute("Value"), NumberStyles.Float,
                                        CultureInfo.InvariantCulture, out t) && t > 0)
                    { info.Tempo = t; tempoSeen = true; }
                }

                if (songScaleDepth >= 0 && depth == songScaleDepth + 1)
                {
                    if (name == "Root") info.ScaleRoot = IntAttr(r);
                    else if (name == "Name") info.ScaleIndex = IntAttr(r);
                }

                if (!empty) stack.Add(name);
            }
        }

        /// <summary>
        /// Разработчик плагина прячется в пути браузера, который Live записывает рядом
        /// с устройством:
        ///     query:Plugins#VST3:FabFilter:Pro-Q%203   -> формат, разработчик, имя
        ///     view:X-Plugins#Antares:Auto-Tune%20Pro   -> разработчик, имя
        ///     view:X-Plugins#Decapitator               -> только имя
        /// Пути вида query:Everything#Reverb принадлежат встроенным устройствам Live и
        /// сюда попадать не должны — их отсекаем по отсутствию "Plugins#".
        /// </summary>
        internal static string ParseBrowserPath(string value, out bool confident)
        {
            confident = false;
            if (string.IsNullOrEmpty(value)) return null;
            if (value.IndexOf("Plugins#", StringComparison.OrdinalIgnoreCase) < 0) return null;

            int hash = value.IndexOf('#');
            if (hash < 0 || hash + 1 >= value.Length) return null;

            string[] parts = value.Substring(hash + 1).Split(':');
            string manufacturer = null;
            if (parts.Length >= 3)
            {
                // query:Plugins#<ФОРМАТ>:<...>:<имя>. Настоящий вендор тут только у VST3
                // и AU — Live берёт его из самого плагина. У VST2 (формат "VST") на этом
                // месте оказывается папка, в которой лежит .dll, то есть «Gen» или «Eff».
                manufacturer = parts[parts.Length - 2];
                string format = parts[0];
                confident = format.Equals("VST3", StringComparison.OrdinalIgnoreCase)
                         || format.Equals("AU", StringComparison.OrdinalIgnoreCase)
                         || format.Equals("AudioUnit", StringComparison.OrdinalIgnoreCase);
            }
            else if (parts.Length == 2)
            {
                // Форма view:X-Plugins#Antares:Auto-Tune Pro — тоже папка браузера.
                manufacturer = parts[0];
            }
            if (string.IsNullOrEmpty(manufacturer)) return null;

            try { manufacturer = Uri.UnescapeDataString(manufacturer); } catch { }
            return manufacturer.Trim();
        }

        static string Attr(XmlReader r, string fallback)
        {
            string v = r.GetAttribute("Value");
            return string.IsNullOrEmpty(v) ? fallback : v;
        }

        static int IntAttr(XmlReader r)
        {
            int v; int.TryParse(r.GetAttribute("Value"), out v); return v;
        }

        static long LongAttr(XmlReader r)
        {
            long v; long.TryParse(r.GetAttribute("Value"), out v); return v;
        }
    }
}
