using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Акриловый фон окон: то, что за окном, размывается и просвечивает сквозь него.
    /// Работает это так: DWM показывает размытие ровно там, где в пикселях окна альфа
    /// меньше единицы, — значит фон надо не «залить полупрозрачным поверх», а именно
    /// записать в буфер вместе с альфой (см. <see cref="Chrome.PaintBase"/>).
    ///
    /// Проверено замером на этой машине: GDI-текст (TextRenderer, а им нарисован весь
    /// интерфейс) поверх такой поверхности остаётся полностью непрозрачным и чётким —
    /// поэтому читаемость не страдает. А вот GDI+ DrawString наоборот становится
    /// полупрозрачным, так что текст рисовать только через TextRenderer.
    /// </summary>
    public static class Glass
    {
        /// <summary>
        /// Держим стекло за одним выключателем: если оно когда-нибудь надоест или
        /// начнёт мешать, достаточно поставить здесь false — окна станут плоскими,
        /// как раньше, и больше нигде править не придётся.
        /// </summary>
        public static bool Enabled = Supported();

        static Icon _appIcon;

        /// <summary>
        /// Иконка приложения (src\icons\icon256.ico, вшита в exe через /win32icon в
        /// build.cmd). Form.Icon сам её не подхватывает: если не задать иконку явно,
        /// WinForms рисует в заголовке и панели задач свою собственную заглушку —
        /// это её embedding в exe не меняет, экземпляр окна должен получить иконку
        /// сам. ExtractAssociatedIcon читает уже вшитый в исполняемый файл ресурс,
        /// так что таскать .ico рядом с exe отдельным файлом не нужно.
        /// </summary>
        public static Icon AppIcon
        {
            get
            {
                if (_appIcon == null)
                {
                    try { _appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); }
                    catch { }
                }
                return _appIcon;
            }
        }

        const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        const int DWMWA_DISALLOW_PEEK = 11;
        const int DWMWA_EXCLUDED_FROM_PEEK = 12;
        const int DWMSBT_TRANSIENTWINDOW = 3;
        const int DWMWCP_DONOTROUND = 1;
        const int DWMWCP_ROUND = 2;

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct Margins { public int L, R, T, B; }

        [DllImport("dwmapi.dll")]
        static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins m);

        [StructLayout(LayoutKind.Sequential)]
        struct AccentPolicy { public int State, Flags, GradientColor, AnimationId; }

        [StructLayout(LayoutKind.Sequential)]
        struct CompositionAttribData { public int Attribute; public IntPtr Data; public int Size; }

        [DllImport("user32.dll")]
        static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref CompositionAttribData data);

        /// <summary>
        /// Тонировка самого акрилового слоя — она ложится ПОД содержимое окна, и это
        /// важнее, чем кажется: сглаженные края GDI-текста наследуют альфу фона и
        /// смешиваются как раз с этим слоем. Пока он тёмный и нейтральный, текст остаётся
        /// серым даже на ярких обоях; если гасить не здесь, а плотностью фона окна,
        /// размытие пропадает раньше, чем текст перестаёт розоветь. Байты — AABBGGRR.
        /// </summary>
        public static int AccentTint = unchecked((int)0xf01D1B1B);

        /// <summary>Умеет ли система акрил вообще — в отличие от Enabled, это про ОС,
        /// а не про выбор пользователя. Нужно окну настроек: там, где стекла нет и быть
        /// не может, предлагать перезапуск ради него бессмысленно.</summary>
        public static bool Supported()
        {
            // Манифест объявляет поддержку Windows 10, поэтому номер сборки здесь честный.
            Version v = Environment.OSVersion.Version;
            return v.Major > 10 || (v.Major == 10 && v.Build >= 17763);
        }

        /// <summary>
        /// Включить стекло и скруглённые углы для окна. Звать сразу после создания хендла,
        /// а не по показу: иначе первый кадр успевает нарисоваться непрозрачным и видно
        /// вспышку.
        /// </summary>
        public static void Apply(Form f)
        {
            ApplyChrome(f);
            if (!f.IsHandleCreated || !Enabled) return;
            try
            {
                int disallow = 1;
                DwmSetWindowAttribute(f.Handle, DWMWA_DISALLOW_PEEK, ref disallow, 4);
                DwmSetWindowAttribute(f.Handle, DWMWA_EXCLUDED_FROM_PEEK, ref disallow, 4);
                ApplyBackdrop(f.Handle);
            }
            catch { Enabled = false; }
        }

        /// <summary>
        /// Только тёмный заголовок и скруглённые углы, без самого стекла. Нужно
        /// окнам, где стекло сознательно отключено (см. GlassDialog.UseGlass) —
        /// замерено, что DwmExtendFrameIntoClientArea делает нативные дочерние HWND
        /// (например TextBox) непредсказуемо подмешанными с тем, что физически за
        /// окном, причём это свойство ВСЕГО окна целиком, а не конкретного контрола:
        /// точечно исключить одно поле нельзя, только выключить стекло у окна, в
        /// котором оно живёт.
        /// </summary>
        public static void ApplyChrome(Form f)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int dark = 1; DwmSetWindowAttribute(f.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, 4);
            }
            catch { }
            SetCornerRounding(f, true);
        }

        /// <summary>
        /// Скруглять углы или нет — отдельно от остального хрома, потому что на
        /// развёрнутом окне их нельзя оставлять включёнными: рамка окна в точности
        /// совпадает с границами экрана (замерено — GetWindowRect равен Screen.Bounds),
        /// но DWM всё равно рисует скругление поверх, и в уголках сквозь него виден
        /// рабочий стол — тот самый зазор. Значит скругление должно гаситься именно
        /// на переходе в Maximized, а не «убрать рамку», рамки тут и так нет.
        /// </summary>
        public static void SetCornerRounding(Form f, bool round)
        {
            if (!f.IsHandleCreated) return;
            try
            {
                int pref = round ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                DwmSetWindowAttribute(f.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, 4);
            }
            catch { }
        }

        static void ApplyBackdrop(IntPtr h)
        {
            // Заметь: DwmExtendFrameIntoClientArea здесь НЕ зовётся. Акрилу через
            // SetWindowCompositionAttribute расширение рамки не нужно — он смотрит
            // прямо на альфу в пикселях окна (первая же проба работала без него).
            // А вот вреда от него много: с «листом стекла» вся клиентская область
            // считается прозрачной по умолчанию, и нативные дочерние окна (тот же
            // TextBox), которые рисуются обычным GDI и альфу не трогают, остаются с
            // нулевой альфой — то есть дырявыми. Отсюда и брались цветные пятна внутри
            // полей ввода, тянущиеся за обоями. Без расширения рамки непрозрачно всё,
            // что мы явно не сделали прозрачным.
            if (!ApplyAccent(h))
            {
                // Запасной путь. Он рабочий, но хуже: замерено, что системный
                // backdrop сплошь и рядом не берёт то, что под окном, и отдаёт
                // ровную серую заглушку — одинаковую и над красным, и над синим.
                // Ему рамку расширить всё-таки надо, иначе он не проявится вовсе.
                Margins m = new Margins();
                m.L = m.R = m.T = m.B = -1;
                DwmExtendFrameIntoClientArea(h, ref m);

                int backdrop = DWMSBT_TRANSIENTWINDOW;
                DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
            }
        }

        /// <summary>
        /// Тот самый акрил, который просвечивает всегда, а не по настроению системы.
        /// API недокументированное, зато единственное, что реально размывает и обои,
        /// и окно под нашим — а это ровно то, что нужно диалогам поверх главного окна.
        /// </summary>
        static bool ApplyAccent(IntPtr h)
        {
            AccentPolicy ap = new AccentPolicy();
            ap.State = 4;                  // ACCENT_ENABLE_ACRYLICBLURBEHIND
            ap.GradientColor = AccentTint;

            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(ap, mem, false);
                CompositionAttribData d = new CompositionAttribData();
                d.Attribute = 19;          // WCA_ACCENT_POLICY
                d.Data = mem;
                d.Size = size;
                return SetWindowCompositionAttribute(h, ref d) != 0;
            }
            finally { Marshal.FreeHGlobal(mem); }
        }
    }
}
