using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>Один звуковой файл рядом с проектом — кандидат в «послушать».</summary>
    public sealed class RenderFile
    {
        public string Path = "";
        public string Name = "";        // имя файла без расширения
        public string Folder = "";      // папка относительно корня проекта, "" — сам корень
        public DateTime Modified;
        public long Size;
        public bool Pinned;             // назначен главным превью вручную
        public int Score;               // насколько похож на рендер; см. RenderScan.Find

        public string Ext
        {
            get { return System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant(); }
        }
    }

    /// <summary>
    /// Ищет рендеры проекта. Считать рендером любой .wav в папке нельзя: в «Samples»
    /// лежат записанные и замороженные куски, их там сотни, и самый свежий из них
    /// почти наверняка не то, что человек хочет услышать. Поэтому Samples (и Backup,
    /// и служебные папки Live) выкидываем совсем, а из остального выстраиваем порядок
    /// по правдоподобности: закреплённое вручную, потом папки вроде Render/Bounce,
    /// потом корень проекта, потом всё прочее; внутри каждой ступени — свежие сверху.
    /// </summary>
    public static class RenderScan
    {
        static readonly string[] Exts =
            { ".wav", ".mp3", ".aif", ".aiff", ".flac", ".m4a", ".ogg", ".wma" };

        // Папки, куда обычно кладут готовый материал.
        static readonly string[] RenderDirs =
            { "render", "renders", "rendered", "bounce", "bounces", "export", "exports",
              "mixdown", "mixdowns", "master", "masters", "mixes", "out", "output", "preview" };

        // Папки, где рендеров не бывает по определению.
        static readonly string[] SkipDirs =
            { "samples", "backup", "ableton project info", "freeze", "frozen", "cache" };

        const int MaxFiles = 600;

        /// <summary>Корень проекта: ближайшая вверх папка «* Project», иначе папка самого .als.</summary>
        public static string ProjectRoot(SetEntry set)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(Path.GetDirectoryName(set.Path));
                DirectoryInfo start = d;
                for (int i = 0; i < 4 && d != null; i++)
                {
                    if (d.Name.EndsWith(" Project", StringComparison.OrdinalIgnoreCase)) return d.FullName;
                    d = d.Parent;
                }
                return start.FullName;
            }
            catch { return ""; }
        }

        public static List<RenderFile> Find(SetEntry set)
        {
            List<RenderFile> list = new List<RenderFile>();
            string root = ProjectRoot(set);
            if (root.Length == 0 || !Directory.Exists(root)) return list;

            string pinned = PreviewPins.Get(root);
            Walk(root, root, set.Name, list, 0);

            foreach (RenderFile f in list)
                if (pinned.Length > 0 && string.Equals(f.Path, pinned, StringComparison.OrdinalIgnoreCase))
                { f.Pinned = true; f.Score += 10000; }

            // «Последний рендер» — это именно последний по времени, поэтому дата решает
            // всё, кроме закрепления вручную. Раньше очередь строилась по «похожести»
            // (папка, совпадение с именем сета), и порядок выглядел случайным: свежий
            // файл оказывался ниже старого только потому, что тот назывался как сет.
            list.Sort(delegate (RenderFile a, RenderFile b)
            {
                if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
                int c = b.Modified.CompareTo(a.Modified);
                if (c != 0) return c;
                if (a.Score != b.Score) return b.Score.CompareTo(a.Score);
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        static void Walk(string dir, string root, string setName, List<RenderFile> list, int depth)
        {
            if (depth > 4 || list.Count >= MaxFiles) return;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return; }

            string rel = dir.Length > root.Length ? dir.Substring(root.Length).Trim('\\') : "";
            int folderScore = FolderScore(rel);

            foreach (string f in files)
            {
                if (list.Count >= MaxFiles) return;
                string ext = Path.GetExtension(f).ToLowerInvariant();
                if (Array.IndexOf(Exts, ext) < 0) continue;

                RenderFile rf = new RenderFile();
                rf.Path = f;
                rf.Name = Path.GetFileNameWithoutExtension(f);
                rf.Folder = rel;
                try { FileInfo fi = new FileInfo(f); rf.Modified = fi.LastWriteTimeUtc; rf.Size = fi.Length; }
                catch { }

                rf.Score = folderScore;
                list.Add(rf);
            }

            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { return; }

            foreach (string sub in subs)
            {
                string name = Path.GetFileName(sub).ToLowerInvariant();
                if (Array.IndexOf(SkipDirs, name) >= 0) continue;
                Walk(sub, root, setName, list, depth + 1);
            }
        }

        static int FolderScore(string rel)
        {
            if (rel.Length == 0) return 60;                 // прямо в корне проекта
            string[] parts = rel.Split('\\');
            foreach (string p in parts)
                if (Array.IndexOf(RenderDirs, p.ToLowerInvariant()) >= 0) return 100;
            return 20;
        }
    }

    /// <summary>
    /// Закреплённые превью: какой файл считать главным для проекта. Живут отдельным
    /// файлом, а не в settings.cfg — их столько же, сколько проектов, и настройкам
    /// незачем распухать до тысячи строк.
    /// </summary>
    public static class PreviewPins
    {
        static Dictionary<string, string> _map;

        static string FilePath { get { return Path.Combine(Settings.Dir, "previews.cfg"); } }

        static void Load()
        {
            if (_map != null) return;
            _map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    int tab = line.IndexOf('\t');
                    if (tab <= 0) continue;
                    _map[line.Substring(0, tab)] = line.Substring(tab + 1);
                }
            }
            catch { }
        }

        public static string Get(string projectRoot)
        {
            Load();
            string v;
            return _map.TryGetValue(Key(projectRoot), out v) ? v : "";
        }

        public static void Set(string projectRoot, string file)
        {
            Load();
            _map[Key(projectRoot)] = file ?? "";
            Save();
        }

        public static void Clear(string projectRoot)
        {
            Load();
            _map.Remove(Key(projectRoot));
            Save();
        }

        static string Key(string projectRoot) { return (projectRoot ?? "").TrimEnd('\\'); }

        static void Save()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                StringBuilder sb = new StringBuilder();
                foreach (KeyValuePair<string, string> kv in _map)
                {
                    if (kv.Value.Length == 0) continue;
                    sb.Append(kv.Key).Append('\t').AppendLine(kv.Value);
                }
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
