using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The acrylic window background: whatever is behind the window is blurred and shows
    /// through it. It works like this: DWM shows the blur exactly where the alpha in the
    /// window's pixels is below one — which means the background must not be "filled
    /// translucently on top" but written into the buffer together with its alpha (see <see
    /// cref="Chrome.PaintBase"/>).
    ///
    /// Verified by measurement on this machine: GDI text (TextRenderer, which the whole
    /// interface is drawn with) stays fully opaque and crisp over such a surface — so
    /// legibility does not suffer. GDI+ DrawString, on the other hand, becomes translucent, so
    /// text is only ever drawn through TextRenderer.
    /// </summary>
    public static class Glass
    {
        /// <summary>
        /// The glass sits behind a single switch: should it ever become tiresome or start
        /// getting in the way, setting false here is enough — the windows go flat, as they used
        /// to be, and nothing else needs editing.
        /// </summary>
        public static bool Enabled = Supported();

        static Icon _appIcon;

        /// <summary>
        /// The application icon (src\icons\icon256.ico, baked into the exe via /win32icon in
        /// build.cmd). Form.Icon does not pick it up by itself: without an explicitly set icon,
        /// WinForms draws its own placeholder in the title bar and the taskbar — embedding it
        /// in the exe does not change that, the window instance has to be given the icon
        /// itself. ExtractAssociatedIcon reads the resource already baked into the executable,
        /// so there is no need to carry a separate .ico next to the exe.
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
        /// The tint of the acrylic layer itself — it goes UNDER the window's content, and that
        /// matters more than it seems: the antialiased edges of GDI text inherit the
        /// background's alpha and blend with exactly this layer. While it stays dark and
        /// neutral, text stays grey even over bright wallpaper; dimming through the window
        /// background's density instead makes the blur disappear before the text stops going
        /// pink. The bytes are AABBGGRR.
        /// </summary>
        public static int AccentTint = unchecked((int)0xf01D1B1B);

        /// <summary>Whether the system can do acrylic at all — unlike Enabled, this is about
        /// the OS rather than the user's choice. The settings window needs it: where glass
        /// neither is nor can be, offering a restart for its sake is pointless.</summary>
        public static bool Supported()
        {
            // The manifest declares Windows 10 support, so the build number here is honest.
            Version v = Environment.OSVersion.Version;
            return v.Major > 10 || (v.Major == 10 && v.Build >= 17763);
        }

        /// <summary>
        /// Turn on glass and rounded corners for a window. Call it right after the handle is
        /// created rather than on show: otherwise the first frame gets drawn opaque and the
        /// flash is visible.
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
        /// Only the dark title bar and rounded corners, without the glass itself. Needed by
        /// windows where glass is deliberately off (see GlassDialog.UseGlass) — it was measured
        /// that DwmExtendFrameIntoClientArea makes native child HWNDs (a TextBox, for instance)
        /// unpredictably blended with whatever is physically behind the window, and that this
        /// is a property of the WHOLE window rather than of a particular control: one field
        /// cannot be excluded on its own, only the glass of the window it lives in can be
        /// switched off.
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
        /// Whether to round the corners — kept apart from the rest of the chrome, because on a
        /// maximized window they must not stay on: the window frame matches the screen bounds
        /// exactly (measured — GetWindowRect equals Screen.Bounds), yet DWM still draws the
        /// rounding over it, and the desktop shows through in the corners — that very gap. So
        /// the rounding has to be killed precisely on the transition to Maximized, not by
        /// "removing the frame" — there is no frame here as it is.
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
            // Note: DwmExtendFrameIntoClientArea is NOT called here. Acrylic through
            // SetWindowCompositionAttribute needs no frame extension — it looks straight at the
            // alpha in the window's pixels (the very first attempt worked without it). It does
            // plenty of harm, though: with a "sheet of glass" the whole client area counts as
            // transparent by default, and native child windows (that same TextBox), which draw
            // with plain GDI and never touch alpha, are left at zero alpha — that is, full of
            // holes. That is where the coloured patches inside the input fields came from,
            // trailing the wallpaper behind them. Without the frame extension, everything we
            // have not explicitly made transparent stays opaque.
            if (!ApplyAccent(h))
            {
                // The fallback. It works, but worse: it was measured that the system backdrop
                // routinely fails to take what is under the window and returns a flat grey
                // placeholder instead — the same one over red and over blue. It does need the
                // frame extended, though, or it does not show up at all.
                Margins m = new Margins();
                m.L = m.R = m.T = m.B = -1;
                DwmExtendFrameIntoClientArea(h, ref m);

                int backdrop = DWMSBT_TRANSIENTWINDOW;
                DwmSetWindowAttribute(h, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, 4);
            }
        }

        /// <summary>
        /// The acrylic that shows through always, rather than at the system's whim. The API is
        /// undocumented, but it is the only one that really blurs both the wallpaper and the
        /// window beneath ours — which is exactly what dialogs over the main window need.
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
