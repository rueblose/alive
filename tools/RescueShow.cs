using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// Открывает окно восстановления на заданном сете и больше ничего не делает. Нужен
    /// затем же, зачем Reel'у нужен Shot: собранный exe ещё ничего не говорит о том, что
    /// нарисовалось, а раскладка тут посчитана руками. Снимок снимается так:
    ///
    ///     Shot.exe RescueShow.exe out.png "сет.als"
    ///
    /// Сборка: tools\build-rescue-test.cmd. В bin не попадает.
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
            // Creator дочитывает сам RescueSession — второй проход по файлу не нужен.

            using (RescueDialog d = new RescueDialog(set, PluginInventory.Load()))
            {
                d.StartPosition = FormStartPosition.CenterScreen;
                // Диалог программы прячется из панели задач (ShowInTaskbar = false у
                // GlassDialog), а Process.MainWindowHandle такое окно не находит — и
                // Shot.exe снимать нечего. Здесь окно единственное, прятать его незачем.
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
