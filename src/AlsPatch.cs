using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AbletonManager
{
    /// <summary>One third-party plugin of a set as a target for disabling — together with all
    /// its copies.</summary>
    public sealed class AlsPluginSlot
    {
        public string Uid = "";
        public string Name = "";
        public PluginKind Kind;
        public int Count = 1;          // how many times it occurs in the set

        /// <summary>
        /// The developer — only when it really is the developer. For VST2 this slot holds the
        /// folder the .dll lies in ("Eff", "Gen"), and showing that as a vendor is a lie (see
        /// AlsFile.ParseBrowserPath).
        /// </summary>
        public string Vendor = "";

        public string Format { get { return Kind == PluginKind.Vst3 ? "VST3" : "VST2"; } }

        public string Label
        {
            get { return Count > 1 ? Name + " ×" + Count.ToString(CultureInfo.InvariantCulture) : Name; }
        }
    }

    /// <summary>
    /// A copy of a set in which Live does not recognise the chosen plugins.
    ///
    /// A plugin in an .als is identified by an identifier, not by a name and not by a file:
    ///
    ///     &lt;Vst3PluginInfo&gt;…&lt;Uid&gt;&lt;Fields.0 Value="-1412567295" /&gt;…&lt;/Uid&gt;
    ///     &lt;VstPluginInfo&gt;&lt;Path Value="…\Serum_x64.dll" /&gt;&lt;UniqueId Value="1483109208" /&gt;
    ///
    /// We substitute exactly those values — and Live honestly says "plugin not found", shows a
    /// placeholder with the former name in its place, and loads the rest of the set. Nothing is
    /// deleted: the device node, its place in the chain, the automation, the identifiers and
    /// even the saved preset blob all stay byte for byte. That is a matter of principle —
    /// cutting a plugin out of an .als means touching DeviceChain, automation and IDs at once,
    /// and such an edit breaks a set more reliably than the broken plugin itself.
    ///
    /// The edit is surgical: only those Values change, everything else is copied byte for byte.
    /// The original is never touched — we write into a separate file that the caller deletes
    /// afterwards.
    ///
    /// We go as a stream, line by line, holding only the current device node in memory. Pulling
    /// the decompressed XML up whole is out of the question: on this machine one set unpacks to
    /// 177 MB, which is 350 MB as a .NET string, and with the result assembled on top that is
    /// close to a gigabyte for one edit. A node, meanwhile, is 0.2 MB at worst, and lines in an
    /// .als are shorter than 230 bytes.
    /// </summary>
    public static class AlsPatch
    {
        /// <summary>
        /// The marker put in place of Fields.0 for VST3: "Aliv" in ASCII. It cannot collide
        /// with a real plugin — it replaces only the high word while the other three fields
        /// stay as they were, so two disabled plugins do not collapse into one and the same
        /// non-existent identifier.
        /// </summary>
        const int Vst3Marker = 0x416C6976;

        /// <summary>The same for VST2: the identifier there is a single number, so we spoil it
        /// with an xor.</summary>
        const int Vst2Marker = 0x416C6976;

        /// <summary>
        /// What gets appended to a VST2's .dll path. A broken UniqueId alone is not enough:
        /// Live can also raise a VST2 by its file, and the plugin would then load as if nothing
        /// had happened — meaning the probe would have checked nothing.
        /// </summary>
        const string DisabledSuffix = ".alive-disabled";

        // ------------------------------------------------------------------ targets

        /// <summary>
        /// The set's third-party plugins that can be disabled — one per identifier. Without an
        /// identifier a plugin cannot be addressed (which happens with Audio Units: they cannot
        /// be raised on Windows anyway), and those do not get in here.
        /// </summary>
        public static List<AlsPluginSlot> Targets(AlsInfo info)
        {
            List<AlsPluginSlot> list = new List<AlsPluginSlot>();
            if (info == null) return list;

            Dictionary<string, AlsPluginSlot> byUid =
                new Dictionary<string, AlsPluginSlot>(StringComparer.OrdinalIgnoreCase);

            foreach (PluginRef p in info.Plugins)
            {
                if (p.Uid.Length == 0) continue;
                if (p.Kind != PluginKind.Vst2 && p.Kind != PluginKind.Vst3) continue;

                AlsPluginSlot slot;
                if (byUid.TryGetValue(p.Uid, out slot)) { slot.Count++; continue; }

                slot = new AlsPluginSlot();
                slot.Uid = p.Uid;
                slot.Name = p.Name;
                slot.Vendor = p.VendorConfident ? p.Manufacturer : "";
                slot.Kind = p.Kind;
                byUid[p.Uid] = slot;
                list.Add(slot);
            }

            list.Sort(delegate (AlsPluginSlot a, AlsPluginSlot b)
            { return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); });
            return list;
        }

        /// <summary>How many of the set's plugins cannot be disabled — nothing to identify them
        /// by.</summary>
        public static int Unaddressable(AlsInfo info)
        {
            if (info == null) return 0;
            int n = 0;
            foreach (PluginRef p in info.Plugins)
                if (p.Uid.Length == 0) n++;
            return n;
        }

        // ------------------------------------------------------------------ patching

        /// <summary>
        /// Writes a copy of src into dst with the listed plugins anonymised. Returns how many
        /// device nodes were touched — zero means none of the requested plugins was found in
        /// the file, and running such a probe is pointless.
        ///
        /// inv is needed only so that a substituted identifier does not accidentally coincide
        /// with another installed plugin: Live would then silently put a foreign device in its
        /// place.
        /// </summary>
        public static int Neutralize(string src, string dst, ICollection<string> uids, PluginInventory inv)
        {
            if (uids == null || uids.Count == 0)
                throw new ArgumentException("nothing to neutralize", "uids");

            HashSet<string> wanted = new HashSet<string>(uids, StringComparer.OrdinalIgnoreCase);
            int patched = 0;

            using (FileStream fin = new FileStream(src, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite, 64 * 1024))
            using (GZipStream gin = new GZipStream(fin, CompressionMode.Decompress))
            // detectEncodingFromByteOrderMarks: false — otherwise the BOM would be eaten on
            // reading and not written back, and the copy must differ from the original by
            // exactly the values we change and by nothing else.
            using (StreamReader rin = new StreamReader(gin, new UTF8Encoding(false), false, 64 * 1024))

            using (FileStream fout = new FileStream(dst, FileMode.Create, FileAccess.Write,
                                                    FileShare.None, 64 * 1024))
            using (GZipStream gout = new GZipStream(fout, CompressionMode.Compress))
            using (StreamWriter wout = new StreamWriter(gout, new UTF8Encoding(false), 64 * 1024))
            {
                LineReader lines = new LineReader(rin);
                StringBuilder node = null;
                string closeTag = null;
                PluginKind kind = PluginKind.Vst3;

                string line;
                while ((line = lines.Next()) != null)
                {
                    if (node == null)
                    {
                        int which = Opens(line);
                        if (which < 0) { wout.Write(line); continue; }

                        node = new StringBuilder(line);
                        closeTag = CloseTags[which];
                        kind = which == 0 ? PluginKind.Vst3 : PluginKind.Vst2;
                    }
                    else node.Append(line);

                    // The closing tag is looked for in the current line rather than in
                    // everything accumulated: otherwise the whole node string would be rebuilt
                    // for every one of its lines. It works both when a node fits on one line
                    // and when it stretches over thousands — and a tag is never torn between
                    // lines.
                    if (line.IndexOf(closeTag, StringComparison.Ordinal) < 0) continue;

                    string text = node.ToString();
                    node = null;

                    string uid = UidOf(text, kind);
                    if (uid.Length > 0 && wanted.Contains(uid))
                    {
                        wout.Write(Rewrite(text, kind, uid, inv));
                        patched++;
                    }
                    else wout.Write(text);
                }

                // The file ended mid-node — write it as is so the tail is not lost.
                if (node != null) wout.Write(node.ToString());
            }

            Diag.Line("rescue: patched " + patched + " device(s) in " + Path.GetFileName(dst));
            return patched;
        }

        // -------------------------------------------------- finding device nodes

        static readonly string[] OpenTags = { "<Vst3PluginInfo", "<VstPluginInfo" };
        static readonly string[] CloseTags = { "</Vst3PluginInfo>", "</VstPluginInfo>" };

        /// <summary>
        /// Whether a line opens a plugin description node: 0 — VST3, 1 — VST2, otherwise -1.
        ///
        /// By plain substring search rather than by parsing XML: the file is machine-written,
        /// these nodes do not nest inside each other, while rebuilding the document through
        /// XmlWriter would rewrite bytes the edit does not touch — and the whole point is to
        /// change the identifiers only. The search cannot stray inside the ProcessorState and
        /// Buffer blobs: those hold nothing but hexadecimal digits, with no brackets in them.
        /// </summary>
        static int Opens(string line)
        {
            // The order matters: "&lt;Vst3PluginInfo" also contains "PluginInfo" but not
            // "&lt;VstPluginInfo" — and checking VST2 first would be wrong anyway on a line
            // holding both (Live's markup has no such line, but the cost is zero).
            for (int i = 0; i < OpenTags.Length; i++)
                if (line.IndexOf(OpenTags[i], StringComparison.Ordinal) >= 0) return i;
            return -1;
        }

        /// <summary>
        /// A line together with its line ending. StreamReader.ReadLine eats the endings, and we
        /// would have to guess what was there: in an .als it is "\r\n", in other Live files
        /// (the log) a lone "\n", and a copy has to match the original byte for byte everywhere
        /// except the substituted values.
        ///
        /// Internal rather than private: AlsSamplePatch uses the same reading.
        /// </summary>
        internal sealed class LineReader
        {
            readonly TextReader _r;
            readonly char[] _buf = new char[64 * 1024];
            int _len, _pos;

            public LineReader(TextReader r) { _r = r; }

            public string Next()
            {
                StringBuilder carry = null;
                while (true)
                {
                    if (_pos >= _len)
                    {
                        _len = _r.Read(_buf, 0, _buf.Length);
                        _pos = 0;
                        if (_len <= 0)
                            return carry != null && carry.Length > 0 ? carry.ToString() : null;
                    }

                    int nl = Array.IndexOf(_buf, '\n', _pos, _len - _pos);
                    if (nl >= 0)
                    {
                        string s = new string(_buf, _pos, nl - _pos + 1);
                        _pos = nl + 1;
                        if (carry == null) return s;
                        carry.Append(s);
                        return carry.ToString();
                    }

                    if (carry == null) carry = new StringBuilder();
                    carry.Append(_buf, _pos, _len - _pos);
                    _pos = _len;
                }
            }
        }

        // ------------------------------------------------------- identifying a plugin

        /// <summary>
        /// The identifier out of a node — in the same shape AlsFile and Live's own database
        /// use. It is computed by the same PluginRef.FinishUid so the formula lives in one
        /// place: were the two parsers to drift apart, they would disable a plugin other than
        /// the one that was shown.
        /// </summary>
        static string UidOf(string node, PluginKind kind)
        {
            if (kind == PluginKind.Vst2)
            {
                long id;
                if (!FirstLong(node, "<UniqueId Value=\"", out id)) return "";
                PluginRef r = new PluginRef();
                r.Kind = PluginKind.Vst2;
                r.Vst2UniqueId = id;
                r.FinishUid();
                return r.Uid;
            }

            // A VST3 node holds two <Uid> blocks — one for the preset and one for the device,
            // with equal values. We take the last: that is the one sitting directly in
            // Vst3PluginInfo, exactly as AlsFile reads it.
            int last = node.LastIndexOf("<Uid>", StringComparison.Ordinal);
            if (last < 0) return "";
            int close = node.IndexOf("</Uid>", last, StringComparison.Ordinal);
            if (close < 0) return "";

            PluginRef v3 = new PluginRef();
            v3.Kind = PluginKind.Vst3;
            string block = node.Substring(last, close - last);
            for (int i = 0; i < 4; i++)
            {
                long f;
                if (!FirstLong(block, "<Fields." + i + " Value=\"", out f)) return "";
                v3.Vst3Fields[i] = unchecked((int)f);
                v3.Vst3FieldCount++;
            }
            v3.FinishUid();
            return v3.Uid;
        }

        // ------------------------------------------------------------- substitution

        static string Rewrite(string node, PluginKind kind, string uid, PluginInventory inv)
        {
            if (kind == PluginKind.Vst2)
            {
                long id;
                if (!FirstLong(node, "<UniqueId Value=\"", out id)) return node;

                int fake = FreeVst2Id(unchecked((int)id), inv);
                string s = ReplaceAll(node, "<UniqueId Value=\"",
                                      fake.ToString(CultureInfo.InvariantCulture));
                return SuffixPaths(s);
            }

            int marker = FreeVst3Marker(node, inv);
            return ReplaceAll(node, "<Fields.0 Value=\"",
                              marker.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A marker that resembles nothing installed. The check is not paranoia: should a
        /// substituted identifier coincide with another plugin, Live would not say "not found"
        /// but put a foreign device in its place, and the probe would show an untruth.
        /// </summary>
        static int FreeVst3Marker(string node, PluginInventory inv)
        {
            int last = node.LastIndexOf("<Uid>", StringComparison.Ordinal);
            if (last < 0) return Vst3Marker;

            PluginRef probe = new PluginRef();
            probe.Kind = PluginKind.Vst3;
            for (int i = 1; i < 4; i++)
            {
                long f;
                if (!FirstLong(node.Substring(last), "<Fields." + i + " Value=\"", out f)) return Vst3Marker;
                probe.Vst3Fields[i] = unchecked((int)f);
            }
            probe.Vst3FieldCount = 4;

            for (int bump = 0; bump < 64; bump++)
            {
                int candidate = unchecked(Vst3Marker + bump);
                probe.Vst3Fields[0] = candidate;
                probe.FinishUid();
                if (inv == null || inv.ByUid(probe.Uid) == null) return candidate;
            }
            return Vst3Marker;
        }

        static int FreeVst2Id(int original, PluginInventory inv)
        {
            for (int bump = 0; bump < 64; bump++)
            {
                int candidate = unchecked(original ^ (Vst2Marker + bump));
                if (candidate == original) continue;
                string uid = "vst2:" + candidate.ToString(CultureInfo.InvariantCulture);
                if (inv == null || inv.ByUid(uid) == null) return candidate;
            }
            return unchecked(original ^ Vst2Marker);
        }

        /// <summary>Append ".alive-disabled" to every path in the node — no file by that name
        /// exists.</summary>
        static string SuffixPaths(string node)
        {
            const string tag = "<Path Value=\"";
            StringBuilder sb = new StringBuilder(node.Length + 32);
            int pos = 0;
            while (true)
            {
                int at = node.IndexOf(tag, pos, StringComparison.Ordinal);
                if (at < 0) break;
                int from = at + tag.Length;
                int close = node.IndexOf('"', from);
                if (close < 0) break;

                sb.Append(node, pos, close - pos).Append(DisabledSuffix);
                pos = close;
            }
            sb.Append(node, pos, node.Length - pos);
            return sb.ToString();
        }

        /// <summary>Replace an attribute value in every occurrence of a tag inside the
        /// node.</summary>
        static string ReplaceAll(string node, string tag, string value)
        {
            StringBuilder sb = new StringBuilder(node.Length + 32);
            int pos = 0;
            while (true)
            {
                int at = node.IndexOf(tag, pos, StringComparison.Ordinal);
                if (at < 0) break;
                int from = at + tag.Length;
                int close = node.IndexOf('"', from);
                if (close < 0) break;

                sb.Append(node, pos, from - pos).Append(value);
                pos = close;
            }
            sb.Append(node, pos, node.Length - pos);
            return sb.ToString();
        }

        static bool FirstLong(string s, string tag, out long value)
        {
            value = 0;
            int at = s.IndexOf(tag, StringComparison.Ordinal);
            if (at < 0) return false;
            int from = at + tag.Length;
            int close = s.IndexOf('"', from);
            if (close < 0) return false;
            return long.TryParse(s.Substring(from, close - from), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out value);
        }

    }
}
