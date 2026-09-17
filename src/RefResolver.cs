using System;
using System.Collections.Generic;
using System.IO;

namespace AbletonManager
{
    public enum RefStatus
    {
        Empty,        // no reference: an empty FileRef placeholder, and most of a set is these
        Found,        // the file is where it should be
        Missing,      // not found — this one is a real loss
        MissingPack   // the Live Pack the set refers to was not found
    }

    public sealed class ResolvedRef
    {
        public FileRefInfo Ref;
        public RefStatus Status;
        public string ResolvedPath = "";
        public string Note = "";
    }

    /// <summary>
    /// RelativePathType in the .als names the ROOT that RelativePath is measured from. The
    /// values were worked out on a real library (see README):
    ///   0 - no reference
    ///   1 - from the project folder, may go upwards (../../Samples/...)
    ///   3 - inside the project folder
    ///   5 - from a Live Pack root, the pack name in LivePackName
    ///   6 - from the User Library
    ///   7 - from Resources\Builtin of the installed Live
    /// The absolute path is only used as the last hint: in other people's and older projects it
    /// leads to another machine, another drive, or a previous Live version.
    /// </summary>
    public static class RefResolver
    {
        // ------------------------------------------------- cache of file probes
        //
        // Measured on a real library: twenty sets hold 29,643 sample references, and only 1,112
        // distinct paths among them — meaning 96% of the "does this file exist" checks ask for
        // exactly what has already been asked. One set points at its own Samples folder with
        // hundreds of clips, while shared libraries and packs repeat across every set at once.
        //
        // What we cache is the result of the PROBE, not the reference itself: resolved paths
        // are needed whole — the counters use them to drop repeats of one and the same file.
        //
        // It lives only for the duration of one scan: the set of files on disk changes without
        // asking, and an answer remembered between scans would be a lie — "rescan" has to mean
        // "check again".

        static volatile Dictionary<string, bool> _probe;

        public static void BeginScan()
        {
            _probe = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        public static void EndScan()
        {
            _probe = null;
        }

        static bool PathExists(string full)
        {
            Dictionary<string, bool> cache = _probe;
            if (cache == null) return Probe(full);

            bool ok;
            lock (cache) { if (cache.TryGetValue(full, out ok)) return ok; }
            ok = Probe(full);
            lock (cache) cache[full] = ok;
            return ok;
        }

        /// <summary>Directory.Exists is not redundant here: .adg and .amxd are sometimes
        /// folders.</summary>
        static bool Probe(string full)
        {
            try { return File.Exists(full) || Directory.Exists(full); }
            catch { return false; }
        }

        public static ResolvedRef Resolve(FileRefInfo fr, string projectDir, LiveEnvironment env)
        {
            ResolvedRef res = new ResolvedRef();
            res.Ref = fr;

            bool noRel = string.IsNullOrEmpty(fr.RelativePath);
            bool noAbs = string.IsNullOrEmpty(fr.AbsolutePath);
            if (noRel && noAbs) { res.Status = RefStatus.Empty; return res; }

            string rel = noRel ? null : fr.RelativePath.Replace('/', Path.DirectorySeparatorChar);

            switch (fr.RelativePathType)
            {
                case 1:
                case 3:
                    if (Try(projectDir, rel, res)) return res;
                    break;

                case 5:
                    string packRoot = null;
                    if (!string.IsNullOrEmpty(fr.LivePackName))
                        env.Packs.TryGetValue(fr.LivePackName, out packRoot);
                    if (Try(packRoot, rel, res)) return res;
                    if (packRoot == null && !string.IsNullOrEmpty(fr.LivePackName))
                    {
                        // the pack is not installed at all — a different trouble from a lost
                        // sample
                        if (!Exists(fr.AbsolutePath))
                        {
                            res.Status = RefStatus.MissingPack;
                            res.Note = fr.LivePackName;
                            res.ResolvedPath = fr.RelativePath;
                            return res;
                        }
                    }
                    break;

                case 6:
                    if (Try(env.UserLibrary, rel, res)) return res;
                    break;

                case 7:
                    if (Try(env.Builtin, rel, res)) return res;
                    if (Try(env.CoreLibrary, rel, res)) return res;
                    break;
            }

            // fallbacks: the absolute path, then the other roots
            if (Exists(fr.AbsolutePath))
            {
                res.Status = RefStatus.Found;
                res.ResolvedPath = fr.AbsolutePath.Replace('/', Path.DirectorySeparatorChar);
                return res;
            }
            if (Try(projectDir, rel, res)) return res;
            if (Try(env.UserLibrary, rel, res)) return res;
            if (Try(env.Builtin, rel, res)) return res;
            if (Try(env.CoreLibrary, rel, res)) return res;

            res.Status = RefStatus.Missing;
            res.ResolvedPath = noAbs ? fr.RelativePath : fr.AbsolutePath;
            return res;
        }

        static bool Try(string root, string relative, ResolvedRef res)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relative)) return false;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, relative)); }
            catch { return false; }

            if (PathExists(full))
            {
                res.Status = RefStatus.Found;
                res.ResolvedPath = full;
                return true;
            }
            return false;
        }

        static bool Exists(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            return PathExists(p.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
