using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>How one attempt to open a set ended.</summary>
    public enum LoadResult
    {
        Running,    // the block started and is still being written — Live is loading right now
        Loaded,     // "Loaded document was created by …" — the document was read through in full
        Broke       // the block broke off: further on in the log there is another attempt or the end of the file
    }

    /// <summary>One plugin Live was raising while opening a set.</summary>
    public sealed class PluginLoad
    {
        public string Name = "";
        public PluginKind Kind;
        public bool Restored;      // we saw the matching "Restored: name"
        public bool Failed;        // we saw "Restore N failed: name" — the plugin refused, but Live survived
        public DateTime At;

        public string Format { get { return Kind == PluginKind.Vst3 ? "VST3" : "VST2"; } }

        /// <summary>Neither an answer nor an error: Live went into the plugin and never came
        /// back.</summary>
        public bool Hung { get { return !Restored && !Failed; } }
    }

    /// <summary>
    /// A block of the log from "Loading document …" to the next one just like it — that is,
    /// exactly one attempt to open one set, with the list of plugins raised along the way.
    /// </summary>
    public sealed class LoadAttempt
    {
        public string LogPath = "";
        public string LiveVersion = "";      // the settings folder name: "Live 12.4.3"
        public string Document = "";         // the path exactly as Live wrote it
        public DateTime Started;
        public DateTime LastEvent;
        public string CreatedBy = "";        // "Ableton Live 11.2.6" out of the Loaded document line
        public LoadResult Result = LoadResult.Running;

        public readonly List<PluginLoad> Plugins = new List<PluginLoad>();

        /// <summary>
        /// The plugin everything broke off on. It is necessarily the LAST record of the block
        /// and necessarily without a pair: Live died inside its code before it could finish
        /// writing the log, so there is physically nothing after it in the block.
        ///
        /// "The last" specifically, not "any one without a pair". A record also stays unpaired
        /// when a plugin introduced itself by one name and reported back under another:
        ///
        ///     info: VST3: Going to restore: SpaceCarver
        ///     info: VST3: plugin processor successfully loaded: Blindspot Audio 'Oppressor'
        ///     info: VST3: Restored: Oppressor
        ///
        /// That is how plugins from one build with a shared class prefix behave (there are six
        /// of them in the log from this machine, and every one is in a set that opened
        /// perfectly well). Counting them as hung means accusing a healthy plugin, and the
        /// investigation begins with that very name.
        /// </summary>
        public PluginLoad Hung
        {
            get
            {
                if (Result == LoadResult.Loaded || Plugins.Count == 0) return null;
                PluginLoad last = Plugins[Plugins.Count - 1];
                return last.Hung ? last : null;
            }
        }

        public int RestoredCount
        {
            get { int n = 0; foreach (PluginLoad p in Plugins) if (p.Restored) n++; return n; }
        }

        public List<PluginLoad> Failures
        {
            get
            {
                List<PluginLoad> r = new List<PluginLoad>();
                foreach (PluginLoad p in Plugins) if (p.Failed) r.Add(p);
                return r;
            }
        }
    }

    /// <summary>
    /// Live's own log: %APPDATA%\Ableton\Live &lt;version&gt;\Preferences\Log.txt.
    ///
    /// This is the rare case where nothing needs working out at all — Live writes everything we
    /// need itself, and writes it before falling over:
    ///
    ///     info: Loading document "E:\...\big black.als"
    ///     info: VST3: Going to restore: soothe2
    ///     info: VST3: Restored: soothe2
    ///     info: Loaded document was created by Ableton Live 11.2.6
    ///
    /// If Live dies inside a plugin, the last line of the block is a "Going to restore" with no
    /// pair. That is the culprit's name, without a single launch and without a binary search.
    /// The log from this machine holds 663 "Going to restore" against 563 "Restored": a hundred
    /// aborted loads, each one a ready diagnosis.
    ///
    /// The log is cumulative and is not rewritten between runs of Live, so an attempt from a
    /// week ago is as visible as today's.
    /// </summary>
    public static class LiveLog
    {
        const string InfoMark = ": info: ";
        const string ErrorMark = ": error: ";

        const string LoadingMark = "Loading document \"";
        const string LoadedMark = "Loaded document was created by ";

        /// <summary>The logs of every Live install — from the one written to last back to the
        /// older ones.</summary>
        public static List<LiveLogFile> Files()
        {
            List<LiveLogFile> found = new List<LiveLogFile>();
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
                if (!Directory.Exists(root)) return found;

                foreach (string dir in Directory.GetDirectories(root))
                {
                    string log = Path.Combine(dir, Path.Combine("Preferences", "Log.txt"));
                    if (File.Exists(log)) found.Add(new LiveLogFile(log, Path.GetFileName(dir)));
                }
                found.Sort(delegate (LiveLogFile a, LiveLogFile b)
                { return b.Written.CompareTo(a.Written); });
            }
            catch (Exception ex) { Diag.Fail("livelog: enumerate", ex); }
            return found;
        }

        /// <summary>
        /// The most recent attempt to open this particular set — across every installed version
        /// of Live at once. There are usually several versions on a machine, and which of them
        /// broke on a project the user neither remembers nor is obliged to know.
        /// </summary>
        public static LoadAttempt LastAttempt(string alsPath)
        {
            LoadAttempt best = null;
            foreach (LiveLogFile f in Files())
            {
                LoadAttempt a = f.LastAttempt(alsPath);
                if (a == null) continue;
                if (best == null || a.Started > best.Started) best = a;
            }
            return best;
        }

        // ------------------------------------------------------------------ parsing

        /// <summary>
        /// Parses an already-read chunk of the log into open attempts. A method of its own
        /// rather than a private gut of LiveLogFile: that way it can be called both on the tail
        /// of a file while watching and on a whole file for a one-off diagnosis.
        /// </summary>
        internal static List<LoadAttempt> Parse(IEnumerable<string> lines, string logPath, string version)
        {
            List<LoadAttempt> attempts = new List<LoadAttempt>();
            LoadAttempt cur = null;

            foreach (string raw in lines)
            {
                if (raw == null || raw.Length == 0) continue;

                DateTime at;
                string text = Payload(raw, out at);
                if (text == null) continue;   // the continuation of a multi-line record — there are no markers in it

                if (text.StartsWith(LoadingMark, StringComparison.Ordinal))
                {
                    // A new block closes the previous one: if that one never managed to say
                    // "Loaded document", then it never loaded.
                    Close(cur);
                    cur = new LoadAttempt();
                    cur.LogPath = logPath;
                    cur.LiveVersion = version;
                    cur.Document = Quoted(text, LoadingMark.Length);
                    cur.Started = cur.LastEvent = at;
                    attempts.Add(cur);
                    continue;
                }

                if (cur == null) continue;
                cur.LastEvent = at;

                if (text.StartsWith(LoadedMark, StringComparison.Ordinal))
                {
                    cur.CreatedBy = text.Substring(LoadedMark.Length).Trim();
                    cur.Result = LoadResult.Loaded;
                    continue;
                }

                PluginKind kind;
                string rest = AfterVstTag(text, out kind);
                if (rest == null) continue;

                if (rest.StartsWith("Going to restore: ", StringComparison.Ordinal))
                {
                    PluginLoad p = new PluginLoad();
                    p.Kind = kind;
                    p.Name = rest.Substring("Going to restore: ".Length).Trim();
                    p.At = at;
                    cur.Plugins.Add(p);
                }
                else if (rest.StartsWith("Restored: ", StringComparison.Ordinal))
                {
                    Pending(cur, rest.Substring("Restored: ".Length).Trim(), true, false);
                }
                else if (rest.StartsWith("Restore ", StringComparison.Ordinal))
                {
                    // "Restore 1 failed: Dist COLDFIRE" — the attempt number is of no use to
                    // us.
                    int failed = rest.IndexOf(" failed: ", StringComparison.Ordinal);
                    if (failed > 0)
                        Pending(cur, rest.Substring(failed + " failed: ".Length).Trim(), false, true);
                }
            }

            return attempts;
        }

        /// <summary>
        /// The block ended and there was no "Loaded document" — meaning it broke off. While the
        /// block is the last one in the file this may still be simply "Live is loading right
        /// now", and that verdict is passed by the caller (see LiveLogFile.Finish).
        /// </summary>
        static void Close(LoadAttempt a)
        {
            if (a != null && a.Result == LoadResult.Running) a.Result = LoadResult.Broke;
        }

        /// <summary>
        /// Close the last unclosed record with this name. By name rather than "the last one at
        /// all": Live has nested restores (a plugin inside a rack), and the order of closing
        /// then does not match the order of opening.
        /// </summary>
        static void Pending(LoadAttempt a, string name, bool restored, bool failed)
        {
            for (int i = a.Plugins.Count - 1; i >= 0; i--)
            {
                PluginLoad p = a.Plugins[i];
                if (!p.Hung) continue;
                if (!string.Equals(p.Name, name, StringComparison.Ordinal)) continue;
                p.Restored = restored;
                p.Failed = failed;
                return;
            }

            // No pair was found: the log was read starting from the middle of a block. The
            // record is valuable all the same — it says this plugin got through loading.
            PluginLoad orphan = new PluginLoad();
            orphan.Name = name;
            orphan.Restored = restored;
            orphan.Failed = failed;
            a.Plugins.Add(orphan);
        }

        /// <summary>"VST3: Going to restore: X" → "Going to restore: X", otherwise
        /// null.</summary>
        static string AfterVstTag(string text, out PluginKind kind)
        {
            kind = PluginKind.Vst3;
            if (text.StartsWith("VST3: ", StringComparison.Ordinal)) return text.Substring(6);
            if (text.StartsWith("VST2: ", StringComparison.Ordinal))
            {
                kind = PluginKind.Vst2;
                return text.Substring(6);
            }
            return null;
        }

        /// <summary>
        /// "2026-08-24T15:15:42.939602: info: Loading document "…"" → the text of the record
        /// itself. Lines without such a header are the continuation of the previous record
        /// (Live wraps MIDI device lists over several lines), and there are never markers in
        /// them.
        /// </summary>
        static string Payload(string line, out DateTime at)
        {
            at = DateTime.MinValue;

            // "info" or "error" — the level itself is of no use to us: everything that needs
            // telling apart is told apart by the record's text ("Restore 1 failed" comes as an
            // error and "Restored" as a message, and we catch them by their words, not by their
            // level).
            int mark = line.IndexOf(InfoMark, StringComparison.Ordinal);
            int len = InfoMark.Length;
            if (mark < 0)
            {
                mark = line.IndexOf(ErrorMark, StringComparison.Ordinal);
                len = ErrorMark.Length;
            }
            if (mark <= 0) return null;

            at = Stamp(line.Substring(0, mark));
            return line.Substring(mark + len);
        }

        static DateTime Stamp(string s)
        {
            DateTime v;
            if (DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss.ffffff",
                                       CultureInfo.InvariantCulture, DateTimeStyles.None, out v))
                return v;
            if (DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss",
                                       CultureInfo.InvariantCulture, DateTimeStyles.None, out v))
                return v;
            return DateTime.MinValue;
        }

        static string Quoted(string text, int from)
        {
            int close = text.IndexOf('"', from);
            return close < 0 ? text.Substring(from) : text.Substring(from, close - from);
        }

        // ------------------------------------------------------- comparing paths

        /// <summary>
        /// Whether this is the same file. Live writes the path sometimes with backslashes (the
        /// command line) and sometimes with forward ones (a template from the library), so the
        /// strings cannot be compared as they are.
        /// </summary>
        public static bool SamePath(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        internal static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.Replace('/', '\\').TrimEnd('\\');
            try { s = Path.GetFullPath(s); }
            catch { }
            return s;
        }
    }

    /// <summary>
    /// One Log.txt that can be both read whole and read on as Live writes into it. The
    /// reading-on is needed by the rescue window: it waits to see how a probe ends and has to
    /// show the answer at once rather than chew through nine megabytes on every tick of the
    /// timer.
    /// </summary>
    public sealed class LiveLogFile
    {
        public readonly string Path;
        public readonly string Version;

        /// <summary>
        /// Which byte to read on from. It stands not at the end of the file but at the start of
        /// the last unclosed "Loading document" block: the block is parsed whole every time, or
        /// an attempt that began between ticks would arrive without its first plugins.
        /// </summary>
        long _offset;

        internal LiveLogFile(string path, string version)
        {
            Path = path;
            Version = version;
        }

        public DateTime Written
        {
            get { try { return File.GetLastWriteTimeUtc(Path); } catch { return DateTime.MinValue; } }
        }

        public long Length
        {
            get { try { return new FileInfo(Path).Length; } catch { return 0; } }
        }

        /// <summary>Start watching from the current end — the past is of no interest to us in a
        /// probe.</summary>
        public void SkipToEnd() { _offset = Length; }

        /// <summary>The most recent attempt to open this set in the whole file.</summary>
        public LoadAttempt LastAttempt(string alsPath)
        {
            LoadAttempt best = null;
            foreach (LoadAttempt a in All())
            {
                if (!LiveLog.SamePath(a.Document, alsPath)) continue;
                if (best == null || a.Started >= best.Started) best = a;
            }
            return best;
        }

        /// <summary>Every attempt in the whole file.</summary>
        public List<LoadAttempt> All()
        {
            long start = 0;
            return Read(0, false, out start);
        }

        /// <summary>
        /// What has been appended since last time. The last block may not be finished yet — it
        /// comes back with Result = Running, and next time it will arrive again, this time
        /// whole.
        /// </summary>
        public List<LoadAttempt> ReadNew()
        {
            long next;
            List<LoadAttempt> a = Read(_offset, true, out next);
            _offset = next;
            return a;
        }

        List<LoadAttempt> Read(long from, bool advance, out long next)
        {
            long fileEnd = from;
            next = from;

            byte[] buf;
            try
            {
                // The file got shorter — Live was reinstalled or the log was trimmed. We start
                // over.
                if (Length < from) from = 0;

                // FileShare.ReadWrite is mandatory: the log is open for writing by Live itself,
                // and without it reading would fail at exactly the moment it was set up for.
                using (FileStream fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
                {
                    fs.Position = from;
                    long size = fs.Length - from;
                    if (size < 0) size = 0;
                    if (size > MaxRead) { from = fs.Length - MaxRead; fs.Position = from; size = MaxRead; }

                    buf = new byte[size];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got != buf.Length) Array.Resize(ref buf, got);
                    fileEnd = from + got;
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("livelog: read " + Path, ex);
                return new List<LoadAttempt>();
            }

            // We split it ourselves, by bytes. StreamReader.ReadLine will not do here: we need
            // the exact offset of a line's start in the file in order to come back to an
            // unclosed block, and it cannot be recomputed from the length of the decoded string
            // — Live's log is UTF-8 and full of Cyrillic from project names, where a character
            // is not a byte. Live's line endings are lone LFs, but we strip a trailing \r just
            // in case.
            long lastOpenBlock = -1;
            List<string> lines = new List<string>();
            int start = 0;
            for (int i = 0; i <= buf.Length; i++)
            {
                if (i < buf.Length && buf[i] != (byte)'\n') continue;

                int end = i;
                if (end > start && buf[end - 1] == (byte)'\r') end--;
                if (end > start)
                {
                    string line = Encoding.UTF8.GetString(buf, start, end - start);
                    if (line.IndexOf(OpenBlock, StringComparison.Ordinal) >= 0)
                        lastOpenBlock = from + start;
                    lines.Add(line);
                }
                start = i + 1;
            }

            List<LoadAttempt> attempts = LiveLog.Parse(lines, Path, Version);
            Settle(attempts);

            // The last block may not have been finished — Live is loading right now. The offset
            // then steps back to its start so that next time the block is re-read whole and the
            // denouement is seen. A block that has been read through does not need returning
            // that way.
            bool tailOpen = attempts.Count > 0
                         && attempts[attempts.Count - 1].Result == LoadResult.Running
                         && lastOpenBlock >= 0;
            if (advance) next = tailOpen ? lastOpenBlock : fileEnd;
            else next = fileEnd;

            return attempts;
        }

        /// <summary>
        /// Parsing always leaves the last block of a file as "Running": there is nothing to
        /// close it with — the next "Loading document" is not there yet. But if nobody has
        /// written to the log for a long time, then Live is not loading but not writing at all
        /// — and the block broke off.
        /// </summary>
        void Settle(List<LoadAttempt> attempts)
        {
            if (attempts.Count == 0) return;
            LoadAttempt last = attempts[attempts.Count - 1];
            if (last.Result != LoadResult.Running) return;

            DateTime written = Written;
            if (written == DateTime.MinValue) return;
            if (DateTime.UtcNow - written > Silence) last.Result = LoadResult.Broke;
        }

        const string OpenBlock = ": info: Loading document \"";

        /// <summary>
        /// How long the log has to stay silent before a load counts as aborted. The threshold
        /// is generous on purpose: between the "Going to restore" and "Restored" lines a heavy
        /// plugin leaves up to ten seconds of silence (measured on Addictive Drums 2 — seven
        /// seconds), and a hasty threshold would name a healthy plugin guilty.
        /// </summary>
        static readonly TimeSpan Silence = TimeSpan.FromSeconds(45);

        /// <summary>
        /// How many bytes of the log to read at a time. Log.txt is cumulative and on this
        /// machine reaches nine megabytes; thirty-two is a cap both for a one-off diagnosis and
        /// against a file grown indecently large.
        /// </summary>
        const long MaxRead = 32L * 1024 * 1024;
    }
}
