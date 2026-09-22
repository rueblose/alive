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
                        if (!use.Sets.Contains(s)) use.Sets.Add(s);
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
}
