using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Reads an arrangement in the background and keeps the last few in memory. Parsing a set
    /// takes from 100 ms to a second, so it must not happen on the UI thread: stepping through
    /// the list with the arrow keys would freeze the window on every row.
    /// </summary>
    public sealed class ArrangementLoader
    {
        /// <summary>
        /// How many parsed arrangements we keep. One is about 0.4 MB (measured on a real
        /// library), so the previous 250 meant up to a hundred megabytes of memory for nothing:
        /// at any moment one arrangement is on screen in the detail panel and one full screen,
        /// while the home tiles live off their own cache of finished pictures.
        /// </summary>
        const int CacheSize = 12;

        /// <summary>
        /// How many sets we parse at once. Parsing is pure CPU (decompression plus XML), and on
        /// a single thread two dozen home tiles took over three seconds to fill. More than
        /// three threads is not worth taking: their priority is below normal, but this is still
        /// background work, not the reason the program was started.
        /// </summary>
        const int MaxWorkers = 3;

        readonly Control _ui;
        readonly object _lock = new object();
        readonly Dictionary<string, Arrangement> _cache = new Dictionary<string, Arrangement>();
        readonly List<string> _order = new List<string>();

        // What is waiting to be parsed and what is being parsed right now.
        readonly List<string> _pending = new List<string>();
        readonly HashSet<string> _working = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _busy;

        /// <summary>Arrives on the UI thread.</summary>
        public event Action<Arrangement> Ready;

        public ArrangementLoader(Control ui) { _ui = ui; }

        public Arrangement Cached(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;
            lock (_lock)
            {
                Arrangement a;
                return _cache.TryGetValue(path, out a) ? a : null;
            }
        }

        /// <summary>
        /// Requests a parse. The queue is served from the end: the freshest request goes first.
        /// This is the same rule as before ("the last one displaces the previous"), except the
        /// previous ones are no longer thrown away but wait their turn — otherwise the two
        /// dozen home tiles would have to be asked for strictly one at a time.
        ///
        /// The answer arrives through the Ready event to every subscriber at once, so there is
        /// no point asking again for something already being parsed.
        /// </summary>
        public void Request(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            bool start = false;
            lock (_lock)
            {
                if (_working.Contains(path)) return;
                _pending.Remove(path);
                _pending.Add(path);
                if (_busy < MaxWorkers) { _busy++; start = true; }
            }
            if (!start) return;

            Thread t = new Thread(Work);
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        void Work()
        {
            while (true)
            {
                string path;
                lock (_lock)
                {
                    if (_pending.Count == 0) { _busy--; return; }
                    int last = _pending.Count - 1;
                    path = _pending[last];
                    _pending.RemoveAt(last);
                    _working.Add(path);
                }

                try
                {
                    Arrangement a = Cached(path);
                    if (a == null)
                    {
                        a = Arrangement.Read(path);
                        lock (_lock)
                        {
                            if (!_cache.ContainsKey(path))
                            {
                                _cache[path] = a;
                                _order.Add(path);
                                while (_order.Count > CacheSize)
                                {
                                    _cache.Remove(_order[0]);
                                    _order.RemoveAt(0);
                                }
                            }
                        }
                    }
                    Fire(a);
                }
                finally
                {
                    // Clear the mark whatever happens: a parse that fell over would otherwise
                    // block any further request for that set forever.
                    lock (_lock) _working.Remove(path);
                }
            }
        }

        void Fire(Arrangement a)
        {
            if (Ready == null || _ui == null) return;
            try
            {
                if (!_ui.IsHandleCreated || _ui.IsDisposed) return;
                _ui.BeginInvoke((MethodInvoker)delegate
                {
                    if (Ready != null) Ready(a);
                });
            }
            catch { }
        }
    }
}
