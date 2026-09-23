using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// Opens one of the program's windows and does nothing else — so it can be captured without
    /// sitting at the machine. The same trick as RescueShow, only for several windows at once:
    ///
    ///     Shot.exe DialogShow.exe out.png settings
    ///     Shot.exe DialogShow.exe out.png settings-folders
    ///     Shot.exe DialogShow.exe out.png export  "set.als"
    ///     Shot.exe DialogShow.exe out.png preview "set.als"
    ///     Shot.exe DialogShow.exe out.png samples     (the folders window on Samples)
    ///     Shot.exe DialogShow.exe out.png folders     (the folders window on Projects)
    ///     Shot.exe DialogShow.exe out.png player  "set.als"
    ///
    /// Build: tools\build-rescue-test.cmd. Does not go into bin.
    /// </summary>
    internal static class DialogShow
    {
        [STAThread]
        static int Main(string[] args)
        {
            string which = args.Length > 0 ? args[0].ToLowerInvariant() : "settings";

            Diag.Start();
            CultureInfo en = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = en;
            Thread.CurrentThread.CurrentUICulture = en;

            if (Settings.Load().DisableGlass) Glass.Enabled = false;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Form f;
            if (which == "export" || which == "preview")
            {
                // Both dialogs want nothing from the catalog but a name and a path — the set
                // is read from disk on the spot.
                SetEntry set = new SetEntry();
                set.Path = args[1];
                set.Name = System.IO.Path.GetFileNameWithoutExtension(args[1]);
                // The preview prints the key next to the tempo, and that one field is the only
                // thing it wants from the catalog.
                AlsInfo info = AlsFile.Read(args[1]);
                if (info != null) set.Key = info.Key;

                if (which == "export")
                {
                    f = new CollectDialog(set, LiveEnvironment.Detect(), Settings.Load());
                }
                else
                {
                    // The loader answers on the thread of the control it was given. The dialog
                    // itself cannot be that control - it does not exist yet - so a hidden form
                    // holds the handle, and Application.Run below pumps its messages.
                    Form host = new Form();
                    host.CreateControl();
                    IntPtr unused = host.Handle;
                    f = new PreviewDialog(set, new ArrangementLoader(host));
                }
            }
            else if (which == "player")
            {
                // The render player with the set's first render loaded but silent - the wave is
                // what is being looked at, and a shot should not play music at somebody.
                SetEntry set = new SetEntry();
                set.Path = args[1];
                set.Name = System.IO.Path.GetFileNameWithoutExtension(args[1]);
                PlayerDialog p = new PlayerDialog();
                p.Shown += delegate { p.LoadSet(set, null, -1, false); };
                f = p;
            }
            else if (which == "samples" || which == "folders")
            {
                // The folders window as the program opens it: both tabs, on Samples ("samples",
                // with Live's Places behind From Live) or on Projects ("folders").
                Settings sst = Settings.Load();
                RootsDialog d = new RootsDialog(
                    new RootsDialog.Page(RootsDialog.Projects, sst.Roots, sst.DisabledRoots),
                    new RootsDialog.Page(RootsDialog.Samples(LiveEnvironment.Detect(), sst.Roots),
                                         sst.SampleRoots, sst.DisabledSampleRoots));
                d.Tab = which == "samples" ? 1 : 0;
                f = d;
            }
            else
            {
                Settings st = Settings.Load();
                // "settings-folders" — the same dialog, but already in folder-scanning mode: it
                // has four more rows there, and that is exactly where the layout breaks.
                if (which == "settings-folders") st.PluginsFromFolders = true;
                f = new SettingsDialog(st);
            }

            // "-flash" — keep the outline highlight lit so it shows up in the shot: on its own
            // it fades within a second (Chrome.SwallowBlockedClick lights it on every click
            // outside the modal window instead of the system beep).
            if (args.Length > 1 && args[1] == "-flash")
            {
                GlassDialog gd = f as GlassDialog;
                if (gd != null)
                {
                    System.Windows.Forms.Timer t = new System.Windows.Forms.Timer();
                    t.Interval = 200;
                    t.Tick += delegate { gd.Flash(); };
                    gd.Shown += delegate { t.Start(); };
                }
            }

            f.StartPosition = FormStartPosition.CenterScreen;
            // GlassDialog hides from the taskbar, and Process.MainWindowHandle does not find
            // such a window — leaving Shot.exe nothing to capture. Here the window is the only
            // one.
            f.ShowInTaskbar = true;
            Application.Run(f);
            return 0;
        }
    }
}
