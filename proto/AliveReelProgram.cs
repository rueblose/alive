using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace Reel
{
    /// <summary>
    /// Одно приложение: главное окно Alive плюс версии проекта (Forks) и облако точек
    /// по всей библиотеке (Stat).
    ///
    /// Исходный Alive при этом не тронут ни одной строкой — он целиком собирается в
    /// этот же exe как есть, а оба окна прицепляются снаружи: Forks через открытое
    /// событие DetailPanel.ForksRequested (без подписчика кнопки в инспекторе просто
    /// нет, и Alive.exe остаётся прежним), Stat — кнопкой в Controls готового окна.
    /// Нужный сет спрашивается у его же таблицы через открытые свойства
    /// (RowListView.Selected -> SetEntry.Path). Никакой рефлексии по приватным полям:
    /// то, что нельзя спросить публично, тут и не спрашивается.
    ///
    /// Когда версии переедут внутрь Alive по-настоящему, всё это заменится третьей
    /// вкладкой в _mode рядом с Sets и Plugins, и файл исчезнет.
    /// </summary>
    internal static class AliveReelProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Порядок preamble повторяет Program.Main самого Alive: журнал заводится
            // первым (часть падений случается до появления окна), язык сообщений
            // рантайма прибивается к английскому, а Glass.Enabled читается ДО первого
            // касания Theme — половина его цветов вычисляется один раз, как раз по нему.
            Diag.Start();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, ThreadExceptionEventArgs e)
            {
                Report(e.Exception, false);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Report(e.ExceptionObject as Exception, true);
            };

            CultureInfo en = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = en;
            Thread.CurrentThread.CurrentUICulture = en;

            if (Settings.Load().DisableGlass) Glass.Enabled = false;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // --reel [сет.als] — только окно версий, без каталога. Так его удобно
            // открывать «этим» из проводника и так его снимает проверочный Shot.exe.
            bool reelOnly = false;
            string startPath = null;

            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--reel") reelOnly = true;
                else if (startPath == null && args[i].EndsWith(".als", StringComparison.OrdinalIgnoreCase))
                    startPath = args[i];
            }

            try
            {
                if (reelOnly)
                {
                    ReelWindow w = new ReelWindow(startPath);
                    w.StartPosition = FormStartPosition.CenterScreen;
                    w.ShowInTaskbar = true;
                    Application.Run(w);
                }
                else
                {
                    MainForm main = new MainForm();
                    AttachHistory(main, startPath);
                    AttachStat(main);
                    Application.Run(main);
                }
            }
            catch (Exception ex) { Report(ex, true); }
        }

        // ------------------------------------------------------------------ прицеп

        static ReelWindow _window;

        /// <summary>
        /// Кнопка «Forks» в инспекторе проекта и Ctrl+H к ней.
        ///
        /// В шапке окна ей было не место: шапка — про весь каталог (поиск, фильтры,
        /// папки), а версии всегда про один конкретный сет, и спрашивать их логично
        /// там же, где Rescue Project, — в панели справа, прямо над ним.
        ///
        /// Сам Alive при этом по-прежнему не тронут ни одной строкой: DetailPanel
        /// показывает кнопку, только если на ForksRequested кто-то подписался, а
        /// подписываемся здесь.
        /// </summary>
        static void AttachHistory(Form main, string startPath)
        {
            foreach (Control c in main.Controls)
            {
                DetailPanel panel = c as DetailPanel;
                if (panel == null) continue;
                panel.ForksRequested += delegate { OpenHistory(main, null); };
            }

            main.Shown += delegate
            {
                if (!string.IsNullOrEmpty(startPath) && File.Exists(startPath))
                    OpenHistory(main, startPath);
            };

            // Клавиша ловится фильтром сообщений, а не KeyDown окна: у MainForm свой
            // разбор клавиш в ProcessCmdKey, влезать в него снаружи нечем.
            Application.AddMessageFilter(new HistoryHotkey(main));
        }

        // ------------------------------------------------------------------ Stat

        static Form _stat;

        /// <summary>
        /// Кнопка «Stat» в панели инструментов — облако точек по всей библиотеке
        /// (nebula\): каждый проект точка, шесть её свойств несут шесть величин сета.
        ///
        /// Место освободилось из-под History, и оно тут по делу: это вид на весь
        /// каталог целиком, ровно как поиск и фильтры рядом, — в отличие от версий,
        /// которые всегда про один сет и потому уехали в инспектор.
        ///
        /// Раскладка та же, что была у History: LayoutAll расставляет свои контролы, а
        /// между полем поиска и панелью справа лежит полоса под счётчиком «N shown» —
        /// счётчик прижат к левому её краю, и правый край свободен на любой ширине
        /// окна. Считаем его теми же Theme.Pad и Theme.PanelW, что и сам LayoutAll,
        /// поэтому кнопка едет вместе с ним.
        /// </summary>
        static void AttachStat(Form main)
        {
            GlassButton stat = new GlassButton();
            stat.Text = L.S("Stat", "Статистика");
            stat.Font = Theme.FButton;
            stat.FitToText(16);
            stat.Click += delegate { OpenStat(main); };
            main.Controls.Add(stat);
            stat.BringToFront();

            EventHandler place = delegate
            {
                float k = main.DeviceDpi / 96f;
                int pad = (int)Math.Round(Theme.Pad * k);
                int h = (int)Math.Round(Theme.ControlH * k);
                int gap = (int)Math.Round(16 * k);
                int panelX = main.ClientSize.Width - pad - (int)Math.Round(Theme.PanelW * k);

                stat.SetBounds(panelX - gap - stat.Width, pad, stat.Width, h);
                stat.BringToFront();
            };

            main.Resize += place;
            main.Shown += delegate { place(main, EventArgs.Empty); };
        }

        static void OpenStat(Form owner)
        {
            if (_stat != null && !_stat.IsDisposed)
            {
                if (_stat.WindowState == FormWindowState.Minimized)
                    _stat.WindowState = FormWindowState.Normal;
                _stat.BringToFront();
                _stat.Focus();
                return;
            }

            _stat = new AbletonManager.Nebula.NebulaForm();
            _stat.FormClosed += delegate { _stat = null; };
            _stat.Show(owner);
        }

        internal static void OpenHistory(Form owner, string path)
        {
            if (path == null) path = SelectedSet(owner);

            if (_window != null && !_window.IsDisposed)
            {
                if (path != null) _window.Open(path);
                if (_window.WindowState == FormWindowState.Minimized)
                    _window.WindowState = FormWindowState.Normal;
                _window.BringToFront();
                _window.Focus();
                return;
            }

            _window = new ReelWindow(path);
            _window.FormClosed += delegate { _window = null; };
            _window.Show(owner);
        }

        /// <summary>
        /// Какой сет сейчас выбран в каталоге. Спрашиваем у таблицы по открытому API:
        /// RowListView и SetEntry — публичные классы, Selected и Path — публичные
        /// свойства. Не нашли — вернём null, и история откроет последний свой сет.
        /// </summary>
        static string SelectedSet(Form owner)
        {
            if (owner == null) return null;
            foreach (Control c in owner.Controls)
            {
                RowListView list = c as RowListView;
                if (list == null || !list.Visible) continue;

                RowData row = list.Selected;
                SetEntry set = row != null ? row.Tag as SetEntry : null;
                if (set != null && !string.IsNullOrEmpty(set.Path) && File.Exists(set.Path))
                    return set.Path;
            }
            return null;
        }

        sealed class HistoryHotkey : IMessageFilter
        {
            const int WM_KEYDOWN = 0x0100;
            readonly Form _main;

            public HistoryHotkey(Form main) { _main = main; }

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_KEYDOWN) return false;
                if ((Keys)m.WParam.ToInt32() != Keys.H) return false;
                if ((Control.ModifierKeys & Keys.Control) == 0) return false;
                if (Control.ModifierKeys != Keys.Control) return false;   // без Shift/Alt

                // В поле ввода Ctrl+H — это «удалить символ слева», отбирать её нельзя.
                Control focused = FromHandleDeep(m.HWnd);
                if (focused is TextBox) return false;

                OpenHistory(_main, null);
                return true;
            }

            static Control FromHandleDeep(IntPtr h)
            {
                return Control.FromChildHandle(h) ?? Control.FromHandle(h);
            }
        }

        // ------------------------------------------------------------------ падения

        static int _told;

        static void Report(Exception ex, bool fatal)
        {
            Diag.Fail(fatal ? "fatal" : "ui thread", ex);

            if (Interlocked.Exchange(ref _told, 1) == 0)
            {
                try
                {
                    MessageBox.Show(
                        (ex == null ? "Unknown error." : ex.GetType().Name + ": " + ex.Message)
                        + "\r\n\r\nDetails were written to:\r\n" + Diag.LogPath
                        + "\r\n\r\nSend that file over and it will be fixed.",
                        "Alive", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                catch { }
            }

            if (fatal) Environment.Exit(1);
        }
    }
}
