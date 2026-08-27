using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager.Nebula
{
    static class NebulaProgram
    {
        [STAThread]
        static void Main()
        {
            // Diag.Start() здесь намеренно не зовётся: журнал один на обе программы
            // (%APPDATA%\Alive\alive.log), и он переписывается при каждом запуске —
            // Nebula затёрла бы отчёт Alive, который как раз и просят прислать.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e) { Report(e.Exception); };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Report(e.ExceptionObject as Exception);
            };

            CultureInfo en = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = en;
            Thread.CurrentThread.CurrentUICulture = en;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try { Application.Run(new NebulaForm()); }
            catch (Exception ex) { Report(ex); }
        }

        static int _told;

        static void Report(Exception ex)
        {
            if (Interlocked.Exchange(ref _told, 1) != 0) return;
            try
            {
                MessageBox.Show(ex == null ? "Unknown error." : ex.GetType().Name + ": " + ex.Message,
                                "Nebula", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }
}
