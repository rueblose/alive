using System;
using System.IO;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace Alive
{
    /// <summary>
    /// Application entry point: the Alive main window plus Stat, the scatter plot over the
    /// whole library.
    ///
    /// Stat attaches to a finished MainForm from the outside, through public events and
    /// properties (RowListView.Selected -> SetEntry.Path), rather than by editing the catalog
    /// itself. No reflection over private fields: what cannot be asked for publicly is not
    /// asked for here either.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            // Before anything else, and before the log in particular: Diag.Start() truncates
            // alive.log, and a second copy doing that would wipe the running one's log — the
            // very file we ask people to send when something goes wrong.
            if (!SingleInstance.Claim())
            {
                SingleInstance.HandOver(args);
                return;
            }

            // Order of the preamble matters: the log is opened first (some crashes happen
            // before any window exists), the runtime's message language is pinned to English,
            // and Glass.Enabled is read BEFORE Theme is first touched — half of its colours
            // are computed once, off exactly that flag.
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

            // The rescue helper's probe copies live inside other people's project folders and
            // have to disappear the moment the probe is over. If the last run was killed
            // halfway, they are still there — clear them before the first scan, so the catalog
            // never even sees them.
            RescueProbe.CleanupStale();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try
            {
                MainForm main = new MainForm();
                AttachStat(main);
                main.OpenPaths(args);      // queued until the catalog is on its feet
                Application.Run(main);
            }
            catch (Exception ex) { Report(ex, true); }
        }

        // ------------------------------------------------------------------ Stat

        static Form _stat;

        /// <summary>
        /// The "Stat" button in the toolbar — a point cloud over the whole library (nebula\):
        /// every project is a dot, and six of its properties carry six values of the set.
        ///
        /// It belongs there: this is a view of the entire catalog, exactly like the search and
        /// the filters next to it.
        ///
        /// LayoutAll places its own controls, and between the search field and the panel on the
        /// right lies the strip holding the "N shown" counter — the counter is pushed against
        /// its left edge, so the right edge is free at any window width. We compute it from the
        /// same Theme.Pad and Theme.PanelW that LayoutAll uses, so the button travels with it.
        /// </summary>
        static void AttachStat(Form main)
        {
            IconButton stat = new IconButton();
            stat.Icon = Glyph.Nebula;
            stat.Click += delegate { OpenStat(main); };
            main.Controls.Add(stat);
            stat.BringToFront();

            EventHandler place = delegate
            {
                float k = main.DeviceDpi / 96f;
                int pad = (int)Math.Round(Theme.Pad * k);
                int icon = (int)Math.Round(Theme.IconSize * k);
                int panelW = (int)Math.Round(Theme.PanelW * k);
                int panelX = main.ClientSize.Width - pad - panelW;

                stat.SetBounds(panelX, pad, icon, icon);
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

            AbletonManager.Nebula.NebulaForm nf = new AbletonManager.Nebula.NebulaForm();
            string selPath = SelectedSet(owner);
            if (!string.IsNullOrEmpty(selPath))
                nf.InitialPath = selPath;

            nf.ShowInListRequested += delegate (string path)
            {
                MainForm mf = owner as MainForm;
                if (mf != null && !string.IsNullOrEmpty(path))
                {
                    mf.SelectSetByPath(path);
                }
            };

            _stat = nf;
            _stat.FormClosed += delegate { _stat = null; };
            if (owner != null)
                owner.FormClosed += delegate { if (_stat != null && !_stat.IsDisposed) _stat.Close(); };
            _stat.Show();
        }

        /// <summary>
        /// Which set is selected in the catalog right now. We ask the table over its public API:
        /// RowListView and SetEntry are public classes, Selected and Path are public properties.
        /// Nothing found — return null, and Stat opens without a starting point.
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

        // ------------------------------------------------------------------ crashes

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
