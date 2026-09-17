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

            Settings st = Settings.Load();
            // "settings-folders" — the same dialog, but already in folder-scanning mode: it has
            // four more rows there, and that is exactly where the layout breaks.
            if (which == "settings-folders") st.PluginsFromFolders = true;
            Form f = new SettingsDialog(st);

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
