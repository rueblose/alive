using System;
using System.Collections.Generic;
using System.IO;

namespace AbletonManager
{
    /// <summary>
    /// The history of the work: on which days and at which hours projects were saved.
    ///
    /// The source is the Backup folders. Live puts a copy there on every save and writes the
    /// moment of saving into its name; nothing else about the past is kept on disk, and the
    /// .als itself remembers only the last time. The parsing is done by FolderScan inside the
    /// very walk that already counts the project folder's weight — there are no extra trips to
    /// the disk.
    ///
    /// But disk alone is not enough: Live keeps only the TEN most recent copies per set name
    /// and overwrites the rest (measured: 144 Backup folders with exactly 10 files each). So
    /// the history erases itself — and precisely for the projects worked on most intensively.
    /// That is why our own cache is not overwritten by a scan but accumulates: a day that once
    /// made it into the history never leaves it again, even if Live threw that copy out long
    /// ago and the project itself is deleted.
    ///
    /// Files from the Alive subfolder inside Backup do not go into the history: they are taken
    /// over the same Live save and would double the day. They are cut off by themselves — both
    /// by the folder name and by the shape of the file name.
    ///
    /// The object is immutable: a scan builds a new one and swaps the reference whole, just as
    /// with the list of sets. It is read from the UI thread and written by the background one.
    /// </summary>
    public sealed class Activity
    {
        public static readonly Activity Empty = new Activity(new List<Mark>());

        /// <summary>One save: when, and the project folder it was made in — "" for a save the
        /// history took in before it kept folders.</summary>
        struct Mark
        {
            public DateTime When;
            public string Project;
        }

        readonly List<Mark> _marks;                      // ascending by When
        readonly Dictionary<DateTime, int> _byDay = new Dictionary<DateTime, int>();
        readonly int[] _byHour = new int[24];

        public int Total { get { return _marks.Count; } }
        public int ActiveDays { get { return _byDay.Count; } }

        public DateTime First, Last;
        public int CurrentStreak, LongestStreak;
        public DateTime BusiestDay;
        public int BusiestSaves;
        public int PeakHour = -1;

        /// <summary>How many saves fell on this day.</summary>
        public int SavesOn(DateTime day)
        {
            int n;
            return _byDay.TryGetValue(day.Date, out n) ? n : 0;
        }

        public int[] HourHistogram { get { return _byHour; } }

        /// <summary>
        /// The project folders saved in on that day, the busiest first — what a day's cell in
        /// the year of work opens. Saves the history took in before it kept folders name
        /// nothing, so a day of those alone gives an empty list.
        /// </summary>
        public List<string> ProjectsOn(DateTime day)
        {
            Dictionary<string, int> saves = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            List<string> order = new List<string>();
            DateTime d = day.Date;
            foreach (Mark m in _marks)
            {
                if (m.When.Date != d || m.Project.Length == 0) continue;
                int n;
                if (!saves.TryGetValue(m.Project, out n)) order.Add(m.Project);
                saves[m.Project] = n + 1;
            }
            // Stable on ties: the one saved first that day stays first.
            List<string> sorted = new List<string>(order);
            sorted.Sort(delegate (string x, string y)
            {
                int c = saves[y].CompareTo(saves[x]);
                return c != 0 ? c : order.IndexOf(x).CompareTo(order.IndexOf(y));
            });
            return sorted;
        }

        // ------------------------------------------------------------------ building

        Activity(List<Mark> marks)
        {
            marks.Sort(delegate (Mark x, Mark y) { return x.When.CompareTo(y.When); });
            _marks = marks;
            if (marks.Count == 0) return;

            First = marks[0].When;
            Last = marks[marks.Count - 1].When;

            foreach (Mark m in marks)
            {
                DateTime t = m.When;
                DateTime day = t.Date;
                int n;
                _byDay.TryGetValue(day, out n);
                _byDay[day] = n + 1;
                _byHour[t.Hour]++;
            }

            foreach (KeyValuePair<DateTime, int> kv in _byDay)
                if (kv.Value > BusiestSaves) { BusiestSaves = kv.Value; BusiestDay = kv.Key; }

            for (int h = 0; h < 24; h++)
                if (PeakHour < 0 || _byHour[h] > _byHour[PeakHour]) PeakHour = h;

            List<DateTime> days = new List<DateTime>(_byDay.Keys);
            days.Sort();
            Streaks(days);
        }

        void Streaks(List<DateTime> days)
        {
            int run = 0;
            for (int i = 0; i < days.Count; i++)
            {
                run = i > 0 && days[i] == days[i - 1].AddDays(1) ? run + 1 : 1;
                if (run > LongestStreak) LongestStreak = run;
            }

            // The current streak is counted from today, but the day is not over yet: if today
            // has not been sat down to, it is too early to break the streak, so we look from
            // yesterday. GitHub counts it the same way, and that is how it feels from the
            // inside too.
            DateTime cursor = DateTime.Today;
            if (!_byDay.ContainsKey(cursor)) cursor = cursor.AddDays(-1);
            while (_byDay.ContainsKey(cursor)) { CurrentStreak++; cursor = cursor.AddDays(-1); }
        }

        /// <summary>
        /// Build the history out of what the scan brought in, adding it to what is already
        /// known. previous is the history of the last run (usually from the cache); null when
        /// building from scratch.
        ///
        /// The .als time itself counts ONLY if the set has no copies at all.
        ///
        /// A fresh copy IS the last save: Live throws the oldest out of Backup rather than the
        /// newest, so the top mark always coincides with the current file. That means that with
        /// live copies the .als time is either a duplicate (and the set would be counted twice)
        /// or the trace of an edit made AROUND Live — Collect All and the rescue helper rewrite
        /// the .als themselves, and that is not a save. There used to be a 90-second window
        /// here, and both cases slipped through it: a Collect All edit 72 days before the last
        /// copy, and a file that arrived from another time zone — its time was off by exactly 4
        /// hours.
        ///
        /// Taking the copies alone will not do either: sets saved only once have no copies at
        /// all (there is nothing to copy on a first save), and they would vanish entirely.
        /// </summary>
        internal static Activity Build(IList<string> dirs, FolderScan.Weight[] weights,
                                       IEnumerable<SetEntry> sets, Activity previous)
        {
            // A second is key enough: saving twice within the same second is only possible from
            // two copies of Live at once, and the price of such a coincidence is one uncounted
            // save. Without a key, meanwhile, the cache would double on every scan.
            //
            // The filtering is needed within a single walk too: if a set does not lie in a "*
            // Project", its own folder counts as the project folder, and that can be the parent
            // of other people's projects — whose copies then get walked twice. Measured: 1,143
            // copy files give 1,135 marks, eight arrived a second time.
            Dictionary<long, int> seen = new Dictionary<long, int>();     // a moment -> its place in marks
            List<Mark> marks = new List<Mark>();

            if (previous != null)
                foreach (Mark m in previous._marks)
                    Add(marks, seen, m.When, m.Project);

            // The key is the folder plus the set name: one project folder holds different
            // versions, and each has copies of its own, carrying its own name at the front.
            HashSet<string> newest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (dirs != null && weights != null)
                for (int i = 0; i < dirs.Count && i < weights.Length; i++)
                {
                    List<FolderScan.Save> saves = weights[i].Saves;
                    if (saves == null) continue;
                    foreach (FolderScan.Save sv in saves)
                    {
                        Add(marks, seen, sv.When, dirs[i]);

                        // Note that this set has copies — the .als time itself is filtered out
                        // below on the strength of this.
                        newest.Add(dirs[i] + "|" + sv.Set);
                    }
                }

            if (sets != null)
                foreach (SetEntry s in sets)
                {
                    if (s.IsBackup || s.Modified == default(DateTime)) continue;
                    if (newest.Contains(s.ProjectDir + "|" + s.Name)) continue;

                    Add(marks, seen, s.Modified.ToLocalTime(), s.ProjectDir);
                }

            return new Activity(marks);
        }

        /// <summary>
        /// A save taken in once. A moment already known is not counted again — but if it came
        /// without a project (a history from before folders were kept) and the walk has just
        /// found it in a Backup folder, it gets that folder now.
        /// </summary>
        static void Add(List<Mark> marks, Dictionary<long, int> seen, DateTime when, string project)
        {
            if (!Sane(when)) return;
            project = project ?? "";
            int i;
            if (seen.TryGetValue(when.Ticks, out i))
            {
                if (marks[i].Project.Length == 0 && project.Length > 0)
                {
                    Mark named = marks[i];
                    named.Project = project;
                    marks[i] = named;
                }
                return;
            }
            seen[when.Ticks] = marks.Count;
            Mark m = new Mark();
            m.When = when;
            m.Project = project;
            marks.Add(m);
        }

        /// <summary>
        /// Does the mark look plausible? The cache accumulates and is never cleaned, so a
        /// single entry with a wrong clock would stay in the history forever and stretch the
        /// calendar over empty decades. The filter also stands on what comes in from the cache:
        /// once the clock is fixed, the junk goes by itself at the next scan.
        /// </summary>
        static bool Sane(DateTime t)
        {
            return t.Year >= 2000 && t <= DateTime.Today.AddDays(2);
        }

        // -------------------------------------------------------------------- cache

        /// <summary>
        /// A file next to index.cache. This is NOT an accelerator but the only durable copy of
        /// the history: on disk, the surviving Live copies hold between one and six days of
        /// work on a project (measured across folders that hit the limit of ten), and all the
        /// rest of the past exists only here. That is why it is written through a temporary
        /// file, and read up to the last intact record rather than "all or nothing".
        /// </summary>
        const int CacheVersion = 1;

        /// <summary>
        /// The projects follow the moments as a tail behind this mark, one folder per moment in
        /// the same order. Not a new version: a copy of Alive from before would read a new
        /// version as nothing and, at its next scan, write the history over with only what is
        /// left in Backup. A tail it simply does not reach.
        /// </summary>
        const int ProjectsMark = 0x6A6F7270;     // "proj"

        static string CachePath { get { return Path.Combine(Settings.Dir, "activity.cache"); } }

        public static Activity LoadCache()
        {
            List<Mark> marks = new List<Mark>();
            try
            {
                if (!File.Exists(CachePath)) return Empty;
                using (BinaryReader r = new BinaryReader(File.OpenRead(CachePath)))
                {
                    if (r.ReadInt32() != CacheVersion) return Empty;
                    int n = r.ReadInt32();
                    if (n < 0 || n > 5000000) return Empty;

                    // We read as much as reads. A truncated record is a lost tail, not a reason
                    // to throw away years: returning Empty would also have us overwrite the
                    // remainder with a blank at the next scan.
                    for (int i = 0; i < n; i++)
                    {
                        Mark m = new Mark();
                        m.When = new DateTime(r.ReadInt64(), DateTimeKind.Local);
                        m.Project = "";
                        marks.Add(m);
                    }

                    // The projects, if this history has them — see ProjectsMark.
                    if (r.BaseStream.Position < r.BaseStream.Length && r.ReadInt32() == ProjectsMark)
                        for (int i = 0; i < marks.Count; i++)
                        {
                            Mark m = marks[i];
                            m.Project = r.ReadString();
                            marks[i] = m;
                        }
                }
            }
            catch { }
            return marks.Count == 0 ? Empty : new Activity(marks);
        }

        public void SaveCache()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);

                // We write alongside and swap in the finished file. Writing straight over would
                // mean that a computer switched off midway leaves a stump instead of the whole
                // history, with nowhere left to recover it from.
                string tmp = CachePath + ".tmp";
                using (BinaryWriter w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(CacheVersion);
                    w.Write(_marks.Count);
                    foreach (Mark m in _marks) w.Write(m.When.Ticks);
                    w.Write(ProjectsMark);
                    foreach (Mark m in _marks) w.Write(m.Project);
                }
                if (File.Exists(CachePath)) File.Replace(tmp, CachePath, null);
                else File.Move(tmp, CachePath);
            }
            catch { }
        }
    }
}
