using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>How the whole investigation ended.</summary>
    public enum RescueVerdict
    {
        None,           // still searching
        NotPlugins,     // the set would not open even with everything disabled — it is not the plugins
        Culprit,        // exactly one is guilty, and it is in Culprit
        Group,          // several are guilty; the set whose disabling helps is known
        NoProbeYet      // there have been no probes yet
    }

    /// <summary>
    /// Probe copies of a set: the name, the journal and the cleanup.
    ///
    /// A probe is placed NEXT TO the original rather than in a temporary folder, and that is
    /// not laziness. Live measures relative sample references from the folder the .als itself
    /// lies in; carry the copy off to %TEMP% and Live will honestly report that it has lost
    /// every file of the project. Such a probe would be checking not the plugins but our own
    /// crookedness.
    /// </summary>
    public static class RescueProbe
    {
        public const string Suffix = ".alive-probe.als";

        /// <summary>The list of probes lying on disk right now — so they can be removed after a
        /// crash.</summary>
        static string JournalPath { get { return Path.Combine(Settings.Dir, "probes.txt"); } }

        public static bool IsProbe(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
        }

        public static string PathFor(SetEntry set)
        {
            return Path.Combine(set.Directory, set.Name + Suffix);
        }

        /// <summary>
        /// Remove probes left over from the previous run. The program crashes rarely, but a
        /// probe is an .als inside somebody else's project folder, and it cannot be left there
        /// for good: one day a person opens it instead of their own set and cannot work out why
        /// half of it has no plugins. Called at startup, before the first scan.
        /// </summary>
        public static void CleanupStale()
        {
            List<string> listed = ReadJournal();
            if (listed.Count == 0) return;

            int gone = 0;
            foreach (string p in listed)
            {
                // Only what really is a probe is removed from the journal: the journal file
                // lies there in the open and will one day be edited by hand.
                if (!IsProbe(p)) continue;
                try { if (File.Exists(p)) { File.Delete(p); gone++; } }
                catch (Exception ex) { Diag.Fail("rescue: cleanup " + p, ex); }
            }

            WriteJournal(new List<string>());
            if (gone > 0) Diag.Line("rescue: removed " + gone + " stale probe(s)");
        }

        public static void Remember(string probe)
        {
            List<string> all = ReadJournal();
            foreach (string p in all)
                if (string.Equals(p, probe, StringComparison.OrdinalIgnoreCase)) return;
            all.Add(probe);
            WriteJournal(all);
        }

        public static void Forget(string probe)
        {
            List<string> all = ReadJournal();
            List<string> kept = new List<string>();
            foreach (string p in all)
                if (!string.Equals(p, probe, StringComparison.OrdinalIgnoreCase)) kept.Add(p);
            if (kept.Count != all.Count) WriteJournal(kept);
        }

        /// <summary>
        /// Delete a probe and strike it from the journal. Quietly: tidying litter is no reason
        /// for an error window. We strike from the journal only what has really gone: Live
        /// itself may have held the file open, and then only the program's next run can remove
        /// it — on the strength of this very record.
        /// </summary>
        public static void Drop(string probe)
        {
            if (string.IsNullOrEmpty(probe)) return;
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch (Exception ex) { Diag.Fail("rescue: drop " + probe, ex); }
            if (!File.Exists(probe)) Forget(probe);
        }

        static List<string> ReadJournal()
        {
            List<string> all = new List<string>();
            try
            {
                if (!File.Exists(JournalPath)) return all;
                foreach (string raw in File.ReadAllLines(JournalPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length > 0) all.Add(line);
                }
            }
            catch (Exception ex) { Diag.Fail("rescue: read journal", ex); }
            return all;
        }

        static void WriteJournal(List<string> all)
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                File.WriteAllLines(JournalPath, all.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Diag.Fail("rescue: write journal", ex); }
        }
    }

    /// <summary>
    /// The investigation of one set that will not open.
    ///
    /// The search logic rests on a single assumption: ONE plugin breaks it. Each probe is then
    /// an answer to the question "is the culprit inside the disabled set?":
    ///
    ///     opened     → the culprit is among the disabled → suspects ∩= disabled
    ///     not opened → the culprit is not among them     → suspects −= disabled
    ///
    /// Any set works that way, not just an even half — which means a person can tick the boxes
    /// themselves, and the investigation will take that into account rather than lose its
    /// footing.
    ///
    /// The assumption is testable: if not a single suspect is left, more than one is guilty,
    /// and instead of a name the Working set is given — the one whose disabling opened the set.
    /// That is always true, even when there is no pretty answer.
    /// </summary>
    public sealed class RescueSession
    {
        public readonly SetEntry Set;
        public readonly AlsInfo Info;
        public readonly List<AlsPluginSlot> Targets;
        public readonly int Unaddressable;
        public readonly string Error;

        readonly PluginInventory _inv;

        /// <summary>What Live's log remembers about previous attempts to open this
        /// set.</summary>
        public LoadAttempt History;
        public AlsPluginSlot HistorySuspect;

        /// <summary>Who else may be guilty.</summary>
        public readonly List<AlsPluginSlot> Suspects = new List<AlsPluginSlot>();

        /// <summary>The smallest known set whose disabling opened the set.</summary>
        public List<AlsPluginSlot> Working;

        public RescueVerdict Verdict = RescueVerdict.NoProbeYet;
        public AlsPluginSlot Culprit;

        public int Round;
        public string ProbePath = "";
        public List<AlsPluginSlot> ProbeDisabled = new List<AlsPluginSlot>();
        public DateTime ProbeStarted;

        readonly List<string> _log = new List<string>();
        readonly Dictionary<string, LiveLogFile> _logs =
            new Dictionary<string, LiveLogFile>(StringComparer.OrdinalIgnoreCase);

        public RescueSession(SetEntry set, PluginInventory inv)
        {
            Set = set;
            _inv = inv;

            Info = AlsFile.Read(set.Path);
            if (!string.IsNullOrEmpty(Info.Error))
            {
                // The .als itself will not read — no plugin has anything to do with it here,
                // and that has to be said outright rather than sending a person round the
                // probes.
                Error = Info.Error;
                Targets = new List<AlsPluginSlot>();
                return;
            }

            // We read in what a minimal SetEntry may not have known — the caller, for instance,
            // may have had only the path to the file rather than a record from the catalog.
            // Reading the .als a second time just for that is not worth it: on heavy sets even
            // one pass is not free (see AlsPatch).
            if (set.Creator.Length == 0) set.Creator = Info.Creator;

            Targets = AlsPatch.Targets(Info);
            Unaddressable = AlsPatch.Unaddressable(Info);
            Suspects.AddRange(Targets);

            Diagnose();
        }

        public bool HasTargets { get { return Targets.Count > 0; } }
        public bool Finished { get { return Verdict == RescueVerdict.Culprit
                                          || Verdict == RescueVerdict.NotPlugins
                                          || Verdict == RescueVerdict.Group; } }

        /// <summary>The lines of the investigation's progress — the window shows them and the
        /// report carries them off.</summary>
        public IList<string> Trail { get { return _log; } }

        // ------------------------------------------------------------------ diagnosis

        /// <summary>
        /// Ask Live's log before any probes. If the set has been crashed already, Live recorded
        /// which plugin it broke off on — and the first probe can start with that one straight
        /// away.
        /// </summary>
        void Diagnose()
        {
            History = LiveLog.LastAttempt(Set.Path);
            if (History == null) return;

            PluginLoad hung = History.Hung;
            if (hung != null) HistorySuspect = Match(hung.Name);

            if (History.Result == LoadResult.Loaded)
                Note("Live's log says this set opened fine on " +
                     History.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            else if (hung != null)
                Note("Live's log stops inside " + hung.Format + " " + hung.Name +
                     " (" + History.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
            else
                Note("Live's log has an unfinished attempt from " +
                     History.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// A name from the log turned into a plugin of the set. Exactly first: both the log and
        /// the .als take the name from the same place, so it usually matches letter for letter.
        /// The lenient comparison is for the "Serum_x64" against "Serum (64 Bit)" case.
        /// </summary>
        public AlsPluginSlot Match(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (AlsPluginSlot s in Targets)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;

            string norm = PluginInventory.Normalize(name);
            if (norm.Length == 0) return null;
            foreach (AlsPluginSlot s in Targets)
                if (PluginInventory.Normalize(s.Name) == norm) return s;
            return null;
        }

        // ------------------------------------------------------------- what to probe

        /// <summary>
        /// What to suggest disabling in the next probe. A suggestion rather than an order: a
        /// person edits the boxes in the window themselves, and Apply copes with any set.
        /// </summary>
        public List<AlsPluginSlot> Suggest()
        {
            List<AlsPluginSlot> pick = new List<AlsPluginSlot>();
            if (Targets.Count == 0) return pick;

            // The first probe: if the log has already named a suspect — that one alone, right
            // away. Guessed right and the investigation ends on the very first probe rather
            // than the fifth.
            if (Working == null)
            {
                if (Round == 0 && HistorySuspect != null) { pick.Add(HistorySuspect); return pick; }
                pick.AddRange(Targets);
                return pick;
            }

            if (Suspects.Count > 1)
            {
                int half = Suspects.Count / 2;
                for (int i = 0; i < half; i++) pick.Add(Suspects[i]);
                return pick;
            }

            if (Suspects.Count == 1) { pick.Add(Suspects[0]); return pick; }

            pick.AddRange(Working);
            return pick;
        }

        /// <summary>
        /// Assemble a probe copy with disable switched off. Returns the path or throws —
        /// failing to write a probe means failing to start, and that cannot be kept quiet.
        /// </summary>
        public string Prepare(List<AlsPluginSlot> disable)
        {
            if (disable == null || disable.Count == 0)
                throw new InvalidOperationException("Pick at least one plugin to disable.");

            Cancel();

            List<string> uids = new List<string>();
            foreach (AlsPluginSlot s in disable) uids.Add(s.Uid);

            string probe = RescueProbe.PathFor(Set);
            RescueProbe.Remember(probe);            // the journal first, the disk second: a crash can happen in between
            int patched = AlsPatch.Neutralize(Set.Path, probe, uids, _inv);
            if (patched == 0)
            {
                RescueProbe.Drop(probe);
                throw new InvalidOperationException("None of those plugins were found inside the set.");
            }

            ProbePath = probe;
            ProbeDisabled = new List<AlsPluginSlot>(disable);
            ProbeStarted = DateTime.Now;
            Round++;

            // Live's logs are read from their current end: whatever came before the probe has
            // already been parsed in Diagnose, and there is no need to confuse earlier attempts
            // with this one.
            _logs.Clear();
            foreach (LiveLogFile f in LiveLog.Files())
            {
                f.SkipToEnd();
                _logs[f.Path] = f;
            }

            Note("Probe " + Round + ": " + Describe(disable) + " disabled");
            return probe;
        }

        /// <summary>Open the probe in Live — the same way a double click would open
        /// it.</summary>
        public void Launch()
        {
            if (ProbePath.Length == 0) throw new InvalidOperationException("No probe prepared.");
            Process.Start(new ProcessStartInfo(ProbePath) { UseShellExecute = true });
        }

        public void Cancel()
        {
            if (ProbePath.Length == 0) return;
            RescueProbe.Drop(ProbePath);
            ProbePath = "";
            ProbeDisabled = new List<AlsPluginSlot>();
        }

        // ------------------------------------------------------- the outcome of a probe

        /// <summary>
        /// What Live's logs say about the current probe. null means Live has not opened it yet.
        /// The files are re-read from scratch every time: which Live version a person will
        /// start is not known in advance, and a fresh install may not have had a Log.txt until
        /// now.
        /// </summary>
        public LoadAttempt Poll()
        {
            if (ProbePath.Length == 0) return null;

            foreach (LiveLogFile f in LiveLog.Files())
                if (!_logs.ContainsKey(f.Path)) _logs[f.Path] = f;   // from scratch: the file has only just appeared

            LoadAttempt best = null;
            foreach (LiveLogFile f in _logs.Values)
            {
                foreach (LoadAttempt a in f.ReadNew())
                {
                    if (!LiveLog.SamePath(a.Document, ProbePath)) continue;
                    if (best == null || a.Started >= best.Started) best = a;
                }
            }
            return best;
        }

        /// <summary>Whether Live is running right now — that is how we tell "still loading"
        /// from "died".</summary>
        public static bool LiveIsRunning()
        {
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Diag.Fail("rescue: process list", ex); return false; }

            bool found = false;
            foreach (Process p in all)
            {
                try
                {
                    // The process name is the exe name: "Ableton Live 12 Suite". We compare by
                    // the start, because the edition and the version differ for everyone.
                    if (!found && p.ProcessName.StartsWith("Ableton Live", StringComparison.OrdinalIgnoreCase))
                        found = true;
                }
                catch { }
                // Every Process holds a system handle, and this is asked once a second.
                finally { p.Dispose(); }
            }
            return found;
        }

        /// <summary>
        /// Take the outcome of a probe into account and narrow the circle. opened — the set
        /// opened in full.
        ///
        /// The disabled set is passed explicitly rather than taken from ProbeDisabled: the
        /// course of the investigation can then be run through without a single file on disk
        /// and without Live — which is how convergence is checked (see tools\RescueTest.cs, the
        /// simulate command).
        /// </summary>
        public void Apply(List<AlsPluginSlot> off, bool opened, LoadAttempt attempt)
        {
            if (off == null) off = ProbeDisabled;
            Cancel();

            if (opened)
            {
                Note("  → opened with " + off.Count + " disabled");
                if (Working == null || off.Count < Working.Count) Working = new List<AlsPluginSlot>(off);
                Intersect(Suspects, off);
            }
            else
            {
                string where = "";
                if (attempt != null && attempt.Hung != null) where = " — stopped inside " + attempt.Hung.Name;
                Note("  → did not open" + where);

                // The log named the plugin it broke off on: that one is guilty, and there is no
                // point halving further. We take the hint only if that plugin really was
                // enabled — otherwise it is about something else.
                AlsPluginSlot named = attempt != null && attempt.Hung != null
                                    ? Match(attempt.Hung.Name) : null;
                if (named != null && !Contains(off, named) && Contains(Suspects, named))
                {
                    Suspects.Clear();
                    Suspects.Add(named);
                }
                else Subtract(Suspects, off);
            }

            Settle(opened, off);
        }

        void Settle(bool opened, List<AlsPluginSlot> off)
        {
            // Everything that could be was disabled and it still would not open — the plugins
            // are not to blame.
            if (!opened && Working == null && off.Count == Targets.Count)
            {
                Verdict = RescueVerdict.NotPlugins;
                Note("Verdict: the plugins are not the problem — the set fails with all of them disabled.");
                return;
            }

            if (Working == null) { Verdict = RescueVerdict.None; return; }

            if (Suspects.Count == 0)
            {
                // Empty means more than one plugin is guilty, and there will be no pretty name.
                // Working, on the other hand, has been proved in practice: with it the set
                // opened.
                Verdict = RescueVerdict.Group;
                Note("Verdict: more than one plugin is involved. Disabling " +
                     Describe(Working) + " opens the set.");
                return;
            }

            if (opened && off.Count == 1 && Suspects.Count == 1)
            {
                Culprit = Suspects[0];
                Verdict = RescueVerdict.Culprit;
                Note("Verdict: " + Culprit.Format + " " + Culprit.Name + " breaks this set.");
                return;
            }

            Verdict = RescueVerdict.None;
        }

        // ------------------------------------------------------- the rescued copy

        /// <summary>
        /// What to disable in the rescued copy: the culprit that was found, or, if there is
        /// more than one, the whole proven set.
        /// </summary>
        public List<AlsPluginSlot> RescueSelection()
        {
            List<AlsPluginSlot> pick = new List<AlsPluginSlot>();
            if (Culprit != null) pick.Add(Culprit);
            else if (Working != null) pick.AddRange(Working);
            return pick;
        }

        public string RescuedPath
        {
            get { return Path.Combine(Set.Directory, Set.Name + " (rescued).als"); }
        }

        /// <summary>
        /// A copy next to the original with the guilty plugin anonymised. The original is not
        /// touched on any outcome: it will come in handy again when the plugin is updated or
        /// reinstalled.
        /// </summary>
        public string SaveRescued(List<AlsPluginSlot> disable)
        {
            if (disable == null || disable.Count == 0)
                throw new InvalidOperationException("Nothing to disable in the rescued copy.");

            string dst = Unique(RescuedPath);
            List<string> uids = new List<string>();
            foreach (AlsPluginSlot s in disable) uids.Add(s.Uid);

            AlsPatch.Neutralize(Set.Path, dst, uids, _inv);
            Note("Saved " + Path.GetFileName(dst));
            return dst;
        }

        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, name + " " + i.ToString(CultureInfo.InvariantCulture) + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        // ------------------------------------------------------------------ the report

        public string Report()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Alive — project rescue report");
            sb.AppendLine("Set:      " + Set.Path);
            if (Set.Creator.Length > 0) sb.AppendLine("Saved by: " + Set.Creator);
            sb.AppendLine("Plugins:  " + Targets.Count + " third-party"
                          + (Unaddressable > 0 ? " (" + Unaddressable + " unidentifiable)" : ""));
            sb.AppendLine();

            if (History != null)
            {
                sb.AppendLine("Live's own log (" + History.LiveVersion + "):");
                sb.AppendLine("  attempt at " + History.Started.ToString("yyyy-MM-dd HH:mm:ss",
                                                                        CultureInfo.InvariantCulture));
                sb.AppendLine("  restored " + History.RestoredCount + " of " + History.Plugins.Count + " plugins");
                foreach (PluginLoad p in History.Failures)
                    sb.AppendLine("  refused:  " + p.Format + " " + p.Name);
                if (History.Hung != null)
                    sb.AppendLine("  stops in: " + History.Hung.Format + " " + History.Hung.Name);
                sb.AppendLine();
            }

            if (_log.Count > 0)
            {
                sb.AppendLine("Probes:");
                foreach (string line in _log) sb.AppendLine("  " + line);
                sb.AppendLine();
            }

            sb.AppendLine("Verdict: " + VerdictText());
            return sb.ToString();
        }

        public string VerdictText()
        {
            switch (Verdict)
            {
                case RescueVerdict.Culprit:
                    return Culprit.Format + " " + Culprit.Name + " breaks this set.";
                case RescueVerdict.Group:
                    return "more than one plugin is involved; disabling " + Describe(Working)
                         + " opens the set.";
                case RescueVerdict.NotPlugins:
                    return "not a plugin problem — the set fails with every third-party plugin disabled.";
                case RescueVerdict.NoProbeYet:
                    return "no probe run yet.";
                default:
                    return Suspects.Count + " plugin(s) still suspected.";
            }
        }

        // ------------------------------------------------------------------ odds and ends

        void Note(string line)
        {
            _log.Add(line);
            Diag.Line("rescue: " + line);
        }

        public static string Describe(List<AlsPluginSlot> list)
        {
            if (list == null || list.Count == 0) return "nothing";
            if (list.Count == 1) return list[0].Name;
            if (list.Count <= 3)
            {
                string[] names = new string[list.Count];
                for (int i = 0; i < list.Count; i++) names[i] = list[i].Name;
                return string.Join(", ", names);
            }
            return list.Count + " plugins";
        }

        static bool Contains(List<AlsPluginSlot> list, AlsPluginSlot s)
        {
            foreach (AlsPluginSlot x in list)
                if (string.Equals(x.Uid, s.Uid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Intersect(List<AlsPluginSlot> target, List<AlsPluginSlot> keep)
        {
            for (int i = target.Count - 1; i >= 0; i--)
                if (!Contains(keep, target[i])) target.RemoveAt(i);
        }

        static void Subtract(List<AlsPluginSlot> target, List<AlsPluginSlot> drop)
        {
            for (int i = target.Count - 1; i >= 0; i--)
                if (Contains(drop, target[i])) target.RemoveAt(i);
        }
    }
}
