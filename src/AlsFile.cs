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
        public string Uid = "";           // "vst3:ed57bd72-..." or "vst2:2017543218"
        public string Manufacturer = "";  // from BrowserContentPath, see AlsFile.ParseBrowserPath
        public bool VendorConfident;      // true only for the VST3:Vendor:Name form

        internal readonly int[] Vst3Fields = new int[4];
        internal int Vst3FieldCount;
        internal long Vst2UniqueId = long.MinValue;

        public string Key { get { return Kind + "|" + Name.ToLowerInvariant(); } }

        /// <summary>
        /// Assembles the Uid in the same shape Live itself writes into PluginScanDb.txt: the
        /// four Fields.* are the same 16 bytes of a VST3 class written as four ints, most
        /// significant byte first. Fields "-313016974, 1549813374, -1504849164, 7703407" turn
        /// into "ed57bd72-5c60-467e-a64d-d2f400758b6f" — exactly what Live lists for FabFilter
        /// Pro-Q 4.
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

        /// <summary>The parent node's name: SampleRef, FilePresetRef, OriginalFileRef and so
        /// on.</summary>
        public string Container = "";

        /// <summary>
        /// A set's real dependency is only a clip's sample (parent SampleRef). Everything else
        /// (FilePresetRef, AbletonDefaultPresetRef, OriginalFileRef, Max patches) records where
        /// embedded content came from: Ableton keeps it inside the set and does not complain
        /// about a missing file on open, while the path leads to somebody else's machine at
        /// best. Counting that as lost is exactly the kind of bug you notice by eye.
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

        // The set's overall key. -1 means the Live version did not save it.
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
    /// Reads an .als. The format is gzip over XML — not tar, as is sometimes claimed. Parsing
    /// is streaming (XmlReader straight over GZipStream): a typical set is 100 KB on disk and
    /// close to 3 MB of XML once decompressed, and there are over a thousand sets here, so
    /// pulling each file whole into memory is out of the question.
    /// </summary>
    public static class AlsFile
    {
        /// <summary>Shared reader settings — the same ones the arrangement parser
        /// uses.</summary>
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

            // Context: which node we are currently inside.
            int mainTrackDepth = -1;
            int pluginDepth = -1;
            PluginRef plugin = null;
            int fileRefDepth = -1;
            FileRefInfo fileRef = null;
            bool tempoSeen = false;
            int tempoDepth = -1;

            // BrowserContentPath comes BEFORE PluginDesc within the same device, so we remember
            // the last one seen and hand it to the nearest plugin.
            string pendingManufacturer = null;
            bool pendingConfident = false;

            // A ScaleInformation node exists on every clip too — the one we want is the one
            // sitting directly in LiveSet: that is the project's overall key.
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
                    case "MasterTrack":                 // called that before Live 12
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
                            pendingManufacturer = null;   // one path per plugin
                            pendingConfident = false;
                        }
                        break;

                    case "FileRef":
                        if (!empty)
                        {
                            fileRef = new FileRefInfo();
                            fileRef.Container = parent;   // SampleRef = a real sample, the rest is provenance
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

                // --- values inside nodes that are already open
                if (plugin != null && depth == pluginDepth + 1)
                {
                    if (name == "PlugName" || name == "Name") plugin.Name = Attr(r, plugin.Name);
                    else if (name == "UniqueId") plugin.Vst2UniqueId = LongAttr(r);
                    else if (name == "Manufacturer")
                    {
                        // On an Audio Unit the vendor is written right in the node — a reliable
                        // source.
                        string v = Attr(r, "");
                        if (v.Length > 0) { plugin.Manufacturer = v; plugin.VendorConfident = true; }
                    }
                }

                // <Vst3PluginInfo><Uid><Fields.0../></Uid> — we take only these four; nodes of
                // the same name inside a preset blob must not get in here.
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
        /// The plugin's developer hides in the browser path Live records next to the device:
        ///     query:Plugins#VST3:FabFilter:Pro-Q%203   -> format, developer, name
        ///     view:X-Plugins#Antares:Auto-Tune%20Pro   -> developer, name
        ///     view:X-Plugins#Decapitator               -> name only
        /// Paths shaped like query:Everything#Reverb belong to Live's built-in devices and must
        /// not get in here — we cut them off by the absence of "Plugins#".
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
                // query:Plugins#<FORMAT>:<...>:<name>. A genuine vendor sits here only for VST3
                // and AU — Live takes it from the plugin itself. For VST2 (format "VST") this
                // slot holds the folder the .dll lies in, that is, "Gen" or "Eff".
                manufacturer = parts[parts.Length - 2];
                string format = parts[0];
                confident = format.Equals("VST3", StringComparison.OrdinalIgnoreCase)
                         || format.Equals("AU", StringComparison.OrdinalIgnoreCase)
                         || format.Equals("AudioUnit", StringComparison.OrdinalIgnoreCase);
            }
            else if (parts.Length == 2)
            {
                // The view:X-Plugins#Antares:Auto-Tune Pro form is a browser folder too.
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
