using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AbletonManager
{
    /// <summary>
    /// A folder of the sample library. Only folders with a sample somewhere below them become
    /// nodes; the rest (a folder of presets, Live's "Ableton Folder Info") pass their weight up
    /// to the nearest one that does.
    /// </summary>
    public sealed class SampleFolder
    {
        public string Path = "";
        public string Name = "";
        public SampleFolder Parent;

        /// <summary>Subfolders with at least one sample below them.</summary>
        public readonly List<SampleFolder> Children = new List<SampleFolder>();

        /// <summary>The samples lying directly in this folder.</summary>
        public readonly List<SampleFile> Files = new List<SampleFile>();

        /// <summary>Samples over the whole subtree.</summary>
        public int TotalSamples;

        /// <summary>Every file over the whole subtree, samples or not — "how much room does this
        /// pack take" is about the whole pack.</summary>
        public long TotalBytes;

        /// <summary>The position in SampleIndex.Folders — the cache writes parents by it.</summary>
        public int Index;
    }

    public sealed class SampleFile
    {
        public string Name = "";
        public long Size;
        public SampleFolder Folder;

        /// <summary>An AIFF the preview cannot open — in practice Ableton's own compressed
        /// AIFC, which is most of Live's packs. Only Live plays it; the walk finds out once.</summary>
        public bool Silent;

        public string Path { get { return FolderScan.Combine(Folder.Path, Name); } }

        /// <summary>Whether the preview can play it: by the extension, and for an AIFF by what
        /// the walk found in its header.</summary>
        public bool CanPreview { get { return !Silent && SampleIndex.CanPreview(Name); } }
    }

    public delegate void SampleProgress(int found);

    /// <summary>
    /// The sample library: every folder the user pointed at, walked in the background and cached
    /// to disk. Published whole, like ProjectIndex.Sets — the interface reads one snapshot
    /// while the next is built, and a published index is never modified.
    /// </summary>
    public sealed class SampleIndex
    {
        const int CacheVersion = 2;   // 2: the Silent flag of an AIFF only Live can play

        public readonly List<SampleFolder> Roots = new List<SampleFolder>();
        public readonly List<SampleFolder> Folders = new List<SampleFolder>();   // parents before children
        public readonly List<SampleFile> Files = new List<SampleFile>();

        public static readonly SampleIndex Empty = new SampleIndex();

        public int TotalSamples
        {
            get { int n = 0; foreach (SampleFolder r in Roots) n += r.TotalSamples; return n; }
        }

        public long TotalBytes
        {
            get { long n = 0; foreach (SampleFolder r in Roots) n += r.TotalBytes; return n; }
        }

        static string CachePath { get { return System.IO.Path.Combine(Settings.Dir, "samples.cache"); } }

        // ------------------------------------------------------------ what counts

        static readonly HashSet<string> Extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".wav", ".aif", ".aiff", ".flac", ".ogg", ".mp3", ".m4a", ".rx2", ".rex" };

        static readonly HashSet<string> Playable = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".wav", ".aif", ".aiff", ".flac", ".mp3", ".m4a" };

        /// <summary>
        /// A file Live can load as a sample. "._name.wav" is not one: that is the AppleDouble
        /// resource fork packs made on a Mac carry beside every file, with no sound inside.
        /// </summary>
        public static bool IsSampleName(string name)
        {
            if (string.IsNullOrEmpty(name) || name.StartsWith("._", StringComparison.Ordinal)) return false;
            int dot = name.LastIndexOf('.');
            return dot > 0 && Extensions.Contains(name.Substring(dot));
        }

        /// <summary>What the preview can play: Windows decodes WAV, FLAC, MP3 and M4A,
        /// AiffReader does AIFF. OGG and REX stay silent.</summary>
        public static bool CanPreview(string name)
        {
            int dot = (name ?? "").LastIndexOf('.');
            return dot > 0 && Playable.Contains(name.Substring(dot));
        }

        /// <summary>
        /// Folders walked for their weight only: Live's "Ableton Folder Info" (its .ogg browser
        /// previews would otherwise pass for samples — 9,491 of them in the packs on the
        /// development machine), Backup, and project folders — a project that happens to lie in
        /// a sample folder does not make its recordings a library.
        /// </summary>
        public static bool IsWeightOnly(string dirName)
        {
            return dirName.Equals("Ableton Folder Info", StringComparison.OrdinalIgnoreCase)
                || dirName.Equals("Backup", StringComparison.OrdinalIgnoreCase)
                || dirName.EndsWith(" Project", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ roots

        static string Norm(string p)
        {
            try { return System.IO.Path.GetFullPath(p).TrimEnd('\\'); }
            catch { return (p ?? "").TrimEnd('\\'); }
        }

        public static bool ContainsPath(IList<string> list, string path)
        {
            if (list == null || string.IsNullOrEmpty(path)) return false;
            string n = Norm(path);
            foreach (string s in list)
                if (string.Equals(Norm(s), n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Whether path lies strictly inside root. Compared as full paths, or
        /// "…\Samples2" would pass for "…\Samples".</summary>
        public static bool Inside(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            string p = Norm(path), r = Norm(root);
            return p.Length > r.Length + 1 && p.StartsWith(r + "\\", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The roots that get walked: switched on, and not lying inside another switched-on
        /// root — such a one is part of that root's tree already, and walking it twice would
        /// count its samples twice.
        /// </summary>
        public static List<string> Effective(IList<string> roots, IList<string> disabled)
        {
            List<string> on = new List<string>();
            foreach (string r in roots)
                if (!string.IsNullOrEmpty(r) && !ContainsPath(disabled, r) && !ContainsPath(on, r)) on.Add(r);

            List<string> result = new List<string>();
            foreach (string r in on)
            {
                bool nested = false;
                foreach (string other in on)
                    if (!ReferenceEquals(other, r) && Inside(r, other)) { nested = true; break; }
                if (!nested) result.Add(r);
            }
            return result;
        }

        // ------------------------------------------------------------------- walk

        sealed class Pending
        {
            public readonly string Path;
            public readonly SampleFolder Folder;   // where what is found here belongs
            public readonly bool Own;              // false — weight only: counted into Folder, never a node

            public Pending(string path, SampleFolder folder, bool own)
            {
                Path = path;
                Folder = folder;
                Own = own;
            }
        }

        /// <summary>
        /// Every switched-on root, side by side: they do not overlap (see Effective) and usually
        /// sit on different drives, so walking them at once halves the wait on two disks.
        ///
        /// previous — the index this one replaces. An AIFF it already looked into, with the same
        /// path and size, keeps its Silent flag without being opened again: opening the thirty
        /// thousand AIFFs of Live's packs took a minute of the first walk on the development
        /// machine, and a file that has not changed has nothing new to say.
        /// </summary>
        public static SampleIndex Build(IList<string> roots, IList<string> disabled,
                                        SampleProgress progress, CancellationToken cancel,
                                        SampleIndex previous = null)
        {
            List<string> walk = Effective(roots, disabled);
            List<SampleFolder>[] trees = new List<SampleFolder>[walk.Count];
            int total = 0;

            Dictionary<string, SampleFile> known = new Dictionary<string, SampleFile>(StringComparer.OrdinalIgnoreCase);
            if (previous != null)
                foreach (SampleFile f in previous.Files)
                    if (AiffReader.IsAiffName(f.Name)) known[f.Path] = f;

            Parallel.For(0, walk.Count, delegate (int i)
            {
                trees[i] = Walk(walk[i], known, delegate (int n)
                {
                    int now = Interlocked.Add(ref total, n);
                    if (progress != null) progress(now);
                }, delegate { return cancel.IsCancellationRequested; });
            });

            SampleIndex idx = new SampleIndex();
            foreach (List<SampleFolder> tree in trees)
            {
                if (tree == null) continue;
                idx.Roots.Add(tree[0]);
                foreach (SampleFolder f in tree)
                {
                    idx.Folders.Add(f);
                    idx.Files.AddRange(f.Files);
                }
            }
            return idx;
        }

        /// <summary>How many samples a folder holds, by the very rules of the walk — the number
        /// in the folders dialog must agree with the tab. -1: the folder would not open.</summary>
        public static int CountIn(string folder, Func<bool> cancelled)
        {
            List<SampleFolder> tree = Walk(folder, null, null, cancelled);
            return tree == null ? -1 : tree[0].TotalSamples;
        }

        /// <summary>
        /// One root. A stack rather than recursion — libraries run a dozen levels deep. Returns
        /// the root first and then every kept folder, a parent always before its children. null:
        /// the root itself would not open, or the walk was called off.
        ///
        /// known — AIFFs looked into before (see Build); null — do not look into AIFFs at all
        /// (SampleFile.Silent): the index wants it, the count in the folders dialog does not.
        /// Only read here, so the parallel walks can share it.
        /// </summary>
        static List<SampleFolder> Walk(string root, Dictionary<string, SampleFile> known,
                                       SampleProgress found, Func<bool> cancelled)
        {
            string top;
            try { top = System.IO.Path.GetFullPath(root); }
            catch { return null; }

            SampleFolder rootFolder = new SampleFolder();
            rootFolder.Path = top;
            rootFolder.Name = top;
            List<SampleFolder> all = new List<SampleFolder>();
            all.Add(rootFolder);

            Stack<Pending> todo = new Stack<Pending>();
            todo.Push(new Pending(top, rootFolder, true));
            int batch = 0;
            bool first = true;

            while (todo.Count > 0)
            {
                if (cancelled != null && cancelled()) return null;
                Pending p = todo.Pop();

                bool ok = FolderScan.List(p.Path, delegate (string name, bool isDir, long size)
                {
                    if (isDir)
                    {
                        string full = FolderScan.Combine(p.Path, name);
                        if (!p.Own || IsWeightOnly(name))
                        {
                            todo.Push(new Pending(full, p.Folder, false));
                            return;
                        }
                        SampleFolder child = new SampleFolder();
                        child.Path = full;
                        child.Name = name;
                        child.Parent = p.Folder;
                        all.Add(child);
                        todo.Push(new Pending(full, child, true));
                        return;
                    }

                    p.Folder.TotalBytes += size;            // own bytes for now; the subtree is added below
                    if (!p.Own || !IsSampleName(name)) return;
                    SampleFile f = new SampleFile();
                    f.Name = name;
                    f.Size = size;
                    f.Folder = p.Folder;
                    if (known != null && AiffReader.IsAiffName(name))
                    {
                        string full = FolderScan.Combine(p.Path, name);
                        SampleFile was;
                        f.Silent = known.TryGetValue(full, out was) && was.Size == size
                                 ? was.Silent
                                 : !AiffReader.CanRead(full);
                    }
                    p.Folder.Files.Add(f);
                    if (++batch == 256)
                    {
                        if (found != null) found(batch);
                        batch = 0;
                    }
                });
                if (!ok && first) return null;
                first = false;
            }
            if (batch > 0 && found != null) found(batch);

            // Totals from the bottom up: a folder is always made after its parent, so going
            // through the list backwards finishes every child before its parent takes it in.
            for (int i = all.Count - 1; i >= 0; i--)
            {
                SampleFolder f = all[i];
                f.TotalSamples += f.Files.Count;
                if (f.Parent == null) continue;
                f.Parent.TotalSamples += f.TotalSamples;
                f.Parent.TotalBytes += f.TotalBytes;
            }

            // Only folders with a sample below them become nodes; the rest have already passed
            // their weight up.
            List<SampleFolder> kept = new List<SampleFolder>();
            foreach (SampleFolder f in all)
            {
                if (f.Parent != null)
                {
                    if (f.TotalSamples == 0) continue;
                    f.Parent.Children.Add(f);
                }
                kept.Add(f);
            }
            return kept;
        }

        // ---------------------------------------------------------------- filters

        /// <summary>
        /// The same index without the roots that are no longer switched on — a folder removed
        /// in the dialog leaves the tree at once rather than after the next walk. The objects
        /// are shared: nothing in them depends on which other roots are present.
        /// </summary>
        public SampleIndex Only(IList<string> roots, IList<string> disabled)
        {
            List<string> keep = Effective(roots, disabled);
            SampleIndex idx = new SampleIndex();
            foreach (SampleFolder r in Roots)
                if (ContainsPath(keep, r.Path)) idx.Roots.Add(r);
            if (idx.Roots.Count == Roots.Count) return this;

            HashSet<SampleFolder> kept = new HashSet<SampleFolder>(idx.Roots);
            foreach (SampleFolder f in Folders)
                if (kept.Contains(f) || (f.Parent != null && kept.Contains(f.Parent)))
                {
                    kept.Add(f);
                    idx.Folders.Add(f);
                }
            foreach (SampleFile s in Files)
                if (kept.Contains(s.Folder)) idx.Files.Add(s);
            return idx;
        }

        public static SampleFolder RootOf(SampleFolder f)
        {
            while (f != null && f.Parent != null) f = f.Parent;
            return f;
        }

        /// <summary>
        /// Where something lies, short: the way from its root down to its folder
        /// ("Cymatics\Kicks"), or the root in full for what lies right in it — two roots on the
        /// development machine are both called "Samples".
        /// </summary>
        public static string Location(SampleFolder folder)
        {
            if (folder == null) return "";
            SampleFolder root = RootOf(folder);
            if (folder == root) return root.Path;
            return folder.Path.Substring(root.Path.TrimEnd('\\').Length + 1);
        }

        // ------------------------------------------------------------------ cache

        public static SampleIndex LoadCache()
        {
            try
            {
                if (!File.Exists(CachePath)) return Empty;
                using (FileStream fs = File.OpenRead(CachePath))
                using (BinaryReader r = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (r.ReadInt32() != CacheVersion) return Empty;
                    SampleIndex idx = new SampleIndex();

                    int folders = r.ReadInt32();
                    for (int i = 0; i < folders; i++)
                    {
                        SampleFolder f = new SampleFolder();
                        f.Path = r.ReadString();
                        int parent = r.ReadInt32();
                        f.TotalSamples = r.ReadInt32();
                        f.TotalBytes = r.ReadInt64();
                        f.Index = i;
                        if (parent >= 0)
                        {
                            f.Parent = idx.Folders[parent];     // a parent is always written first
                            f.Parent.Children.Add(f);
                            f.Name = System.IO.Path.GetFileName(f.Path);
                        }
                        else f.Name = f.Path;
                        idx.Folders.Add(f);
                    }

                    int files = r.ReadInt32();
                    for (int i = 0; i < files; i++)
                    {
                        SampleFile s = new SampleFile();
                        s.Folder = idx.Folders[r.ReadInt32()];
                        s.Name = r.ReadString();
                        s.Size = r.ReadInt64();
                        s.Silent = r.ReadBoolean();
                        s.Folder.Files.Add(s);
                        idx.Files.Add(s);
                    }

                    int roots = r.ReadInt32();
                    for (int i = 0; i < roots; i++) idx.Roots.Add(idx.Folders[r.ReadInt32()]);
                    return idx;
                }
            }
            catch { return Empty; }
        }

        public void SaveCache()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                for (int i = 0; i < Folders.Count; i++) Folders[i].Index = i;

                string tmp = CachePath + ".tmp";
                using (FileStream fs = File.Create(tmp))
                using (BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8))
                {
                    w.Write(CacheVersion);
                    w.Write(Folders.Count);
                    foreach (SampleFolder f in Folders)
                    {
                        w.Write(f.Path);
                        w.Write(f.Parent != null ? f.Parent.Index : -1);
                        w.Write(f.TotalSamples);
                        w.Write(f.TotalBytes);
                    }
                    w.Write(Files.Count);
                    foreach (SampleFile s in Files)
                    {
                        w.Write(s.Folder.Index);
                        w.Write(s.Name);
                        w.Write(s.Size);
                        w.Write(s.Silent);
                    }
                    w.Write(Roots.Count);
                    foreach (SampleFolder r in Roots) w.Write(r.Index);
                }
                if (File.Exists(CachePath)) File.Delete(CachePath);
                File.Move(tmp, CachePath);
            }
            catch { /* the cache is not critical — the worst case is walking again */ }
        }
    }
}
