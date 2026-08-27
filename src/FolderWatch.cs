using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Присматривает за корнями каталога и сообщает, когда там что-то изменилось, —
    /// чтобы новый сет появлялся в списке сам, без F5.
    ///
    /// Событие всегда с задержкой: одно сохранение из Live — это не одно изменение на
    /// диске, а пачка (временный файл, переименование, обновление папки), и сканировать
    /// по каждому значило бы гонять полный обход дерева по нескольку раз подряд.
    /// Отсчёт начинается заново от каждого нового события, поэтому во время экспорта или
    /// записи, когда диск пишется непрерывно, пересканирование просто ждёт тишины.
    /// </summary>
    public sealed class FolderWatch : IDisposable
    {
        readonly List<FileSystemWatcher> _watchers = new List<FileSystemWatcher>();
        readonly Timer _settle = new Timer();
        readonly ISynchronizeInvoke _sync;

        /// <summary>Уже в потоке интерфейса — за это отвечает SynchronizingObject.</summary>
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
        /// Переставить наблюдение на новый набор корней. Выключенные из сканирования
        /// пропускаем: их содержимое каталог всё равно не показывает, и будить его
        /// оттуда незачем.
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

                    // Два наблюдателя на корень вместо одного на всё подряд.
                    //
                    // Первый — только .als, зато вместе с изменением содержимого: сет
                    // пересохранили, у него другой темп и другие плагины, и каталог
                    // должен это увидеть.
                    //
                    // Второй — появление и исчезновение чего угодно: новая папка проекта,
                    // удалённый проект, свежий рендер рядом с сетом (от него зависит
                    // кнопка прослушивания). Изменение СОДЕРЖИМОГО здесь не слушаем —
                    // иначе каждый записанный сэмпл дёргал бы отсчёт, и во время работы
                    // в Live обновление не наступало бы вовсе.
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
            // Обработчики приходят в поток интерфейса сами — иначе таймер WinForms,
            // который они трогают, заводился бы из потока пула и не тикал бы вовсе.
            w.SynchronizingObject = _sync;
            w.Changed += OnAny;
            w.Created += OnAny;
            w.Deleted += OnAny;
            w.Renamed += OnAny;
            // Переполнение внутреннего буфера — это «событий было столько, что часть
            // потерялась», то есть тем более повод пересканировать.
            w.Error += delegate { Bump(); };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }

        /// <summary>
        /// Пробы помощника по восстановлению не будят каталог: он их всё равно не
        /// показывает (см. FolderScan.Find), а во время расследования они появляются и
        /// исчезают на каждой пробе — и каждый раз запускали бы полное пересканирование.
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
