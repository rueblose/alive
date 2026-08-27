using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>
    /// Теги и заметки, которые пишет сам пользователь. Отдельный файл notes.cfg рядом с
    /// настройками — это данные о проектах, а не настройка программы.
    ///
    /// Ключ — папка проекта, а не путь к .als. У проекта рядом лежит десяток версий, и
    /// теги, привязанные к файлу, пришлось бы проставлять каждой заново; к тому же
    /// «final2.als» появляется уже после того, как проект отметили, и метка на него бы
    /// не перешла. Папка же у всех версий одна.
    ///
    /// Теги намеренно без всякого словаря: ни жанров, ни статусов, ни оценок заранее не
    /// заведено — что человек напишет, то и будет.
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

        /// <summary>Поднялся ли состав тегов — окну пора пересобрать список.</summary>
        public static event Action Changed;

        // ------------------------------------------------------------------ чтение

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

                    // Путь и значение разделены табуляцией: в путях её не бывает, а вот
                    // равенства и запятые попадаются сплошь и рядом.
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

        // Заметка многострочная, а файл построчный — переносы уезжают в «\n».
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
                else sb.Append(next);          // «\\» и всё прочее — как есть
            }
            return sb.ToString();
        }

        // ------------------------------------------------------------------ доступ

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

        /// <summary>Разбирает строку «drum, vocal, beat» в теги.</summary>
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
        /// Все теги, которые уже где-то проставлены, по алфавиту. Нужны, чтобы второй
        /// раз тот же тег можно было выбрать из списка, а не вспоминать, как он писался.
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
