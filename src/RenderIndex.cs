using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>One audio file next to the project — a candidate for "let me hear
    /// it".</summary>
    public sealed class RenderFile
    {
        public string Path = "";
        public string Name = "";        // file name without the extension
        public string Folder = "";      // folder relative to the project root, "" is the root itself
        public DateTime Modified;
        public long Size;
        public bool Pinned;             // pinned as the main preview by hand
        public int Score;               // how much it looks like a render; see RenderScan.Find

        public string Ext
        {
            get { return System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant(); }
        }
    }

    /// <summary>
    /// Finds a project's renders. Treating every .wav in the folder as a render will not do: in
    /// "Samples" lie recorded and frozen pieces, hundreds of them, and the freshest is almost
    /// certainly not what the person wants to hear. So Samples (along with Backup and Live's
    /// own housekeeping folders) is dropped entirely, and the rest is ordered by plausibility:
    /// pinned by hand, then folders like Render/Bounce, then the project root, then everything
    /// else; within each step, newest first.
    /// </summary>
    public static class RenderScan
    {
        static readonly string[] Exts =
            { ".wav", ".mp3", ".aif", ".aiff", ".flac", ".m4a", ".ogg", ".wma" };

        // Folders where finished material usually goes.
        static readonly string[] RenderDirs =
            { "render", "renders", "rendered", "bounce", "bounces", "export", "exports",
              "mixdown", "mixdowns", "master", "masters", "mixes", "out", "output", "preview" };

        // Folders where renders never live by definition.
        static readonly string[] SkipDirs =
            { "samples", "backup", "ableton project info", "freeze", "frozen", "cache" };

        const int MaxFiles = 600;

        /// <summary>The project root: the nearest "* Project" folder above, otherwise the .als
        /// folder itself.</summary>
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

            // "The latest render" means latest in time, so the date decides everything except
            // manual pinning. The queue used to be built on "similarity" (the folder, a match
            // with the set's name), and the order looked random: a fresh file ended up below an
            // old one only because the old one was named like the set.
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
            if (rel.Length == 0) return 60;                 // right in the project root
            string[] parts = rel.Split('\\');
            foreach (string p in parts)
                if (Array.IndexOf(RenderDirs, p.ToLowerInvariant()) >= 0) return 100;
            return 20;
        }
    }

    /// <summary>
    /// Pinned previews: which file counts as the main one for a project. They live in their own
    /// file rather than settings.cfg — there are as many of them as there are projects, and the
    /// settings have no business swelling to a thousand lines.
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
