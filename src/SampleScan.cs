using System;
using System.Collections.Generic;
using System.IO;

namespace AbletonManager
{
    /// <summary>
    /// Where a media file a set refers to came from. The categories are the same four "Collect
    /// All and Save" has in Live, plus "already in the project": Live does not ask about those,
    /// they are in place as it is.
    /// </summary>
    public enum SampleOrigin
    {
        InProject,     // inside the set's own folder — always copied when collecting
        OtherProject,  // inside somebody else's "* Project" folder
        UserLibrary,   // Documents\Ableton\User Library
        FactoryPack,   // an installed pack, the Core Library or Builtin
        Elsewhere,     // simply somewhere on disk
        Missing        // not found
    }

    /// <summary>
    /// One media file a set needs — together with every reference leading to it.
    ///
    /// A file, not a reference: one set points at its own Samples folder with hundreds of
    /// clips, and on a real library 28,631 references boil down to roughly 1,100 distinct
    /// files. What has to be copied and shown are files, while the reference numbers are needed
    /// later by the patcher — every one of them has to be rewritten.
    /// </summary>
    public sealed class SampleDep
    {
        public FileRefInfo Ref;            // one of the references — the path and the pack name come from it
        public ResolvedRef Resolved;       // where it was found
        public SampleOrigin Origin;
        public string PackName = "";       // filled in for FactoryPack
        public long Size;                  // from disk; 0 if it was not found or could not be counted
        public bool IsDevice;              // .amxd (MxPatchRef) rather than a sample

        /// <summary>FileRef numbers in document order — AlsSamplePatch addresses by
        /// them.</summary>
        public readonly List<int> RefIndexes = new List<int>();

        public string Path
        {
            get { return Resolved != null ? Resolved.ResolvedPath : ""; }
        }

        public string Name
        {
            get
            {
                try { return System.IO.Path.GetFileName(Path); }
                catch { return ""; }
            }
        }
    }

    /// <summary>
    /// Which media files a set needs and where they come from. It reads nothing from disk
    /// beyond what AlsFile has already read, and writes nothing.
    /// </summary>
    public static class SampleScan
    {
        /// <summary>
        /// setDir is the .als folder itself, not the project folder. ProjectIndex.Build counts
        /// it the same way, and these two must not diverge: otherwise "in the project" here and
        /// "lost" in the catalog would be talking about different things.
        /// </summary>
        public static List<SampleDep> Of(AlsInfo info, string setDir, LiveEnvironment env)
        {
            List<SampleDep> list = new List<SampleDep>();
            if (info == null) return list;

            // Two levels of folding. First by the reference itself: identical references
            // resolve identically, and there is no point calling RefResolver 28 thousand times.
            // Then by the path found: different references (one through a pack, another by
            // absolute path) lead to one file, and it has to be copied once.
            Dictionary<string, SampleDep> byRaw = new Dictionary<string, SampleDep>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SampleDep> byFile = new Dictionary<string, SampleDep>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < info.Files.Count; i++)
            {
                FileRefInfo fr = info.Files[i];
                bool device = string.Equals(fr.Container, "MxPatchRef", StringComparison.Ordinal);
                if (!fr.IsSampleDependency && !device) continue;

                string raw = fr.RelativePathType.ToString(System.Globalization.CultureInfo.InvariantCulture)
                           + "|" + fr.RelativePath + "|" + fr.AbsolutePath + "|" + fr.LivePackName;

                SampleDep dep;
                if (byRaw.TryGetValue(raw, out dep))
                {
                    if (dep != null) dep.RefIndexes.Add(i);
                    continue;
                }

                ResolvedRef rr = RefResolver.Resolve(fr, setDir, env);
                if (rr.Status == RefStatus.Empty)
                {
                    byRaw[raw] = null;      // a blank — remembered so it is not resolved again
                    continue;
                }

                string fileKey = rr.Status == RefStatus.Found
                    ? rr.ResolvedPath
                    : "?" + (fr.RelativePath.Length > 0 ? fr.RelativePath : fr.AbsolutePath);

                if (byFile.TryGetValue(fileKey, out dep))
                {
                    dep.RefIndexes.Add(i);
                    byRaw[raw] = dep;
                    continue;
                }

                dep = new SampleDep();
                dep.Ref = fr;
                dep.Resolved = rr;
                dep.IsDevice = device;
                dep.RefIndexes.Add(i);
                dep.Origin = Classify(rr, fr, setDir, env, out dep.PackName);
                dep.Size = dep.Origin == SampleOrigin.Missing ? 0L : SizeOf(rr.ResolvedPath);

                byRaw[raw] = dep;
                byFile[fileKey] = dep;
                list.Add(dep);
            }

            return list;
        }

        /// <summary>
        /// The order of the checks matters. "In the project" comes first: a project lying
        /// inside the User Library is still one's own project, and its samples are not "from
        /// the library".
        /// </summary>
        static SampleOrigin Classify(ResolvedRef rr, FileRefInfo fr, string setDir,
                                     LiveEnvironment env, out string packName)
        {
            packName = "";
            if (rr.Status != RefStatus.Found)
            {
                if (rr.Status == RefStatus.MissingPack) packName = fr.LivePackName ?? "";
                return SampleOrigin.Missing;
            }

            string p = rr.ResolvedPath;

            if (Under(p, setDir)) return SampleOrigin.InProject;

            if (fr.RelativePathType == 5 && fr.LivePackName.Length > 0)
            {
                packName = fr.LivePackName;
                return SampleOrigin.FactoryPack;
            }

            foreach (KeyValuePair<string, string> kv in env.Packs)
                if (Under(p, kv.Value)) { packName = kv.Key; return SampleOrigin.FactoryPack; }

            if (fr.RelativePathType == 7 || Under(p, env.Builtin) || Under(p, env.CoreLibrary))
            {
                packName = "Core Library";
                return SampleOrigin.FactoryPack;
            }

            if (fr.RelativePathType == 6 || Under(p, env.UserLibrary)) return SampleOrigin.UserLibrary;

            if (InSomeProject(p)) return SampleOrigin.OtherProject;

            return SampleOrigin.Elsewhere;
        }

        /// <summary>Whether a path lies inside a root. Compared as full paths, or "…\Samples2"
        /// would pass for "…\Samples".</summary>
        static bool Under(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            try
            {
                string a = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                string b = System.IO.Path.GetFullPath(path);
                return b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>By the same rule as SetEntry.ProjectDir: no more than four levels
        /// up.</summary>
        static bool InSomeProject(string path)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(System.IO.Path.GetDirectoryName(path));
                for (int i = 0; i < 4 && d != null; i++)
                {
                    if (d.Name.EndsWith(" Project", StringComparison.OrdinalIgnoreCase)) return true;
                    d = d.Parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// .adg and .amxd are sometimes folders (see RefResolver.Probe) — their size is the sum
        /// of the files inside, not 0. A 0 on a dependency that was found (Origin != Missing)
        /// would otherwise be indistinguishable from "not found", which contradicts the comment
        /// on SampleDep.Size.
        /// </summary>
        static long SizeOf(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists) return fi.Length;
            }
            catch { return 0L; }

            if (!Directory.Exists(path)) return 0L;
            return DirSize(path);
        }

        /// <summary>
        /// The recursive sum of file sizes in a folder. Walked by hand rather than through
        /// Directory.GetFiles(path, "*", SearchOption.AllDirectories): that one throws on the
        /// first inaccessible subfolder and gives back nothing of what it had already counted.
        /// Here an inaccessible subfolder only cuts the count short for itself — we take the
        /// most of what could be counted.
        /// </summary>
        static long DirSize(string dir) { return DirSize(dir, 0); }

        /// <summary>
        /// The depth limit — the same number RenderIndex.Walk uses. Without it a junction loop
        /// in the file system (and .adg/.amxd bundles are ordinary folders, nothing stops one
        /// being mounted inside itself) inflates the sum: the walk cannot tell a repeat visit
        /// to the same folder from a new one, only the depth. It does not catch the crash — on
        /// a real loop that is cut short by PathTooLongException inside the try above — but
        /// without the limit the number manages to overshoot several times over before the path
        /// grows to that length.
        /// </summary>
        static long DirSize(string dir, int depth)
        {
            long total = 0L;
            if (depth > 4) return total;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return total; }
            foreach (string f in files)
            {
                try { total += new FileInfo(f).Length; }
                catch { }
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { return total; }
            foreach (string d in subdirs)
                total += DirSize(d, depth + 1);

            return total;
        }
    }
}
