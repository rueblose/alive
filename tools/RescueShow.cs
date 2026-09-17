using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// Opens the rescue window on a given set and does nothing else. Needed for the same reason
    /// Shot is needed: a compiled exe still says nothing about what got drawn, and the layout
    /// here is computed by hand. A shot is taken like this:
    ///
    ///     Shot.exe RescueShow.exe out.png "set.als"
    ///
    /// Build: tools\build-rescue-test.cmd. Does not go into bin.
    /// </summary>
    internal static class RescueShow
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                MessageBox.Show("usage: RescueShow.exe <set.als>");
                return 2;
            }

            Diag.Start();
            CultureInfo en = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = en;
            Thread.CurrentThread.CurrentUICulture = en;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            SetEntry set = new SetEntry();
            set.Path = args[0];
            set.Name = System.IO.Path.GetFileNameWithoutExtension(args[0]);
            // RescueSession reads Creator itself — a second pass over the file is not needed.

            using (RescueDialog d = new RescueDialog(set, PluginInventory.Load()))
            {
                d.StartPosition = FormStartPosition.CenterScreen;
                // The program's own dialog hides from the taskbar (ShowInTaskbar = false on
                // GlassDialog), and Process.MainWindowHandle does not find such a window —
                // leaving Shot.exe nothing to capture. Here the window is the only one, so
                // hiding it is pointless.
                d.ShowInTaskbar = true;
                d.Shown += delegate
                {
                    Diag.Line("form dpi=" + d.DeviceDpi + " client=" + d.ClientSize + " size=" + d.Size
                              + " autoscale=" + d.AutoScaleMode + "/" + d.AutoScaleDimensions);
                    foreach (Control c in d.Controls)
                        Diag.Line("  " + c.GetType().Name + " '" + c.Text + "' " + c.Bounds);
                };
                Application.Run(d);
            }
            return 0;
        }
    }
}
