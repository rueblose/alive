using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>
    /// Tags and notes written by the user. A separate notes.cfg next to the settings — this is
    /// data about projects, not a program setting.
    ///
    /// The key is the project folder, not the path to the .als. A project has a dozen versions
    /// lying next to each other, and tags bound to a file would have to be re-applied to each
    /// one; besides, "final2.als" appears only after the project has been marked, and the label
    /// would not carry over to it. The folder, meanwhile, is the same for every version.
    ///
    /// Tags deliberately come with no vocabulary: no genres, no statuses, no ratings set up in
    /// advance — whatever the person writes is what it is.
    /// </summary>
    public static class ProjectMeta
    {
        sealed class Entry
        {
            public readonly List<string> Tags = new List<string>();
            public string Note = "";
            public bool Empty { get { return Tags.Count == 0 && Note.Length == 0; } }
        }

        static readonly Dictionary<string, Entry> _byDir =
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        static bool _loaded;

        static string FilePath { get { return Path.Combine(Settings.Dir, "notes.cfg"); } }

        /// <summary>Has the tag set grown — time for the window to rebuild its list.</summary>
        public static event Action Changed;

        // ------------------------------------------------------------------ reading

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;

                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq);
                    string rest = line.Substring(eq + 1);

                    // The path and the value are separated by a tab: paths never contain one,
                    // while equals signs and commas turn up all the time.
                    int tab = rest.IndexOf('\t');
                    if (tab < 0) continue;
                    string dir = rest.Substring(0, tab);
                    string val = rest.Substring(tab + 1);
                    if (dir.Length == 0) continue;

                    Entry e = Slot(dir);
                    if (key == "tags")
                    {
                        foreach (string t in val.Split(','))
                        {
                            string tag = t.Trim();
                            if (tag.Length > 0 && !Has(e.Tags, tag)) e.Tags.Add(tag);
                        }
                    }
                    else if (key == "note") e.Note = Unescape(val);
                }
            }
            catch { }
        }

        static Entry Slot(string dir)
        {
            Entry e;
            if (!_byDir.TryGetValue(dir, out e)) { e = new Entry(); _byDir[dir] = e; }
            return e;
        }

        static bool Has(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.CurrentCultureIgnoreCase)) return true;
            return false;
        }

        static void Save()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Alive - tags and notes, one project folder per key");
                foreach (KeyValuePair<string, Entry> kv in _byDir)
                {
                    if (kv.Value.Empty) continue;
                    if (kv.Value.Tags.Count > 0)
                        sb.Append("tags=").Append(kv.Key).Append('\t')
                          .AppendLine(string.Join(", ", kv.Value.Tags.ToArray()));
                    if (kv.Value.Note.Length > 0)
                        sb.Append("note=").Append(kv.Key).Append('\t')
                          .AppendLine(Escape(kv.Value.Note));
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // The note is multi-line and the file is line-based — breaks go out as "\n".
        static string Escape(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n");
        }

        static string Unescape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char next = s[++i];
                if (next == 'n') sb.Append("\r\n");
                else sb.Append(next);          // "\\" and everything else — as is
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------- access

        public static List<string> TagsOf(string dir)
        {
            Load();
            Entry e;
            if (string.IsNullOrEmpty(dir) || !_byDir.TryGetValue(dir, out e)) return new List<string>();
            return new List<string>(e.Tags);
        }

        public static string NoteOf(string dir)
        {
            Load();
            Entry e;
            if (string.IsNullOrEmpty(dir) || !_byDir.TryGetValue(dir, out e)) return "";
            return e.Note;
        }

        public static bool HasAnything(string dir)
        {
            Load();
            Entry e;
            return !string.IsNullOrEmpty(dir) && _byDir.TryGetValue(dir, out e) && !e.Empty;
        }

        public static void Set(string dir, IEnumerable<string> tags, string note)
        {
            Load();
            if (string.IsNullOrEmpty(dir)) return;

            Entry e = Slot(dir);
            e.Tags.Clear();
            if (tags != null)
                foreach (string t in tags)
                {
                    string tag = (t ?? "").Trim();
                    if (tag.Length > 0 && !Has(e.Tags, tag)) e.Tags.Add(tag);
                }
            e.Note = (note ?? "").Trim();

            if (e.Empty) _byDir.Remove(dir);
            Save();
            if (Changed != null) Changed();
        }

        /// <summary>Parses the string "drum, vocal, beat" into tags.</summary>
        public static List<string> ParseTags(string text)
        {
            List<string> result = new List<string>();
            if (string.IsNullOrEmpty(text)) return result;
            foreach (string t in text.Split(','))
            {
                string tag = t.Trim();
                if (tag.Length > 0 && !Has(result, tag)) result.Add(tag);
            }
            return result;
        }

        public static string JoinTags(IEnumerable<string> tags)
        {
            List<string> list = new List<string>();
            foreach (string t in tags) list.Add(t);
            return string.Join(", ", list.ToArray());
        }

        /// <summary>
        /// Every tag already used somewhere, alphabetically. Needed so that the second time the
        /// same tag can be picked from a list instead of recalling how it was spelled.
        /// </summary>
        public static List<string> AllTags()
        {
            Load();
            List<string> all = new List<string>();
            foreach (KeyValuePair<string, Entry> kv in _byDir)
                foreach (string t in kv.Value.Tags)
                    if (!Has(all, t)) all.Add(t);
            all.Sort(StringComparer.CurrentCultureIgnoreCase);
            return all;
        }
    }
}
