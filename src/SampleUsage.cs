using System;
using System.Collections.Generic;

namespace AbletonManager
{
    /// <summary>How one sample of the library is used: by which sets, in how many projects,
    /// and when last. LastUsed is UTC, like SetEntry.Modified.</summary>
    public sealed class SampleUse
    {
        public readonly List<SetEntry> Sets = new List<SetEntry>();
        public int Projects;
        public DateTime LastUsed;
    }

    /// <summary>The same over a folder's whole subtree.</summary>
    public sealed class FolderUse
    {
        public int Used;        // distinct samples of the subtree used at least once
        public int Projects;
        public DateTime LastUsed;
    }

    /// <summary>
    /// Which samples of the library the sets use. Counted by PROJECTS — distinct
    /// SetEntry.ProjectDir — rather than by .als files: ten versions of one track are one use.
    ///
    /// A path inside a root of the library is looked up directly. A path outside it may be a
    /// copy — Collect All puts one into the project's Samples\Imported, and on the development
    /// machine 224 of the used library files were visible only that way — so it is matched by
    /// size and name. Several files of the library with the same size and name all count as
    /// used: each of them holds the sound that plays in the track.
    /// </summary>
    public sealed class SampleUsage
    {
        public static readonly SampleUsage Empty = new SampleUsage();

        readonly Dictionary<SampleFile, SampleUse> _files = new Dictionary<SampleFile, SampleUse>();
        readonly Dictionary<SampleFolder, FolderUse> _folders = new Dictionary<SampleFolder, FolderUse>();
        readonly Dictionary<SetEntry, List<SampleFile>> _sets = new Dictionary<SetEntry, List<SampleFile>>();

        public SampleUse Of(SampleFile f)
        {
            SampleUse u;
            return f != null && _files.TryGetValue(f, out u) ? u : null;
        }

        public FolderUse Of(SampleFolder d)
        {
            FolderUse u;
            return d != null && _folders.TryGetValue(d, out u) ? u : null;
        }

        public ICollection<SampleFile> UsedFiles { get { return _files.Keys; } }

        /// <summary>The library samples one set uses — the other way round from Of.</summary>
        public List<SampleFile> FilesOf(SetEntry s)
        {
            List<SampleFile> l;
            return s != null && _sets.TryGetValue(s, out l) ? l : new List<SampleFile>();
        }

        public static SampleUsage Compute(SampleIndex index, List<SetEntry> sets)
        {
            SampleUsage u = new SampleUsage();
            if (index == null || index.Files.Count == 0 || sets == null || sets.Count == 0) return u;

            Dictionary<string, SampleFolder> byPath =
                new Dictionary<string, SampleFolder>(StringComparer.OrdinalIgnoreCase);
            foreach (SampleFolder f in index.Folders) byPath[f.Path.TrimEnd('\\')] = f;

            List<string> roots = new List<string>();
            foreach (SampleFolder r in index.Roots) roots.Add(r.Path.TrimEnd('\\') + "\\");

            // Sizes of audio files are nearly unique, so a list is made only where two files
            // do share one — a list per file would cost a few hundred thousand objects.
            Dictionary<long, object> bySize = new Dictionary<long, object>();
            foreach (SampleFile s in index.Files)
            {
                object was;
                if (!bySize.TryGetValue(s.Size, out was)) { bySize[s.Size] = s; continue; }
                List<SampleFile> list = was as List<SampleFile>;
                if (list == null)
                {
                    list = new List<SampleFile>();
                    list.Add((SampleFile)was);
                    bySize[s.Size] = list;
                }
                list.Add(s);
            }

            // A folder's names are indexed only once somebody lands in it.
            Dictionary<SampleFolder, Dictionary<string, SampleFile>> names =
                new Dictionary<SampleFolder, Dictionary<string, SampleFile>>();
            Dictionary<SampleFile, HashSet<string>> projects = new Dictionary<SampleFile, HashSet<string>>();
            List<SampleFile> hits = new List<SampleFile>();

            foreach (SetEntry s in sets)
            {
                for (int i = 0; i < s.Samples.Length; i++)
                {
                    hits.Clear();
                    string p = s.Samples[i];
                    if (Within(p, roots)) Direct(p, byPath, names, hits);
                    else Copies(p, i < s.SampleSizes.Length ? s.SampleSizes[i] : 0, bySize, hits);

                    foreach (SampleFile f in hits)
                    {
                        SampleUse use;
                        if (!u._files.TryGetValue(f, out use))
                        {
                            use = new SampleUse();
                            u._files[f] = use;
                            projects[f] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        }
                        if (!use.Sets.Contains(s))
                        {
                            use.Sets.Add(s);
                            List<SampleFile> mine;
                            if (!u._sets.TryGetValue(s, out mine)) { mine = new List<SampleFile>(); u._sets[s] = mine; }
                            mine.Add(f);
                        }
                        projects[f].Add(s.ProjectDir);
                        if (s.Modified > use.LastUsed) use.LastUsed = s.Modified;
                    }
                }
            }

            // Up the folders, from every used file to its root. Sets of projects are made only
            // on the folders of these chains.
            Dictionary<SampleFolder, HashSet<string>> folderProjects = new Dictionary<SampleFolder, HashSet<string>>();
            foreach (KeyValuePair<SampleFile, SampleUse> kv in u._files)
            {
                HashSet<string> mine = projects[kv.Key];
                kv.Value.Projects = mine.Count;
                for (SampleFolder d = kv.Key.Folder; d != null; d = d.Parent)
                {
                    FolderUse fu;
                    HashSet<string> fp;
                    if (!u._folders.TryGetValue(d, out fu))
                    {
                        fu = new FolderUse();
                        u._folders[d] = fu;
                        fp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        folderProjects[d] = fp;
                    }
                    else fp = folderProjects[d];
                    fu.Used++;
                    fp.UnionWith(mine);
                    if (kv.Value.LastUsed > fu.LastUsed) fu.LastUsed = kv.Value.LastUsed;
                }
            }
            foreach (KeyValuePair<SampleFolder, FolderUse> kv in u._folders)
                kv.Value.Projects = folderProjects[kv.Key].Count;
            return u;
        }

        static bool Within(string path, List<string> roots)
        {
            foreach (string r in roots)
                if (path.StartsWith(r, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Direct(string p, Dictionary<string, SampleFolder> byPath,
                           Dictionary<SampleFolder, Dictionary<string, SampleFile>> names,
                           List<SampleFile> hits)
        {
            int slash = p.LastIndexOf('\\');
            if (slash <= 0) return;
            SampleFolder dir;
            if (!byPath.TryGetValue(p.Substring(0, slash), out dir)) return;

            Dictionary<string, SampleFile> map;
            if (!names.TryGetValue(dir, out map))
            {
                map = new Dictionary<string, SampleFile>(StringComparer.OrdinalIgnoreCase);
                foreach (SampleFile f in dir.Files) map[f.Name] = f;
                names[dir] = map;
            }
            SampleFile hit;
            if (map.TryGetValue(p.Substring(slash + 1), out hit)) hits.Add(hit);
        }

        static void Copies(string p, long size, Dictionary<long, object> bySize, List<SampleFile> hits)
        {
            object c;
            if (size <= 0 || !bySize.TryGetValue(size, out c)) return;
            string name = p.Substring(p.LastIndexOf('\\') + 1);
            SampleFile one = c as SampleFile;
            if (one != null)
            {
                if (string.Equals(one.Name, name, StringComparison.OrdinalIgnoreCase)) hits.Add(one);
                return;
            }
            foreach (SampleFile f in (List<SampleFile>)c)
                if (string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)) hits.Add(f);
        }

        // ----------------------------------------------------------------- views

        /// <summary>More projects first, then the more recently used, then by name.</summary>
        public int CompareUse(SampleFile a, SampleFile b)
        {
            SampleUse ua = Of(a), ub = Of(b);
            int pa = ua != null ? ua.Projects : 0, pb = ub != null ? ub.Projects : 0;
            if (pa != pb) return pb.CompareTo(pa);
            DateTime la = ua != null ? ua.LastUsed : DateTime.MinValue;
            DateTime lb = ub != null ? ub.LastUsed : DateTime.MinValue;
            if (la != lb) return lb.CompareTo(la);
            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The used samples of a folder's subtree, the most used first.</summary>
        public List<SampleFile> UsedUnder(SampleFolder d)
        {
            List<SampleFile> list = new List<SampleFile>();
            if (Of(d) == null) return list;
            foreach (SampleFile f in _files.Keys)
                for (SampleFolder x = f.Folder; x != null; x = x.Parent)
                    if (x == d) { list.Add(f); break; }
            list.Sort(CompareUse);
            return list;
        }

        /// <summary>
        /// The topmost folders nothing is used from: unused themselves, while their parent is
        /// used (or there is no parent). These are the pieces that can go whole — listing every
        /// unused subfolder under them as well would only repeat them.
        /// </summary>
        public List<SampleFolder> NeverUsed(SampleIndex index)
        {
            List<SampleFolder> list = new List<SampleFolder>();
            foreach (SampleFolder f in index.Folders)
            {
                if (f.TotalSamples == 0 || Of(f) != null) continue;
                if (f.Parent == null || Of(f.Parent) != null) list.Add(f);
            }
            return list;
        }

        /// <summary>The newest set of every project among the given ones, newest first — a
        /// project in a list stands under the name of its latest version.</summary>
        public static List<SetEntry> Newest(IEnumerable<SetEntry> sets)
        {
            Dictionary<string, SetEntry> best = new Dictionary<string, SetEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (SetEntry s in sets)
            {
                SetEntry b;
                if (!best.TryGetValue(s.ProjectDir, out b) || s.Modified > b.Modified) best[s.ProjectDir] = s;
            }
            List<SetEntry> list = new List<SetEntry>(best.Values);
            list.Sort(delegate (SetEntry a, SetEntry b) { return b.Modified.CompareTo(a.Modified); });
            return list;
        }
    }

    /// <summary>
    /// The same sample in more than one place of the library: equal name, size and content
    /// hash (SampleFile.Print — name and size alone also pair a pack's Dry and Wet takes of one
    /// sound: 1,904 of the 8,001 namesakes on the development machine). The typical case is a pack unpacked twice, or the same one-shots shipped in
    /// several packs. A file without a print is never anybody's copy: telling a person that two
    /// different sounds are one is worse than missing a copy.
    /// </summary>
    public sealed class SampleCopies
    {
        public static readonly SampleCopies Empty = new SampleCopies();

        // Every file that has a copy maps to its whole group, the file itself included.
        readonly Dictionary<SampleFile, List<SampleFile>> _groups = new Dictionary<SampleFile, List<SampleFile>>();
        readonly Dictionary<SampleFolder, int> _files = new Dictionary<SampleFolder, int>();
        readonly Dictionary<SampleFolder, long> _bytes = new Dictionary<SampleFolder, long>();

        /// <summary>Every file with a copy, the group that wastes the most room first and its
        /// copies side by side — the Duplicates lens as it is.</summary>
        public readonly List<SampleFile> Files = new List<SampleFile>();

        /// <summary>What the copies take beyond one of each.</summary>
        public long ExtraBytes;

        /// <summary>The other places this very sample lies in; empty — it is the only one.</summary>
        public List<SampleFile> Others(SampleFile f)
        {
            List<SampleFile> g, others = new List<SampleFile>();
            if (f == null || !_groups.TryGetValue(f, out g)) return others;
            foreach (SampleFile x in g) if (!ReferenceEquals(x, f)) others.Add(x);
            return others;
        }

        /// <summary>In how many other places this sample lies; 0 — nowhere else.</summary>
        public int CopiesOf(SampleFile f)
        {
            List<SampleFile> g;
            return f != null && _groups.TryGetValue(f, out g) ? g.Count - 1 : 0;
        }

        /// <summary>How many samples of a folder's subtree lie somewhere else too, and what
        /// they weigh.</summary>
        public int FilesIn(SampleFolder d) { int n; return d != null && _files.TryGetValue(d, out n) ? n : 0; }
        public long BytesIn(SampleFolder d) { long n; return d != null && _bytes.TryGetValue(d, out n) ? n : 0; }

        public static SampleCopies Find(SampleIndex index)
        {
            SampleCopies c = new SampleCopies();
            if (index == null || index.Files.Count == 0) return c;

            Dictionary<string, List<SampleFile>> byKey = new Dictionary<string, List<SampleFile>>(StringComparer.OrdinalIgnoreCase);
            foreach (SampleFile f in index.Files)
            {
                if (f.Print == 0) continue;
                string key = f.Size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|"
                           + f.Print.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + f.Name;
                List<SampleFile> g;
                if (!byKey.TryGetValue(key, out g)) { g = new List<SampleFile>(1); byKey[key] = g; }
                g.Add(f);
            }

            List<List<SampleFile>> groups = new List<List<SampleFile>>();
            foreach (List<SampleFile> g in byKey.Values)
            {
                if (g.Count < 2 || g[0].Size <= 0) continue;
                groups.Add(g);
                c.ExtraBytes += g[0].Size * (g.Count - 1);
                foreach (SampleFile f in g)
                {
                    c._groups[f] = g;
                    for (SampleFolder d = f.Folder; d != null; d = d.Parent)
                    {
                        int n; long b;
                        c._files.TryGetValue(d, out n);
                        c._bytes.TryGetValue(d, out b);
                        c._files[d] = n + 1;
                        c._bytes[d] = b + f.Size;
                    }
                }
            }

            groups.Sort(delegate (List<SampleFile> a, List<SampleFile> b)
            {
                int r = (b[0].Size * (b.Count - 1)).CompareTo(a[0].Size * (a.Count - 1));
                return r != 0 ? r : string.Compare(a[0].Name, b[0].Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (List<SampleFile> g in groups)
            {
                g.Sort(delegate (SampleFile a, SampleFile b)
                    { return string.Compare(a.Folder.Path, b.Folder.Path, StringComparison.OrdinalIgnoreCase); });
                c.Files.AddRange(g);
            }
            return c;
        }
    }
}
