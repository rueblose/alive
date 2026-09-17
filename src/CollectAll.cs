using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace AbletonManager
{
    /// <summary>
    /// What to copy when collecting. The same four questions "Collect All and Save" asks in
    /// Live. Files already lying in the set's folder are not asked about — Live does not ask
    /// about them either.
    /// </summary>
    public sealed class CollectOptions
    {
        public bool FromElsewhere = true;
        public bool FromOtherProjects = true;
        public bool FromUserLibrary = true;

        /// <summary>
        /// The only one off by default. Anyone who bought a pack has it, while it weighs an
        /// order of magnitude more than everything else put together: on by default it turns
        /// "collect the project" into "copy half the library" for whoever pressed without
        /// looking.
        /// </summary>
        public bool FromFactoryPacks;

        /// <summary>Put the collected material into a .zip alongside and leave no folder
        /// behind.</summary>
        public bool ToZip;
    }

    /// <summary>What exactly will be done. Computed before the boxes are shown and recomputed
    /// on every click.</summary>
    public sealed class CollectPlan
    {
        public string TargetDir = "";
        public bool Zip;

        /// <summary>An archive instead of a folder — same name, alongside. A taken name is
        /// filtered out by FreeTarget.</summary>
        public string ZipPath { get { return TargetDir + ".zip"; } }

        public readonly List<SampleDep> Copy = new List<SampleDep>();
        public readonly List<SampleDep> Skipped = new List<SampleDep>();
        public readonly List<SampleDep> NotFound = new List<SampleDep>();
        public long TotalBytes;
        public long FreeBytes;

        /// <summary>What could not be copied — busy, or the path too long. Filled in by
        /// Run.</summary>
        public readonly List<string> Failed = new List<string>();

        /// <summary>FileRef number -> new path. Empty for in-project ones: their path is right
        /// as it is.</summary>
        public readonly Dictionary<int, NewRef> Rewrites = new Dictionary<int, NewRef>();

        /// <summary>Where a file will land — relative to TargetDir, with forward
        /// slashes.</summary>
        public readonly Dictionary<SampleDep, string> Dest = new Dictionary<SampleDep, string>();
    }

    /// <summary>
    /// Collecting a project into a portable folder. The logic sits apart from the window — by
    /// the same division as RescueSession and RescueDialog.
    ///
    /// The original is not touched: what is collected goes into a new folder, and the source
    /// .als is opened read-only.
    /// </summary>
    public static class CollectAll
    {
        const string ImportedDir = "Samples/Imported";
        const string DevicesDir = "Devices";
        const string ProjectInfo = "Ableton Project Info";

        public static bool Wanted(SampleOrigin o, CollectOptions opt)
        {
            switch (o)
            {
                case SampleOrigin.InProject: return true;      // already ours, always copied
                case SampleOrigin.Elsewhere: return opt.FromElsewhere;
                case SampleOrigin.OtherProject: return opt.FromOtherProjects;
                case SampleOrigin.UserLibrary: return opt.FromUserLibrary;
                case SampleOrigin.FactoryPack: return opt.FromFactoryPacks;
                default: return false;
            }
        }

        public static CollectPlan Plan(SetEntry set, List<SampleDep> deps, CollectOptions opt)
        {
            CollectPlan plan = new CollectPlan();
            plan.TargetDir = FreeTarget(set);
            plan.Zip = opt.ToZip;

            string setDir = Path.GetDirectoryName(set.Path);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // The first pass only stakes out the names of in-project files. This used to be
            // done in a single pass together with handing out names to external ones through
            // Unique(), and the order in deps (which is the order of FileRefs in the document —
            // chosen by the set's author, not by us) decided who took
            // "Samples/Imported/kick.wav" first: let an external dependency come before an
            // in-project one of the same name and Unique() gave the path to it, while the
            // in-project one staked out that same path a second time — both ended up with one
            // dst, and File.Copy in Run silently overwrote one with the other. With taken
            // seeded in full before the first call to Unique(), the result no longer depends on
            // who came first in the document.
            foreach (SampleDep seed in deps)
            {
                if (seed.Origin != SampleOrigin.InProject) continue;
                string seedRel = Relative(setDir, seed.Path);
                if (seedRel.Length > 0) taken.Add(seedRel);
            }

            foreach (SampleDep d in deps)
            {
                if (d.Origin == SampleOrigin.Missing) { plan.NotFound.Add(d); continue; }
                if (!Wanted(d.Origin, opt)) { plan.Skipped.Add(d); continue; }

                string rel;
                if (d.Origin == SampleOrigin.InProject)
                {
                    // We reproduce the set folder's structure, so the path in the copy is right
                    // as it is — neither a rename nor an edit of the reference is needed. taken
                    // has already been seeded with it by the first pass above.
                    rel = Relative(setDir, d.Path);
                    if (rel.Length == 0) { plan.Skipped.Add(d); continue; }
                }
                else
                {
                    string folder = d.IsDevice ? DevicesDir : ImportedDir;
                    rel = Unique(taken, folder + "/" + d.Name);

                    NewRef nr = new NewRef();
                    nr.RelativePath = rel;
                    // AbsolutePath is not filled in: it derives from TargetDir, and that can
                    // still change between the plan and the collecting. We complete it in Run.
                    nr.RelativePathType = 3;
                    nr.ClearPack = true;
                    foreach (int i in d.RefIndexes) plan.Rewrites[i] = nr;
                }

                plan.Dest[d] = rel;
                plan.Copy.Add(d);
                plan.TotalBytes += d.Size;
            }

            plan.FreeBytes = FreeSpace(plan.TargetDir);
            return plan;
        }

        public static void Run(CollectPlan plan, SetEntry set, AlsInfo info,
                               Action<int, int, string> progress, CancellationToken cancel)
        {
            bool ok = false;
            // CreateDirectory on an existing folder is a quiet no-op and will not say whether
            // it created anything. We remember it ourselves while the folder is certainly not
            // there yet: Plan() for two runs over one set, computed before either created a
            // folder on disk, can hand out one and the same TargetDir. In archive mode there is
            // no folder at all — nothing for the finally to remove.
            bool createdHere = !plan.Zip && !Directory.Exists(plan.TargetDir);
            Target target = null;
            try
            {
                target = plan.Zip ? (Target)new ZipTarget(plan.ZipPath) : new FolderTarget(plan.TargetDir);
                PutProjectInfo(set, target);

                int done = 0, total = plan.Copy.Count;
                foreach (SampleDep d in plan.Copy)
                {
                    cancel.ThrowIfCancellationRequested();

                    string rel;
                    if (!plan.Dest.TryGetValue(d, out rel)) continue;
                    try
                    {
                        // .amxd and .adg are sometimes bundle folders (see RefResolver.Probe) —
                        // one file will not take them. The dependency still has to be carried
                        // over whole, with everything inside.
                        if (Directory.Exists(d.Path)) target.PutDir(d.Path, rel);
                        else target.PutFile(d.Path, rel);
                    }
                    catch (Exception ex)
                    {
                        // A busy or over-long path is no reason to abandon the collecting: the
                        // person needs the other files. But keeping quiet will not do either —
                        // a copy without a sample looks whole and sounds wrong.
                        plan.Failed.Add(d.Name);
                        Diag.Line("collect: cannot copy " + d.Path + ": " + ex.Message);

                        // It did not copy — the reference has to stay as it was in the original
                        // rather than point at a file that is not in the copy: the same spec as
                        // for "sample not found" — the reference stays as it was and the loss
                        // count goes into the report.
                        foreach (int i in d.RefIndexes) plan.Rewrites.Remove(i);

                        // A bundle (.adg/.amxd — see RefResolver.Probe) may have landed
                        // half-way: it is carried over file by file. A half-copied bundle is
                        // worse than a missing one — it looks like a device with half the
                        // preset gone.
                        if (Directory.Exists(d.Path)) target.Undo(rel);
                    }

                    done++;
                    if (progress != null) progress(done, total, d.Name);
                }

                // The absolute path is completed here rather than in Plan: by this moment
                // TargetDir is final, and a derived value will not drift away from it. In
                // archive mode the TargetDir folder does not exist, and the path is right
                // anyway: the archive carries its name, and unpacking next to it gives exactly
                // that folder.
                string root = plan.TargetDir.Replace('\\', '/');
                foreach (KeyValuePair<int, NewRef> kv in plan.Rewrites)
                    kv.Value.AbsolutePath = root + "/" + kv.Value.RelativePath;

                cancel.ThrowIfCancellationRequested();
                PutSet(plan, set, info, target);
                ok = true;
            }
            finally
            {
                // The archive is closed before the finally gets round to deleting it.
                if (target != null) target.Dispose();
                // We delete only what this run created. If TargetDir already existed before
                // Run, somebody else's already-collected copy may lie there (a second Plan()
                // over the same set), and that we do not touch.
                if (!ok && createdHere) { try { Directory.Delete(plan.TargetDir, true); } catch { } }
                // An unfinished archive looks like a finished export with half a set inside.
                if (!ok && plan.Zip) { try { File.Delete(plan.ZipPath); } catch { } }
            }
        }

        /// <summary>
        /// Live decides a folder is a project by the presence of "Ableton Project Info". If the
        /// original has none (the set lies on its own), we make an empty one: Live will write
        /// its own into it on the first save.
        /// </summary>
        static void PutProjectInfo(SetEntry set, Target target)
        {
            string src = Path.Combine(set.ProjectDir, ProjectInfo);
            string[] files = Directory.Exists(src) ? Directory.GetFiles(src) : new string[0];
            if (files.Length == 0) { target.PutEmptyDir(ProjectInfo); return; }

            try
            {
                foreach (string f in files)
                    target.PutFile(f, ProjectInfo + "/" + Path.GetFileName(f));
            }
            catch (Exception ex) { Diag.Line("collect: project info: " + ex.Message); }
        }

        /// <summary>
        /// The .als is put in last: collecting cut short must not leave an export that looks
        /// finished.
        ///
        /// AlsSamplePatch.Rewrite writes into a file, so for an archive the set is first
        /// assembled in a temporary one and goes into the zip from there. One file per set is
        /// not the kind of staging worth teaching Rewrite to work on streams for.
        /// </summary>
        static void PutSet(CollectPlan plan, SetEntry set, AlsInfo info, Target target)
        {
            string rel = set.Name + ".als";
            if (!plan.Zip)
            {
                AlsSamplePatch.Rewrite(set.Path, Path.Combine(plan.TargetDir, rel),
                                       plan.Rewrites, info.Files.Count);
                return;
            }

            string tmp = Path.Combine(Path.GetTempPath(),
                                      "alive-" + Guid.NewGuid().ToString("N") + ".als");
            try
            {
                AlsSamplePatch.Rewrite(set.Path, tmp, plan.Rewrites, info.Files.Count);
                target.PutFile(tmp, rel);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ------------------------------------------------------- where it is put

        /// <summary>
        /// Where the collected material goes: into a folder on disk, or straight into archive
        /// entries.
        ///
        /// The archive is written as a stream rather than by the trick of "copy everything into
        /// a folder, pack it, delete the folder". That halves the writing — gigabyte packs pass
        /// through the disk once instead of twice — and it also leaves no sequence of "gather
        /// other people's files into a heap, pack it, wipe the heap", which is what antivirus
        /// behavioural heuristics object to.
        /// </summary>
        abstract class Target : IDisposable
        {
            public abstract void PutFile(string src, string rel);
            public abstract void PutDir(string src, string rel);
            public abstract void PutEmptyDir(string rel);

            /// <summary>Remove a bundle that landed half-way.</summary>
            public abstract void Undo(string rel);

            public virtual void Dispose() { }
        }

        sealed class FolderTarget : Target
        {
            readonly string _root;

            public FolderTarget(string root)
            {
                _root = root;
                Directory.CreateDirectory(root);
            }

            string Abs(string rel)
            {
                return Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            }

            public override void PutFile(string src, string rel)
            {
                string dst = Abs(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }

            public override void PutDir(string src, string rel)
            {
                string dst = Abs(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                CopyDir(src, dst);
            }

            public override void PutEmptyDir(string rel) { Directory.CreateDirectory(Abs(rel)); }

            public override void Undo(string rel)
            {
                try { Directory.Delete(Abs(rel), true); } catch { }
            }
        }

        sealed class ZipTarget : Target
        {
            readonly ZipArchive _zip;

            /// <summary>
            /// Fastest rather than Optimal: samples are wav and flac, there is nothing in them
            /// to squeeze, while the difference in time over gigabytes is visible to the naked
            /// eye.
            /// </summary>
            const CompressionLevel Level = CompressionLevel.Fastest;

            public ZipTarget(string path) { _zip = ZipFile.Open(path, ZipArchiveMode.Create); }

            public override void PutFile(string src, string rel)
            {
                // The source file may be held open by an antivirus or an indexer and come back
                // "busy with another process". They do not hold it long, so we wait and try
                // again; CreateEntryFromFile opens the file BEFORE creating the entry, so a
                // retry leaves no halves in the archive.
                for (int attempt = 0; ; attempt++)
                {
                    try { _zip.CreateEntryFromFile(src, rel, Level); return; }
                    catch (Exception ex)
                    {
                        bool busy = ex is IOException || ex is UnauthorizedAccessException;
                        if (!busy || attempt == 5) throw;
                        Thread.Sleep(200);
                    }
                }
            }

            public override void PutDir(string src, string rel)
            {
                foreach (string f in Directory.GetFiles(src))
                    PutFile(f, rel + "/" + Path.GetFileName(f));
                foreach (string d in Directory.GetDirectories(src))
                    PutDir(d, rel + "/" + Path.GetFileName(d));
            }

            public override void PutEmptyDir(string rel) { _zip.CreateEntry(rel + "/"); }

            /// <summary>
            /// In an archive open for writing there is nothing to erase: ZipArchiveMode.Create
            /// writes as a stream and never goes back, while Update would hold the whole
            /// archive in memory — not an option on gigabyte packs. Half a bundle will stay
            /// inside, but the reference to it has already been dropped from the .als (see the
            /// catch in Run), so for Live it is not there: extra files in the archive rather
            /// than a broken device in the set.
            /// </summary>
            public override void Undo(string rel) { }

            public override void Dispose() { _zip.Dispose(); }
        }

        /// <summary>
        /// A recursive copy of a bundle folder (.adg, .amxd) with everything inside. Unlike
        /// SampleScan.DirSize we do not swallow an error from a subfolder here: a partially
        /// copied device is worse than an outright refusal, which reaches plan.Failed through
        /// the catch in Run.
        /// </summary>
        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // ------------------------------------------------------------------- paths

        /// <summary>
        /// "&lt;Project&gt;\&lt;set name&gt; Project_export", and with a counter when taken.
        /// Next to the source, with no intermediate folder: an export is looked for where the
        /// project itself is.
        /// </summary>
        static string FreeTarget(SetEntry set)
        {
            for (int n = 1; n < 1000; n++)
            {
                string name = set.Name + " Project_export"
                            + (n == 1 ? "" : " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string dir = Path.Combine(set.ProjectDir, name);
                // The name has to be free both as a folder and as an archive: in .zip mode the
                // folder is deleted after collecting, and the next export of the same set would
                // otherwise find the name "free" and overwrite the previous archive.
                if (!Directory.Exists(dir) && !File.Exists(dir + ".zip")) return dir;
            }
            return Path.Combine(set.ProjectDir, set.Name + " Project_export " + DateTime.Now.Ticks);
        }

        /// <summary>A path relative to a root, with forward slashes. Empty if the path is not
        /// inside the root.</summary>
        static string Relative(string root, string path)
        {
            try
            {
                string a = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                string b = Path.GetFullPath(path);
                if (!b.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return "";
                return b.Substring(a.Length).Replace('\\', '/');
            }
            catch { return ""; }
        }

        /// <summary>Separate names that clashed: "kick.wav", "kick 2.wav", "kick
        /// 3.wav".</summary>
        static string Unique(HashSet<string> taken, string rel)
        {
            if (taken.Add(rel)) return rel;

            string dir = "", name = rel;
            int slash = rel.LastIndexOf('/');
            if (slash >= 0) { dir = rel.Substring(0, slash + 1); name = rel.Substring(slash + 1); }

            string stem = name, ext = "";
            int dot = name.LastIndexOf('.');
            if (dot > 0) { stem = name.Substring(0, dot); ext = name.Substring(dot); }

            for (int n = 2; n < 100000; n++)
            {
                string candidate = dir + stem + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext;
                if (taken.Add(candidate)) return candidate;
            }
            string last = dir + stem + " " + Guid.NewGuid().ToString("N") + ext;
            taken.Add(last);
            return last;
        }

        static long FreeSpace(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }
    }
}
