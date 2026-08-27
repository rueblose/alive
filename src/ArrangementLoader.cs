using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Читает аранжировку в фоне и держит несколько последних в памяти. Разбор сета —
    /// от 100 мс до секунды, поэтому в потоке интерфейса его делать нельзя: при листании
    /// списка стрелками окно бы застывало на каждой строке.
    /// </summary>
    public sealed class ArrangementLoader
    {
        /// <summary>
        /// Сколько разобранных аранжировок держим. Одна — около 0,4 МБ (замерено на
        /// реальной библиотеке), так что прежние 250 — это до сотни мегабайт памяти
        /// впустую: одновременно смотрят одну аранжировку в панели подробностей и одну
        /// на весь экран, а плитки главной живут своим кешем готовых картинок.
        /// </summary>
        const int CacheSize = 12;

        /// <summary>
        /// Сколько сетов разбираем одновременно. Разбор чисто процессорный (распаковка
        /// плюс XML), и на одном потоке два десятка плиток главной заполнялись больше
        /// трёх секунд. Больше трёх потоков брать не стоит: приоритет у них ниже
        /// обычного, но это всё равно фон, а не то, ради чего запускали программу.
        /// </summary>
        const int MaxWorkers = 3;

        readonly Control _ui;
        readonly object _lock = new object();
        readonly Dictionary<string, Arrangement> _cache = new Dictionary<string, Arrangement>();
        readonly List<string> _order = new List<string>();

        // Что ждёт разбора и что разбирается прямо сейчас.
        readonly List<string> _pending = new List<string>();
        readonly HashSet<string> _working = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int _busy;

        /// <summary>Приходит в потоке интерфейса.</summary>
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
        /// Запрашивает разбор. В работу очередь уходит с конца: самый свежий запрос —
        /// первым. Это то же правило, что и раньше («последний вытесняет предыдущий»),
        /// просто теперь предыдущие не выбрасываются, а ждут своей очереди — иначе
        /// плитки главной, которых два десятка, приходилось бы просить строго по одной.
        ///
        /// Ответ приходит событием Ready всем подписчикам сразу, поэтому повторно
        /// просить то, что уже разбирается, незачем.
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
                    // Снимаем отметку в любом случае: иначе сорвавшийся разбор навсегда
                    // заблокировал бы повторный запрос этого сета.
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
