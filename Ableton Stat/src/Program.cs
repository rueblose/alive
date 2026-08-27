using System;
using System.Globalization;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            // Журнал заводим первым делом: часть падений случается ещё до того, как
            // появится хоть одно окно, и без файла о них снаружи не узнать ничего.
            Diag.Start();

            // Иначе WinForms показывает своё окно «Необработанное исключение» с кнопкой
            // «Продолжить», а пользователь на чужой машине жмёт «Продолжить» и получает
            // наполовину живую программу, про которую потом говорит «не работает».
            // Теперь любое исключение сперва уходит в журнал.
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate (object s, ThreadExceptionEventArgs e)
            {
                Report(e.Exception, false);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate (object s, UnhandledExceptionEventArgs e)
            {
                Report(e.ExceptionObject as Exception, true);
            };

            // Интерфейс у нас только английский (см. L.S), но текст системных исключений
            // .NET берёт из языка Windows — и в панель сета прилетало «Неправильное
            // магическое число в заголовке GZip», а в плеер такие же русские строки от
            // Media Foundation. Своими силами это не лечится: сообщения приходят готовыми
            // из ex.Message. Прибиваем язык сообщений рантайма к английскому.
            //
            // Default*Culture, а не только текущий поток: разбор сетов идёт в Parallel.For
            // и в своих потоках, а они наследуют язык не от нас, а от процесса.
            CultureInfo en = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = en;
            Thread.CurrentThread.CurrentUICulture = en;

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            try { Application.Run(new MainForm()); }
            catch (Exception ex) { Report(ex, true); }
        }

        // Весь интерфейс рисуется своими руками, поэтому одна ошибка в отрисовке
        // повторяется на каждом кадре. Окно с текстом показываем только первое —
        // остальное молча копится в журнале.
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
