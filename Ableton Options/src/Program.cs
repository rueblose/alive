using System;
using System.Windows.Forms;

namespace AbletonOptions
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            L.Load();
            Backdrop.Capture();   // снимок рабочего стола делаем до показа окна

            Application.Run(new MainForm());
        }
    }
}
