using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// Открывает одно из окон программы и больше ничего не делает — чтобы его можно
    /// было снять, не сидя за машиной. Тот же приём, что и у RescueShow, только на
    /// несколько окон сразу:
    ///
    ///     Shot.exe DialogShow.exe out.png settings
    ///     Shot.exe DialogShow.exe out.png options
    ///
    /// Сборка: tools\build-rescue-test.cmd. В bin не попадает.
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
            if (which == "options") f = new OptionsDialog();
            else
            {
                Settings st = Settings.Load();
                // «settings-folders» — тот же диалог, но сразу в режиме обхода папок:
                // там на четыре строки больше, и разъезжается раскладка именно в нём.
                if (which == "settings-folders") st.PluginsFromFolders = true;
                f = new SettingsDialog(st);
            }

            // «-flash» — держать подсветку обводки зажжённой, чтобы её было видно на
            // снимке: сама по себе она гаснет за секунду (Chrome.SwallowBlockedClick
            // зажигает её на каждый клик мимо модального окна вместо системного звука).
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
            // GlassDialog прячется из панели задач, а Process.MainWindowHandle такое
            // окно не находит — Shot.exe нечего снимать. Здесь окно единственное.
            f.ShowInTaskbar = true;
            Application.Run(f);
            return 0;
        }
    }
}
