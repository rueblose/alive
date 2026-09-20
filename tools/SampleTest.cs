using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// A test bench for the sample model. Not part of the distribution — it checks the thing
    /// rather than being it.
    ///
    ///     SampleTest.exe scan &lt;root or .als&gt;   a breakdown by category, with the sums cross-checked
    ///     SampleTest.exe transport             the play/pause button against a file still opening
    ///
    /// Build: tools\build-sample-test.cmd
    /// </summary>
    internal static class SampleTest
    {
        static int _checks, _failed;

        static void Check(bool ok, string what)
        {
            _checks++;
            if (ok) return;
            _failed++;
            Console.WriteLine("FAIL: " + what);
        }

        [STAThread]
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string arg = args.Length > 1 ? args[1] : "";

            if (cmd == "scan") Scan(arg);
            else if (cmd == "patch") Patch(arg);
            else if (cmd == "collect") Collect(arg);
            else if (cmd == "show") Show(arg);
            else if (cmd == "transport") TransportProbe();
            else
            {
                Console.WriteLine("usage: SampleTest.exe scan <folder or .als>");
                Console.WriteLine("       SampleTest.exe patch <set.als>");
                Console.WriteLine("       SampleTest.exe collect <set.als>");
                Console.WriteLine("       SampleTest.exe show <set.als>");
                Console.WriteLine("       SampleTest.exe transport          needs no set - it makes its own render");
                return 2;
            }

            Console.WriteLine();
            if (_failed > 0)
            {
                Console.WriteLine(string.Format("{0} of {1} checks FAILED", _failed, _checks));
                return 1;
            }
            Console.WriteLine(string.Format("OK: {0} checks passed", _checks));
            return 0;
        }

        static List<string> SetsUnder(string path)
        {
            List<string> list = new List<string>();
            if (File.Exists(path)) { list.Add(path); return list; }
            if (!Directory.Exists(path)) return list;
            foreach (string f in Directory.GetFiles(path, "*.als", SearchOption.AllDirectories))
                if (f.IndexOf(@"\Backup\", StringComparison.OrdinalIgnoreCase) < 0)
                    list.Add(f);
            return list;
        }

        static void Scan(string path)
        {
            LiveEnvironment env = LiveEnvironment.Detect();
            List<string> sets = SetsUnder(path);
            Check(sets.Count > 0, "no .als found under " + path);

            int[] byOrigin = new int[6];
            long[] bytesByOrigin = new long[6];
            int devices = 0, totalRefs = 0, totalDeps = 0, dirDeps = 0;

            foreach (string file in sets)
            {
                AlsInfo info = AlsFile.Read(file);
                if (info.Error != null) continue;

                List<SampleDep> deps = SampleScan.Of(info, Path.GetDirectoryName(file), env);

                // Every reference is accounted for exactly once: the sum of RefIndexes over all
                // dependencies equals the number of FileRefs selected, and the numbers do not
                // repeat.
                HashSet<int> seen = new HashSet<int>();
                int refsHere = 0;
                foreach (SampleDep d in deps)
                {
                    Check(d.RefIndexes.Count > 0, "dep without RefIndexes in " + file);
                    foreach (int i in d.RefIndexes)
                    {
                        Check(seen.Add(i), "FileRef " + i + " counted twice in " + file);
                        Check(i >= 0 && i < info.Files.Count, "RefIndex out of range in " + file);
                        refsHere++;
                    }
                    byOrigin[(int)d.Origin]++;
                    bytesByOrigin[(int)d.Origin] += d.Size;
                    if (d.IsDevice) devices++;

                    Check(d.Origin != SampleOrigin.Missing || d.Size == 0,
                          "missing dep has a size in " + file);
                    Check(d.Origin != SampleOrigin.FactoryPack || d.PackName.Length > 0,
                          "FactoryPack dep without a pack name in " + file);

                    // A dependency found as a folder (.adg/.amxd are sometimes folders) has to
                    // have a non-zero size — otherwise it is indistinguishable from "not
                    // found".
                    if (d.Origin != SampleOrigin.Missing && Directory.Exists(d.Path))
                    {
                        dirDeps++;
                        Check(d.Size > 0, "folder dep has zero size in " + file + ": " + d.Path);
                    }
                }

                int expected = 0;
                foreach (FileRefInfo fr in info.Files)
                {
                    bool device = string.Equals(fr.Container, "MxPatchRef", StringComparison.Ordinal);
                    if (!fr.IsSampleDependency && !device) continue;
                    if (fr.RelativePath.Length == 0 && fr.AbsolutePath.Length == 0) continue;
                    expected++;
                }
                Check(refsHere == expected,
                      string.Format("{0}: covered {1} refs, expected {2}", Path.GetFileName(file), refsHere, expected));

                totalRefs += refsHere;
                totalDeps += deps.Count;
            }

            Console.WriteLine(string.Format("sets={0}  refs={1}  distinct files={2}  devices={3}",
                                            sets.Count, totalRefs, totalDeps, devices));
            Console.WriteLine(string.Format("folder deps (size checked): {0}", dirDeps));
            Console.WriteLine();
            string[] names = { "InProject", "OtherProject", "UserLibrary", "FactoryPack", "Elsewhere", "Missing" };
            for (int i = 0; i < names.Length; i++)
                Console.WriteLine(string.Format("{0,-14} {1,6} files  {2,10:N1} MB",
                                                names[i], byOrigin[i], bytesByOrigin[i] / 1048576.0));
        }

        /// <summary>
        /// A round trip: rewrite the paths of some of the references, read the result back with
        /// the same AlsFile and make sure that exactly what was ordered changed and exactly to
        /// what was ordered, while everything else stayed as it was. That is the very invariant
        /// the patcher addresses nodes by number rather than by content for.
        /// </summary>
        static void Patch(string file)
        {
            if (!File.Exists(file)) { Check(false, "no such set: " + file); return; }

            LiveEnvironment env = LiveEnvironment.Detect();
            AlsInfo before = AlsFile.Read(file);
            Check(before.Error == null, "cannot read " + file);
            if (before.Error != null) return;

            List<SampleDep> deps = SampleScan.Of(before, Path.GetDirectoryName(file), env);
            Check(deps.Count > 0, "no sample dependencies in " + file);
            if (deps.Count == 0) return;

            // We take every second dependency — that checks both that what was touched changed
            // and that what was untouched beside it survived. The "Drum & Bass" in every probe
            // path is not decoration: Escape() escapes "&" in XML, and before this probe not a
            // single run of the bench had touched the "&" character at all (the paths had no
            // special characters), although that is precisely one of the two cases the review
            // named as quietly breaking the file.
            Dictionary<int, NewRef> rewrites = new Dictionary<int, NewRef>();
            HashSet<int> touched = new HashSet<int>();
            int n = 0;
            foreach (SampleDep d in deps)
            {
                if ((n++ % 2) != 0) continue;
                NewRef nr = new NewRef();
                nr.RelativePath = "Samples/Imported/Drum & Bass " + n + ".wav";
                nr.AbsolutePath = "C:/probe/Drum & Bass/probe " + n + ".wav";
                nr.RelativePathType = 3;
                foreach (int i in d.RefIndexes) { rewrites[i] = nr; touched.Add(i); }
            }

            string dst = Path.Combine(Path.GetTempPath(), "alive-patch-probe.als");
            int patched = AlsSamplePatch.Rewrite(file, dst, rewrites, before.Files.Count);
            Check(patched == rewrites.Count,
                  string.Format("rewrote {0} nodes, asked for {1}", patched, rewrites.Count));

            AlsInfo after = AlsFile.Read(dst);
            Check(after.Error == null, "patched copy does not parse");
            if (after.Error != null) return;

            Check(after.Files.Count == before.Files.Count, "FileRef count changed");
            Check(after.Plugins.Count == before.Plugins.Count, "plugin count changed");
            Check(after.Tempo == before.Tempo, "tempo changed");
            Check(after.TotalTracks == before.TotalTracks, "track count changed");

            for (int i = 0; i < before.Files.Count && i < after.Files.Count; i++)
            {
                FileRefInfo a = before.Files[i], b = after.Files[i];
                if (touched.Contains(i))
                {
                    NewRef nr = rewrites[i];
                    Check(b.RelativePath == nr.RelativePath, "RelativePath not applied at " + i);
                    Check(b.AbsolutePath == nr.AbsolutePath, "Path not applied at " + i);
                    Check(b.RelativePathType == nr.RelativePathType, "RelativePathType not applied at " + i);
                    Check(b.LivePackName.Length == 0, "LivePackName not cleared at " + i);
                    Check(b.OriginalFileSize == a.OriginalFileSize, "OriginalFileSize touched at " + i);
                }
                else
                {
                    Check(b.RelativePath == a.RelativePath, "untouched RelativePath changed at " + i);
                    Check(b.AbsolutePath == a.AbsolutePath, "untouched Path changed at " + i);
                    Check(b.RelativePathType == a.RelativePathType, "untouched type changed at " + i);
                    Check(b.LivePackName == a.LivePackName, "untouched LivePackName changed at " + i);
                }
            }

            // AlsFile does not parse the LivePackId field — it simply is not in the model —
            // while a zeroed LivePackId is exactly what detaches a collected copy from a pack:
            // RefResolver goes by the pack name while Live itself goes by the identifier. We
            // check against the raw text, independently of the model, by the same node-walking
            // rule the patcher uses.
            int packChecked = CheckPackCleared(dst, rewrites);
            Check(packChecked == rewrites.Count, "pack-clear check did not cover all touched nodes");

            // A wrong expected node count has to kill the result rather than write it.
            string bad = Path.Combine(Path.GetTempPath(), "alive-patch-bad.als");
            bool threw = false;
            try { AlsSamplePatch.Rewrite(file, bad, rewrites, before.Files.Count + 1); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "count mismatch did not throw");
            Check(!File.Exists(bad), "failed patch left a file behind");

            try { File.Delete(dst); } catch { }
            Console.WriteLine(string.Format("patched {0} of {1} FileRef in {2}",
                                            patched, before.Files.Count, Path.GetFileName(file)));
            Console.WriteLine(string.Format("pack cleared (raw): {0} touched FileRef checked", packChecked));
        }

        /// <summary>
        /// Collects a set into a temporary folder and checks the main promise of collecting:
        /// every reference of the collected copy resolves to an existing file INSIDE that
        /// folder. That is what the collecting is done for, and it has to be checked with the
        /// same RefResolver the catalog will use afterwards.
        /// </summary>
        static void Collect(string file)
        {
            // It does not depend on file — CRITICAL 1 of the final review reproduces without a
            // single real file on disk (see the comment on CollisionProbe).
            CollisionProbe();
            SameSetProbe();

            if (!File.Exists(file)) { Check(false, "no such set: " + file); return; }

            LiveEnvironment env = LiveEnvironment.Detect();
            AlsInfo info = AlsFile.Read(file);
            Check(info.Error == null, "cannot read " + file);
            if (info.Error != null) return;

            // The original has to remain exactly the same file: Run opens src read-only
            // (FileShare.ReadWrite rather than exclusively), and that is the main promise of
            // this whole branch — "the original is never touched". The snapshot is taken here,
            // before anything, and compared at the very end — after every Run() below,
            // including the cancelled attempts.
            FileInfo srcBefore = new FileInfo(file);
            long srcLenBefore = srcBefore.Length;
            DateTime srcWriteBefore = srcBefore.LastWriteTimeUtc;

            SetEntry set = new SetEntry();
            set.Path = file;
            set.Name = Path.GetFileNameWithoutExtension(file);

            List<SampleDep> deps = SampleScan.Of(info, Path.GetDirectoryName(file), env);
            Check(deps.Count > 0, "no sample dependencies in " + file);
            if (deps.Count == 0) return;

            CollectOptions opt = new CollectOptions();
            opt.FromFactoryPacks = true;    // in the bench we collect everything, to exercise every branch

            CollectPlan plan = CollectAll.Plan(set, deps, opt);

            // The plan has to sort every dependency into exactly one basket.
            int total = plan.Copy.Count + plan.Skipped.Count + plan.NotFound.Count;
            Check(total == deps.Count,
                  string.Format("plan covers {0} deps of {1}", total, deps.Count));
            Check(plan.Skipped.Count == 0, "nothing should be skipped when all options are on");

            // Different files must not land in the same destination.
            HashSet<string> dests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SampleDep d in plan.Copy)
            {
                string rel;
                Check(plan.Dest.TryGetValue(d, out rel), "no destination for " + d.Name);
                if (rel != null) Check(dests.Add(rel), "two files land on " + rel);
            }

            // In-project references are not rewritten: their path in the copy is right as it
            // is.
            foreach (SampleDep d in plan.Copy)
                if (d.Origin == SampleOrigin.InProject)
                    foreach (int i in d.RefIndexes)
                        Check(!plan.Rewrites.ContainsKey(i), "in-project ref rewritten at " + i);

            // The export lands next to the project, with no intermediate folder. We check
            // before the TargetDir substitution below: beyond that the bench takes the
            // collecting off into %TEMP%.
            Check(plan.TargetDir == Path.Combine(set.ProjectDir, set.Name + " Project_export"),
                  "unexpected export folder: " + plan.TargetDir);

            string temp = Path.Combine(Path.GetTempPath(), "alive-collect-probe");
            Nuke(temp);
            Directory.CreateDirectory(temp);
            plan.TargetDir = Path.Combine(temp, set.Name + " Project");

            CollectAll.Run(plan, set, info, null, System.Threading.CancellationToken.None);

            // done == plan.Copy.Count was a tautology: done grows in Run() unconditionally,
            // including on every File.Copy that failed — it could not have failed even if not a
            // single file had been copied. plan.Failed is the very place Run() fills in
            // precisely when a copy is refused.
            Check(plan.Failed.Count == 0,
                  string.Format("{0} of {1} files failed to copy: {2}", plan.Failed.Count, plan.Copy.Count,
                                string.Join(", ", plan.Failed.ToArray())));

            string copied = Path.Combine(plan.TargetDir, set.Name + ".als");
            Check(File.Exists(copied), "collected .als is missing");
            if (!File.Exists(copied)) return;

            AlsInfo after = AlsFile.Read(copied);
            Check(after.Error == null, "collected .als does not parse");
            if (after.Error != null) return;

            List<SampleDep> afterDeps = SampleScan.Of(after, Path.GetDirectoryName(copied), env);
            int outside = 0, missing = 0;
            foreach (SampleDep d in afterDeps)
            {
                if (d.Origin == SampleOrigin.Missing) { missing++; continue; }
                if (d.Origin != SampleOrigin.InProject) outside++;
            }
            Check(outside == 0, string.Format("{0} refs still point outside the collected folder", outside));
            Check(missing == plan.NotFound.Count,
                  string.Format("{0} missing after collect, {1} were missing before", missing, plan.NotFound.Count));

            Console.WriteLine(string.Format("collected {0} files, {1:N1} MB -> {2}",
                                            plan.Copy.Count, plan.TotalBytes / 1048576.0, plan.TargetDir));

            // On being cut short, Run() has to delete only the folder it created itself:
            // TargetDir from two Plan()s over one set may coincide, and wiping somebody else's
            // already-collected copy is data loss. We check both outcomes with a separate Run
            // on a pre-cancelled token: cancel.ThrowIfCancellationRequested() in the copy loop
            // throws on the very first iteration.
            System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            // A fresh folder — Run creates it itself, and on being cut short it has to
            // disappear.
            CollectPlan freshPlan = CollectAll.Plan(set, deps, opt);
            freshPlan.TargetDir = Path.Combine(temp, "cancel-fresh Project");
            bool threwFresh = false;
            try { CollectAll.Run(freshPlan, set, info, null, cts.Token); }
            catch (OperationCanceledException) { threwFresh = true; }
            Check(threwFresh, "cancelled Run did not throw on a fresh target dir");
            Check(!Directory.Exists(freshPlan.TargetDir), "cancelled Run left behind a folder it created itself");

            // An already-existing folder with somebody else's file in it — Run did not create
            // it, and on being cut short it has to stay untouched along with its contents.
            CollectPlan existingPlan = CollectAll.Plan(set, deps, opt);
            existingPlan.TargetDir = Path.Combine(temp, "cancel-existing Project");
            Directory.CreateDirectory(existingPlan.TargetDir);
            string marker = Path.Combine(existingPlan.TargetDir, "someone else's file.txt");
            File.WriteAllText(marker, "not ours", new UTF8Encoding(false));
            bool threwExisting = false;
            try { CollectAll.Run(existingPlan, set, info, null, cts.Token); }
            catch (OperationCanceledException) { threwExisting = true; }
            Check(threwExisting, "cancelled Run did not throw on a pre-existing target dir");
            Check(Directory.Exists(existingPlan.TargetDir), "cancelled Run deleted a folder it did not create");
            Check(File.Exists(marker), "cancelled Run deleted someone else's file from a pre-existing folder");

            // .zip mode: the same again, but with an archive on the way out and no staging
            // folder left behind.
            CollectOptions zipOpt = new CollectOptions();
            zipOpt.FromFactoryPacks = true;
            zipOpt.ToZip = true;

            CollectPlan zipPlan = CollectAll.Plan(set, deps, zipOpt);
            zipPlan.TargetDir = Path.Combine(temp, "zipped Project");

            // There must be no staging folder not only at the end but at any moment of the
            // collecting: the archive is written as a stream straight from the sources. The
            // check belongs exactly here, on the progress callback — the one place the middle
            // of Run() is visible from. A check after the fact (below) would have passed on the
            // old trick of "copy, pack, delete" too, and that is bad precisely because it puts
            // a second copy of the set on disk and then erases it.
            bool staged = false;
            Action<int, int, string> watch = delegate
            {
                if (Directory.Exists(zipPlan.TargetDir)) staged = true;
            };

            CollectAll.Run(zipPlan, set, info, watch, System.Threading.CancellationToken.None);

            Check(zipPlan.Zip, "ToZip did not reach the plan");
            Check(!staged, "zip run created a staging folder on disk");
            Check(File.Exists(zipPlan.ZipPath), "zip run produced no archive");
            Check(!Directory.Exists(zipPlan.TargetDir), "zip run left its staging folder behind");
            if (File.Exists(zipPlan.ZipPath))
                using (ZipArchive za = ZipFile.OpenRead(zipPlan.ZipPath))
                {
                    Check(za.GetEntry(set.Name + ".als") != null, "collected .als is missing from the archive");
                    // Greater or equal: bundles (.adg, .amxd) are folders, and each unfolds
                    // into several entries in the archive.
                    Check(za.Entries.Count >= zipPlan.Copy.Count + 1,
                          string.Format("archive holds {0} entries for {1} copied files",
                                        za.Entries.Count, zipPlan.Copy.Count));
                }

            // An aborted .zip must not be left on disk — from outside it looks like a finished
            // export with half a set inside.
            CollectPlan zipCancel = CollectAll.Plan(set, deps, zipOpt);
            zipCancel.TargetDir = Path.Combine(temp, "cancel-zip Project");
            bool threwZip = false;
            try { CollectAll.Run(zipCancel, set, info, null, cts.Token); }
            catch (OperationCanceledException) { threwZip = true; }
            Check(threwZip, "cancelled zip Run did not throw");
            Check(!File.Exists(zipCancel.ZipPath), "cancelled zip Run left a half-written archive");
            Check(!Directory.Exists(zipCancel.TargetDir), "cancelled zip Run left behind a folder it created itself");

            // That very snapshot from the start of the function — after every Run() above, the
            // successful one and the two cancelled. The slightest discrepancy here means that
            // somewhere in Run src was opened for writing rather than only for reading.
            FileInfo srcAfter = new FileInfo(file);
            Check(srcAfter.Length == srcLenBefore, "original .als size changed after collect");
            Check(srcAfter.LastWriteTimeUtc == srcWriteBefore, "original .als write time changed after collect");

            Nuke(temp);
        }

        /// <summary>
        /// Wipe the bench folder. Copies inherit "read only" from their sources (on samples
        /// from packs the attribute is everywhere), and Delete will not take such a file — so
        /// the litter of the previous run breaks the next one: File.Copy over a read-only file
        /// fails with an access denial.
        /// </summary>
        static void Nuke(string dir)
        {
            if (!Directory.Exists(dir)) return;
            try
            {
                foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
                Directory.Delete(dir, true);
            }
            catch { }
        }

        /// <summary>
        /// CRITICAL 1 (the final review): an external dependency whose file name coincides with
        /// an in-project one must not get the same destination. Plan() used to hand out names
        /// to external ones through Unique() in the same pass where InProject merely marked its
        /// place as taken, without checking the result of HashSet.Add. Let an external
        /// dependency come in deps BEFORE an in-project one with the same file name (and the
        /// order in deps is the order of FileRefs in the document, chosen by the set's author,
        /// not by us) — and Unique() managed to give it the in-project one's future path before
        /// that one staked it out, and both got one Dest: Run() copied them into one and the
        /// same file on disk, while the external one's rewritten reference already led there
        /// too.
        ///
        /// A real .als is not needed: Plan() has no side effects depending on whether the
        /// source files exist on disk — only SetEntry.ProjectDir and
        /// DriveInfo.AvailableFreeSpace, and neither throws on "no such folder".
        /// </summary>
        /// <summary>
        /// A set's identity is its path, not the object. A scan republishes the catalog with new
        /// SetEntry objects even for files that have not changed, and everything to do with
        /// playback compares tags across exactly that moment: the pulse on the playing row, the
        /// play/pause toggle, the position in the playlist. While those comparisons were
        /// ReferenceEquals, a folder-watcher refresh or F5 left the sound playing and the row
        /// without its animation.
        ///
        /// No files needed: SetEntry.SameSet looks at nothing but the paths.
        /// </summary>
        static void SameSetProbe()
        {
            SetEntry a = new SetEntry();
            a.Path = @"E:\Music\Projects\demo Project\demo.als";

            // The very case the bug was: another object, same file.
            SetEntry rescanned = new SetEntry();
            rescanned.Path = a.Path;
            Check(SetEntry.SameSet(a, rescanned), "a rescanned copy of the same set is not recognised");

            // Windows paths are case-insensitive, and the catalog compares them that way
            // everywhere else (see Settings.HasRoot).
            SetEntry cased = new SetEntry();
            cased.Path = a.Path.ToUpperInvariant();
            Check(SetEntry.SameSet(a, cased), "the same path in another case is not recognised");

            SetEntry other = new SetEntry();
            other.Path = @"E:\Music\Projects\demo Project\demo v2.als";
            Check(!SetEntry.SameSet(a, other), "two different sets counted as one");

            // Two "nothings" are not the same set: otherwise, with nothing playing, every row
            // whose tag is null would light up at once.
            Check(!SetEntry.SameSet(null, null), "two nulls counted as the same set");
            Check(!SetEntry.SameSet(a, null), "a set and null counted as the same");

            // Tags that are not sets — the player's own list of render files — stay on
            // reference: those objects live as long as the window does.
            object f = new object();
            Check(SetEntry.SameSet(f, f), "a non-set tag is not equal to itself");
            Check(!SetEntry.SameSet(f, new object()), "two different non-set tags counted as one");
            Check(!SetEntry.SameSet(a, f), "a set and a foreign tag counted as the same");
        }

        static void CollisionProbe()
        {
            SetEntry set = new SetEntry();
            set.Name = "demo";
            set.Path = Path.Combine(Path.GetTempPath(), @"alive-collision-probe\demo Project\demo.als");
            string setDir = Path.GetDirectoryName(set.Path);

            SampleDep inProject = new SampleDep();
            inProject.Origin = SampleOrigin.InProject;
            inProject.Resolved = new ResolvedRef();
            inProject.Resolved.Status = RefStatus.Found;
            inProject.Resolved.ResolvedPath = Path.Combine(setDir, "Samples", "Imported", "kick.wav");
            inProject.RefIndexes.Add(0);
            inProject.Size = 100;

            SampleDep elsewhere = new SampleDep();
            elsewhere.Origin = SampleOrigin.Elsewhere;
            elsewhere.Resolved = new ResolvedRef();
            elsewhere.Resolved.Status = RefStatus.Found;
            elsewhere.Resolved.ResolvedPath = @"C:\Downloads\kick.wav";
            elsewhere.RefIndexes.Add(1);
            elsewhere.Size = 200;

            Check(inProject.Name == elsewhere.Name, "collision probe: setup is broken, file names differ");

            // The order reproduces the review's finding: the external dependency comes in deps
            // before the in-project one with the same file name.
            List<SampleDep> deps = new List<SampleDep>();
            deps.Add(elsewhere);
            deps.Add(inProject);

            CollectPlan plan = CollectAll.Plan(set, deps, new CollectOptions());

            string relIn, relElsewhere;
            bool haveIn = plan.Dest.TryGetValue(inProject, out relIn);
            bool haveElsewhere = plan.Dest.TryGetValue(elsewhere, out relElsewhere);
            Check(haveIn, "collision probe: no destination for the in-project dependency");
            Check(haveElsewhere, "collision probe: no destination for the elsewhere dependency");
            if (haveIn && haveElsewhere)
                Check(!string.Equals(relIn, relElsewhere, StringComparison.OrdinalIgnoreCase),
                      "collision probe: an elsewhere dependency landed on the same destination as an " +
                      "in-project dependency with the same file name (" + relIn + ")");
        }

        /// <summary>
        /// The transport button while the file is still opening.
        ///
        /// Opening runs on its own thread and takes as long as the system decoder needs — a
        /// noticeable fraction of a second on a big master, and every auto-advance of the
        /// preview goes through that window. The button used to ask the device rather than the
        /// transport, and while the device was not ready the only thing it could do was Play():
        /// a pause pressed there was swallowed, the icon went on showing "not playing" — and a
        /// moment later the track started, for no reason the person could see.
        ///
        /// The window is never shown: with the player collapsed to the footer strip is exactly
        /// the case this went wrong in.
        /// </summary>
        static void TransportProbe()
        {
            string root = Path.Combine(Path.GetTempPath(), "alive-transport-probe");
            Nuke(root);
            string dir = Path.Combine(root, "demo Project");
            Directory.CreateDirectory(dir);
            WriteSilence(Path.Combine(dir, "demo master.wav"), 1.2);

            SetEntry set = new SetEntry();
            set.Name = "demo";
            set.Path = Path.Combine(dir, "demo.als");
            File.WriteAllText(set.Path, "");   // only the folder matters to RenderScan

            try
            {
                using (PlayerDialog p = new PlayerDialog())
                {
                    IntPtr unused = p.Handle;   // no window, but the timers need a handle
                    p.LoadSet(set);

                    if (!p.Audio.IsOpening)
                    {
                        Console.WriteLine("transport probe skipped: the file opened before the probe could press");
                        return;
                    }

                    // The press lands while the device is still being made ready.
                    p.PlayPause();

                    if (!Settle(delegate { return !p.Audio.IsOpening; }, 8000))
                    {
                        Console.WriteLine("transport probe skipped: the file never opened (no sound device?)");
                        return;
                    }

                    Pump(300);          // the output thread gets its chance to start
                    Check(!p.IsPlaying,
                          "the track started although pause was pressed while the file was opening");

                    // And the same button has to start it again: a pause that cannot be lifted
                    // is not a pause.
                    p.PlayPause();
                    Check(Settle(delegate { return p.IsPlaying; }, 3000),
                          "play after a pause during the open did not start the track");

                    // Played to the end with nowhere to go — one render, one set in the
                    // playlist. What is left is the pair the auto-advance reads: finished, and
                    // standing on pause. Losing either is how a paused track starts the next
                    // one by itself.
                    Check(Settle(delegate { return p.Audio.Finished; }, 8000),
                          "the track never reported that it had finished");
                    Pump(300);
                    Check(!p.IsPlaying, "the track was still playing after its own end");
                }

                // The other side of the same guard: a track that ran out while nobody had
                // paused it still has to pull the next set in. This is what the preview does
                // all day with the window collapsed to the footer.
                string dirB = Path.Combine(root, "next Project");
                Directory.CreateDirectory(dirB);
                WriteSilence(Path.Combine(dirB, "next master.wav"), 1.2);

                SetEntry next = new SetEntry();
                next.Name = "next";
                next.Path = Path.Combine(dirB, "next.als");
                next.HasRenders = true;
                File.WriteAllText(next.Path, "");
                set.HasRenders = true;

                using (PlayerDialog p = new PlayerDialog())
                {
                    IntPtr unused = p.Handle;
                    List<SetEntry> playlist = new List<SetEntry>();
                    playlist.Add(set);
                    playlist.Add(next);
                    p.LoadSet(set, playlist, 0);

                    Check(Settle(delegate { return SetEntry.SameSet(p.CurrentSet, next); }, 12000),
                          "a track that played to its end did not move on to the next set");
                    Check(Settle(delegate { return p.IsPlaying; }, 3000),
                          "the next set was loaded but never started playing");
                }
            }
            finally { Nuke(root); }
        }

        /// <summary>
        /// Waits for a condition while pumping messages: the player lives on timers, and
        /// without a message loop not one of them ticks. False on running out of time.
        /// </summary>
        static bool Settle(Func<bool> done, int ms)
        {
            for (int waited = 0; waited < ms; waited += 20)
            {
                System.Windows.Forms.Application.DoEvents();
                if (done()) return true;
                System.Threading.Thread.Sleep(20);
            }
            System.Windows.Forms.Application.DoEvents();
            return done();
        }

        static void Pump(int ms) { Settle(delegate { return false; }, ms); }

        /// <summary>
        /// Plain PCM silence — 44.1 kHz, mono, 16-bit. No codec is involved, so Media
        /// Foundation opens it on any machine, and nothing here listens anyway.
        /// </summary>
        static void WriteSilence(string path, double seconds)
        {
            const int rate = 44100, channels = 1, bits = 16;
            int dataBytes = (int)(rate * seconds) * channels * bits / 8;

            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(Encoding.ASCII.GetBytes("RIFF"));
                w.Write(36 + dataBytes);
                w.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                w.Write(16);                                  // the size of this chunk
                w.Write((short)1);                            // PCM, uncompressed
                w.Write((short)channels);
                w.Write(rate);
                w.Write(rate * channels * bits / 8);          // bytes per second
                w.Write((short)(channels * bits / 8));        // block align
                w.Write((short)bits);
                w.Write(Encoding.ASCII.GetBytes("data"));
                w.Write(dataBytes);
                w.Write(new byte[dataBytes]);
            }
        }

        /// <summary>
        /// Opens the collecting window and does nothing else — so it can be captured with
        /// Shot.exe without sitting at the machine. A built exe that "compiles without errors"
        /// still says nothing about what got drawn.
        /// </summary>
        static void Show(string file)
        {
            if (!File.Exists(file)) { Check(false, "no such set: " + file); return; }

            SetEntry set = new SetEntry();
            set.Path = file;
            set.Name = Path.GetFileNameWithoutExtension(file);

            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            using (CollectDialog d = new CollectDialog(set, LiveEnvironment.Detect(), Settings.Load()))
                System.Windows.Forms.Application.Run(d);
        }

        /// <summary>
        /// A raw check, independent of AlsFile, that LivePackId is zeroed on the nodes that
        /// were touched — the very field that is not in the FileRefInfo model. It decompresses
        /// dst itself (GZipStream over FileStream, as AlsSamplePatch.Rewrite does) and finds
        /// &lt;FileRef&gt; by the same rule as the patcher: an opening tag, not self-closing,
        /// up to &lt;/FileRef&gt;. For every node whose number is in rewrites it asserts that
        /// LivePackId is empty; in the same pass over the text it also checks LivePackName — a
        /// cheap model-independent check of what is already checked through AlsFile above.
        /// About nodes outside rewrites it asserts nothing: the patcher does not touch them and
        /// their pack may be anything. Returns the number of nodes checked.
        /// </summary>
        static int CheckPackCleared(string dst, Dictionary<int, NewRef> rewrites)
        {
            int index = -1, checkedNodes = 0;
            using (FileStream fin = new FileStream(dst, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite, 64 * 1024))
            using (GZipStream gin = new GZipStream(fin, CompressionMode.Decompress))
            using (StreamReader rin = new StreamReader(gin, new UTF8Encoding(false), false, 64 * 1024))
            {
                AlsPatch.LineReader lines = new AlsPatch.LineReader(rin);
                StringBuilder node = null;

                string line;
                while ((line = lines.Next()) != null)
                {
                    if (node == null)
                    {
                        int at = line.IndexOf("<FileRef", StringComparison.Ordinal);
                        if (at < 0 || SelfClosing(line, at)) continue;
                        index++;
                        node = new StringBuilder(line);
                    }
                    else node.Append(line);

                    if (line.IndexOf("</FileRef>", StringComparison.Ordinal) < 0) continue;

                    string text = node.ToString();
                    node = null;
                    if (!rewrites.ContainsKey(index)) continue;

                    checkedNodes++;
                    Check(AttrEmpty(text, "<LivePackId Value=\""), "LivePackId not cleared (raw) at " + index);
                    Check(AttrEmpty(text, "<LivePackName Value=\""), "LivePackName not cleared (raw) at " + index);
                }
            }
            return checkedNodes;
        }

        /// <summary>The tag was found, and right after the value's opening quote comes the
        /// closing one — the value is empty.</summary>
        static bool AttrEmpty(string node, string tag)
        {
            int at = node.IndexOf(tag, StringComparison.Ordinal);
            return at >= 0 && at + tag.Length < node.Length && node[at + tag.Length] == '"';
        }

        /// <summary>
        /// Whether a "/" stands before the tag's closing bracket — that is, the node is empty.
        /// A copy of the same rule from AlsSamplePatch: there it is private to the class and
        /// shares no common assembly.
        /// </summary>
        static bool SelfClosing(string line, int at)
        {
            int close = line.IndexOf('>', at);
            return close > at && line[close - 1] == '/';
        }
    }
}
