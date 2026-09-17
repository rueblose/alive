using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// A console check of the rescue helper: parsing Live's log and patching a copy of a set.
    /// Both pieces work with other people's files that cannot be faked convincingly, so they
    /// are checked against real ones — against this machine's log and a real .als.
    ///
    /// Build: tools\build-rescue-test.cmd. Does not go into bin — this is a checking tool, not
    /// part of the program.
    ///
    ///     RescueTest.exe logs                 every aborted load in every Live log
    ///     RescueTest.exe log   &lt;set.als&gt;      what the log remembers about this set
    ///     RescueTest.exe patch &lt;set.als&gt;      disable every plugin in a copy and diff the XML
    /// </summary>
    internal static class RescueTest
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage: RescueTest.exe logs | log <set.als> | patch <set.als>");
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "logs": return Logs();
                    case "log": return One(args[1]);
                    case "patch": return Patch(args[1]);
                    case "simulate": return Simulate(args[1]);
                    case "probe": return Probe(args[1]);
                    default:
                        Console.WriteLine("unknown command: " + args[0]);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex);
                return 1;
            }
        }

        // ------------------------------------------------------------------ logs

        static int Logs()
        {
            List<LiveLogFile> files = LiveLog.Files();
            Console.WriteLine("log files: " + files.Count);

            int total = 0, broke = 0, loaded = 0;
            int seen = 0, ok = 0, refused = 0, hungCount = 0;
            foreach (LiveLogFile f in files)
            {
                List<LoadAttempt> all = f.All();
                int fSeen = 0, fOk = 0, fBad = 0, fHung = 0;
                foreach (LoadAttempt a in all)
                    foreach (PluginLoad p in a.Plugins)
                    {
                        fSeen++;
                        if (p.Restored) fOk++;
                        else if (p.Failed) fBad++;
                        else fHung++;
                    }
                seen += fSeen; ok += fOk; refused += fBad; hungCount += fHung;

                Console.WriteLine();
                Console.WriteLine(f.Version + "  (" + all.Count + " attempts, "
                                  + (f.Length / 1024) + " KB)   plugins: " + fSeen
                                  + " seen, " + fOk + " restored, " + fBad + " refused, " + fHung + " hung");

                foreach (LoadAttempt a in all)
                {
                    total++;
                    if (a.Result == LoadResult.Loaded) { loaded++; continue; }
                    broke++;

                    PluginLoad hung = a.Hung;
                    Console.WriteLine("  " + a.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + "  " + Short(a.Document));
                    Console.WriteLine("      restored " + a.RestoredCount + "/" + a.Plugins.Count
                        + (hung != null ? "   STOPS IN: " + hung.Format + " " + hung.Name : "   (no plugin pending)"));
                    foreach (PluginLoad p in a.Failures)
                        Console.WriteLine("      refused:  " + p.Format + " " + p.Name);
                }
            }

            Console.WriteLine();
            Console.WriteLine("total " + total + " attempts: " + loaded + " loaded, " + broke + " broke");
            Console.WriteLine("total " + seen + " plugin loads: " + ok + " restored, "
                              + refused + " refused, " + hungCount + " hung");
            return 0;
        }

        static int One(string als)
        {
            Console.WriteLine("set: " + als);
            LoadAttempt a = LiveLog.LastAttempt(als);
            if (a == null) { Console.WriteLine("no attempt found in any Live log"); return 0; }

            Console.WriteLine("live:     " + a.LiveVersion);
            Console.WriteLine("started:  " + a.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Console.WriteLine("result:   " + a.Result);
            Console.WriteLine("saved by: " + a.CreatedBy);
            Console.WriteLine("plugins:  " + a.RestoredCount + " restored of " + a.Plugins.Count);
            foreach (PluginLoad p in a.Plugins)
                Console.WriteLine("   " + (p.Restored ? "ok    " : p.Failed ? "FAILED" : "HUNG  ")
                                  + " " + p.Format + " " + p.Name);
            return 0;
        }

        // ------------------------------------------------------------------ patching

        static int Patch(string als)
        {
            AlsInfo info = AlsFile.Read(als);
            if (!string.IsNullOrEmpty(info.Error)) { Console.WriteLine("read failed: " + info.Error); return 1; }

            List<AlsPluginSlot> targets = AlsPatch.Targets(info);
            Console.WriteLine("set:     " + als);
            Console.WriteLine("plugins: " + targets.Count + " addressable, "
                              + AlsPatch.Unaddressable(info) + " without an id");
            foreach (AlsPluginSlot s in targets)
                Console.WriteLine("   " + s.Format + "  " + s.Uid + "  " + s.Label);
            if (targets.Count == 0) { Console.WriteLine("nothing to disable"); return 0; }

            List<string> uids = new List<string>();
            foreach (AlsPluginSlot s in targets) uids.Add(s.Uid);

            string dst = Path.Combine(Path.GetTempPath(), "alive-patch-check.als");
            PluginInventory inv = PluginInventory.Load();
            int patched = AlsPatch.Neutralize(als, dst, uids, inv);
            Console.WriteLine();
            Console.WriteLine("patched " + patched + " device node(s) -> " + dst);

            // The main check: NOTHING in the file may change except those very Values. We
            // compare the decompressed XML line by line, before and after.
            string[] before = Lines(als), after = Lines(dst);
            if (before.Length != after.Length)
            {
                Console.WriteLine("MISMATCH: line count " + before.Length + " -> " + after.Length);
                return 1;
            }

            int changed = 0, unexpected = 0;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;
                changed++;
                string tag = before[i].Trim();
                bool ok = tag.StartsWith("<Fields.0 ", StringComparison.Ordinal)
                       || tag.StartsWith("<UniqueId ", StringComparison.Ordinal)
                       || tag.StartsWith("<Path ", StringComparison.Ordinal);
                if (!ok) { unexpected++; Console.WriteLine("  UNEXPECTED: " + tag); }
                else if (changed <= 8)
                    Console.WriteLine("  " + tag + "\n           -> " + after[i].Trim());
            }
            Console.WriteLine("changed lines: " + changed + ", unexpected: " + unexpected);

            // And the reverse check: parsing the copy must see DIFFERENT identifiers on those
            // same plugins — that is, Live will no longer recognise them.
            AlsInfo patchedInfo = AlsFile.Read(dst);
            if (!string.IsNullOrEmpty(patchedInfo.Error))
            {
                Console.WriteLine("PATCHED FILE UNREADABLE: " + patchedInfo.Error);
                return 1;
            }

            HashSet<string> was = new HashSet<string>(uids, StringComparer.OrdinalIgnoreCase);
            int still = 0, known = 0;
            List<AlsPluginSlot> now = AlsPatch.Targets(patchedInfo);
            Console.WriteLine();
            Console.WriteLine("after patch: " + now.Count + " plugin(s), same names, new ids");
            foreach (AlsPluginSlot s in now)
            {
                if (was.Contains(s.Uid)) still++;
                if (inv.ByUid(s.Uid) != null) known++;
                Console.WriteLine("   " + s.Format + "  " + s.Uid + "  " + s.Label
                                  + (inv.ByUid(s.Uid) != null ? "   <-- STILL INSTALLED" : ""));
            }

            Console.WriteLine();
            Console.WriteLine("names kept:            " + (now.Count == targets.Count ? "yes" : "NO"));
            Console.WriteLine("old ids left in file:  " + still + " (must be 0)");
            Console.WriteLine("new ids Live knows:    " + known + " (must be 0)");

            bool pass = unexpected == 0 && still == 0 && known == 0 && now.Count == targets.Count;
            Console.WriteLine();
            Console.WriteLine(pass ? "PASS" : "FAIL");
            return pass ? 0 : 1;
        }

        // ------------------------------------------------------------------ convergence

        /// <summary>
        /// Runs the investigation in a loop, naming each plugin of the set guilty in turn. Not
        /// a single file and not a single launch of Live: the outcome of a probe is known in
        /// advance — the set opens if and only if the culprit is among the disabled.
        ///
        /// What gets checked is exactly what cannot be checked by eye: that the search always
        /// converges, names the right one, and never spins forever.
        /// </summary>
        static int Simulate(string als)
        {
            AlsInfo info = AlsFile.Read(als);
            if (!string.IsNullOrEmpty(info.Error)) { Console.WriteLine("read failed: " + info.Error); return 1; }

            SetEntry set = new SetEntry();
            set.Path = als;
            set.Name = Path.GetFileNameWithoutExtension(als);

            List<AlsPluginSlot> all = AlsPatch.Targets(info);
            Console.WriteLine("set:     " + Path.GetFileName(als));
            Console.WriteLine("plugins: " + all.Count);
            Console.WriteLine();

            int worst = 0, failures = 0;
            foreach (AlsPluginSlot guilty in all)
            {
                RescueSession s = new RescueSession(set, null);

                // We mute the hint from Live's log: it belongs to this set's real history,
                // while the culprit here is one we appoint.
                s.History = null;
                s.HistorySuspect = null;

                int probes = 0;
                while (!s.Finished && probes < 64)
                {
                    List<AlsPluginSlot> off = s.Suggest();
                    if (off.Count == 0) break;
                    probes++;
                    s.Apply(off, Has(off, guilty), null);
                }

                bool ok = s.Verdict == RescueVerdict.Culprit
                       && s.Culprit != null && s.Culprit.Uid == guilty.Uid;
                if (!ok) failures++;
                if (probes > worst) worst = probes;

                Console.WriteLine((ok ? "  ok   " : "  FAIL ") + probes + " probes  "
                                  + guilty.Name.PadRight(22) + " -> " + s.VerdictText());
            }

            // And a separate case: the culprit is not a plugin at all. The very first probe
            // disables everything and it still will not open — the investigation has to admit
            // that rather than keep searching.
            RescueSession n = new RescueSession(set, null);
            n.History = null; n.HistorySuspect = null;
            int rounds = 0;
            while (!n.Finished && rounds < 64)
            {
                List<AlsPluginSlot> off = n.Suggest();
                if (off.Count == 0) break;
                rounds++;
                n.Apply(off, false, null);
            }
            bool notPlugins = n.Verdict == RescueVerdict.NotPlugins;
            Console.WriteLine();
            Console.WriteLine((notPlugins ? "  ok   " : "  FAIL ") + rounds + " probes  "
                              + "not a plugin at all".PadRight(22) + " -> " + n.VerdictText());

            Console.WriteLine();
            Console.WriteLine("worst case: " + worst + " probes for " + all.Count + " plugins");
            Console.WriteLine(failures == 0 && notPlugins ? "PASS" : "FAIL (" + failures + " wrong verdicts)");
            return failures == 0 && notPlugins ? 0 : 1;
        }

        // ------------------------------------------------------- life of the probe file

        /// <summary>
        /// A probe appears next to the set, lands in the cleanup journal and disappears. This
        /// is worth checking separately: the probe .als sits in someone else's project folder,
        /// and everything that can go wrong here ends as litter among a person's projects.
        ///
        /// Run it on a COPY of a set — the file is placed next to whatever it is given.
        /// </summary>
        static int Probe(string als)
        {
            SetEntry set = new SetEntry();
            set.Path = als;
            set.Name = Path.GetFileNameWithoutExtension(als);

            RescueSession s = new RescueSession(set, PluginInventory.Load());
            if (s.Targets.Count == 0) { Console.WriteLine("no plugins to disable here"); return 1; }

            string expected = RescueProbe.PathFor(set);
            Console.WriteLine("probe path: " + expected);

            string made = s.Prepare(s.Suggest());
            bool onDisk = File.Exists(made);
            bool listed = Journal().Contains(made);
            bool named = RescueProbe.IsProbe(made) && made == expected;
            Console.WriteLine("  written:  " + onDisk + "   journalled: " + listed + "   named: " + named);

            // The catalog must not see it — or the probe would turn up in the sets list beside
            // the real one.
            bool hidden = true;
            FolderScan.Find(Path.GetDirectoryName(als), ".als", true,
                            delegate (string f) { if (f == made) hidden = false; }, null);
            Console.WriteLine("  invisible to the catalog: " + hidden);

            s.Cancel();
            bool gone = !File.Exists(made);
            bool cleared = !Journal().Contains(made);
            Console.WriteLine("  removed:  " + gone + "   journal cleared: " + cleared);

            // And cleanup after a crash: an entry with no file must be dropped from the journal
            // silently.
            RescueProbe.Remember(expected);
            RescueProbe.CleanupStale();
            bool swept = Journal().Count == 0;
            Console.WriteLine("  stale entry swept: " + swept);

            // This number is the reason the patching was rewritten as a stream: the
            // decompressed XML used to be held whole, and on a heavy set that is close to a
            // gigabyte.
            Console.WriteLine("  peak working set: "
                              + (Process.GetCurrentProcess().PeakWorkingSet64 / 1048576) + " MB");

            bool pass = onDisk && listed && named && hidden && gone && cleared && swept;
            Console.WriteLine();
            Console.WriteLine(pass ? "PASS" : "FAIL");
            return pass ? 0 : 1;
        }

        static List<string> Journal()
        {
            string p = Path.Combine(Settings.Dir, "probes.txt");
            List<string> all = new List<string>();
            if (!File.Exists(p)) return all;
            foreach (string line in File.ReadAllLines(p))
                if (line.Trim().Length > 0) all.Add(line.Trim());
            return all;
        }

        static bool Has(List<AlsPluginSlot> list, AlsPluginSlot s)
        {
            foreach (AlsPluginSlot x in list) if (x.Uid == s.Uid) return true;
            return false;
        }

        static string[] Lines(string als)
        {
            using (FileStream fs = new FileStream(als, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
            using (StreamReader r = new StreamReader(gz, new UTF8Encoding(false)))
                return r.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        }

        static string Short(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return path; }
        }
    }
}
