using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Watches the catalog roots and reports when something there has changed — so that a new
    /// set appears in the list on its own, without F5.
    ///
    /// The event always comes with a delay: one save from Live is not one change on disk but a
    /// burst of them (a temporary file, a rename, a folder update), and scanning on each would
    /// mean walking the whole tree several times in a row. The countdown restarts from every
    /// new event, so during an export or a recording, when the disk is written to continuously,
    /// the rescan simply waits for quiet.
    /// </summary>
    public sealed class FolderWatch : IDisposable
    {
        readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        readonly Timer _settle = new Timer();
        readonly ISynchronizeInvoke _sync;

        /// <summary>Already on the UI thread — SynchronizingObject sees to that.</summary>
        public event Action Changed;

        public FolderWatch(ISynchronizeInvoke sync)
        {
            _sync = sync;
            _settle.Interval = 3000;
            _settle.Tick += delegate
            {
                _settle.Stop();
                if (Changed != null) Changed();
            };
        }

        /// <summary>
        /// Point the watch at a new set of roots. Roots excluded from scanning are skipped: the
        /// catalog does not show their contents anyway, and there is no reason to be woken from
        /// there.
        /// </summary>
        public void Watch(IEnumerable<string> roots, IEnumerable<string> disabled)
        {
            Stop();
            if (roots == null) return;

            HashSet<string> off = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (disabled != null) foreach (string d in disabled) off.Add(d);

            foreach (string root in roots)
            {
                if (off.Contains(root)) continue;
                try
                {
                    if (!Directory.Exists(root)) continue;

                    // Two watchers per root instead of one for everything.
                    //
                    // The first — .als only, but including content changes: a set was re-saved,
                    // it has a different tempo and different plugins, and the catalog has to
                    // see that.
                    //
                    // The second — anything appearing or disappearing: a new project folder, a
                    // deleted project, a fresh render next to a set (the listen button depends
                    // on it). CONTENT changes are not watched here — otherwise every recorded
                    // sample would reset the countdown, and while working in Live the refresh
                    // would never come at all.
                    Add(root, "*.als", NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size);
                    Add(root, "*", NotifyFilters.FileName | NotifyFilters.DirectoryName);
                }
                catch { }
            }
        }

        void Add(string root, string filter, NotifyFilters notify)
        {
            FileSystemWatcher w = new FileSystemWatcher(root, filter);
            w.IncludeSubdirectories = true;
            w.NotifyFilter = notify;
            // The handlers arrive on the UI thread by themselves — otherwise the WinForms timer
            // they touch would be started from a pool thread and would not tick at all.
            w.SynchronizingObject = _sync;
            w.Changed += OnAny;
            w.Created += OnAny;
            w.Deleted += OnAny;
            w.Renamed += OnAny;
            // An internal buffer overflow means "there were so many events that some were
            // lost", which is all the more reason to rescan.
            w.Error += delegate { Bump(); };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }

        /// <summary>
        /// The rescue helper's probes do not wake the catalog: it does not show them anyway
        /// (see FolderScan.Find), and during an investigation they appear and disappear on
        /// every probe — each time triggering a full rescan.
        /// </summary>
        void OnAny(object sender, FileSystemEventArgs e)
        {
            if (RescueProbe.IsProbe(e.Name) || RescueProbe.IsProbe(e.FullPath)) return;
            Bump();
        }

        void Bump()
        {
            _settle.Stop();
            _settle.Start();
        }

        public void Stop()
        {
            foreach (FileSystemWatcher w in _watchers)
            {
                try { w.EnableRaisingEvents = false; w.Dispose(); } catch { }
            }
            _watchers.Clear();
            _settle.Stop();
        }

        public void Dispose()
        {
            Stop();
            _settle.Dispose();
        }
    }
}
