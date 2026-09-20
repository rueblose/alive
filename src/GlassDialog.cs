using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>A borderless dialog window — the same background and the same header as the
    /// main window.</summary>
    public class GlassDialog : Form, IAnimatable
    {
        protected readonly IconButton CloseBtn = new IconButton();
        protected Rectangle Card;
        protected string Caption = "";

        /// <summary>
        /// Overridden to false on dialogs with input fields: real glass on a window breaks
        /// their child TextBoxes with an unpredictable blend of the actual background
        /// (measured, see Glass.ApplyChrome). The window stays dark and round-cornered — just
        /// not translucent.
        /// </summary>
        public virtual bool UseGlass { get { return true; } }

        public GlassDialog()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            CloseBtn.Icon = Glyph.Close;
            CloseBtn.Danger = true;
            CloseBtn.Click += delegate { Close(); };
            Controls.Add(CloseBtn);
        }

        protected int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        const int CS_DROPSHADOW  = 0x00020000;   // class style
        const int WS_MINIMIZEBOX = 0x00020000;   // window style — the same number, a different field

        /// <summary>
        /// WS_MINIMIZEBOX for the same reason as in MainForm: the shell reads it to decide
        /// whether a click on the taskbar button may minimise the window. Most dialogs here
        /// keep out of the taskbar altogether and never notice, but the player window is in it
        /// (ShowInTaskbar) and behaved differently from every other program because of this.
        /// </summary>
        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= CS_DROPSHADOW;
                cp.Style |= WS_MINIMIZEBOX;
                return cp;
            }
        }

        /// <summary>Glass goes on straight through the handle, before the first frame, so
        /// nothing flickers.</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (UseGlass) Glass.Apply(this); else Glass.ApplyChrome(this);
        }

        /// <summary>And once more on show: a backdrop set before the window appears is
        /// sometimes not picked up by DWM until the window's visual is recreated.</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { if (UseGlass) Glass.Apply(this); else Glass.ApplyChrome(this); }
        }

        // The window appears over 160 ms, rising by Sc(10) as the transparency fades out. The
        // dialog used to pop into existence — the one place where the interface jerked. There
        // is deliberately no scaling: changing Size means recomputing the children's layout on
        // every frame, and that is not animation but flicker.
        System.Windows.Forms.Timer _enter;
        int _enterStart, _enterTop;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Invalidate(true);
            StartEnterAnimation();
        }

        void StartEnterAnimation()
        {
            if (_enter != null) return;

            _enterTop = Top;
            int lift = Sc(10);
            Top = _enterTop + lift;
            Opacity = 0.0;
            _enterStart = Environment.TickCount;

            _enter = new System.Windows.Forms.Timer();
            _enter.Interval = 15;
            _enter.Tick += delegate
            {
                if (IsDisposed) { _enter.Stop(); return; }

                float t = (Environment.TickCount - _enterStart) / 160f;
                if (t >= 1f)
                {
                    Opacity = 1.0;
                    Top = _enterTop;
                    _enter.Stop();
                    _enter.Dispose();
                    _enter = null;
                    return;
                }
                float k = 1f - (float)Math.Pow(1f - t, 3);   // ease-out
                Opacity = k;
                Top = _enterTop + (int)Math.Round(lift * (1f - k));
            };
            _enter.Start();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Card = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            int icon = Sc(Theme.IconSize);
            CloseBtn.SetBounds(Card.Right - Sc(Theme.Pad) - icon, Card.Top + Sc(Theme.Pad) - Sc(6), icon, icon);
            Invalidate(true);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        // ------------------------------------------------------------------ highlight

        float _flash;

        /// <summary>
        /// Flash the outline: "the window is here, and it is what stops you clicking further".
        /// The answer to a click outside a modal window instead of the system beep — see
        /// Chrome.SwallowBlockedClick. While the button is held the message arrives dozens of
        /// times; flaring up again on each is not wanted, so we restart only once the highlight
        /// has already faded.
        /// </summary>
        public void Flash()
        {
            if (_flash > 0.9f) return;
            _flash = 1f;
            AnimEngine.Register(this);
        }

        public bool OnAnimTick()
        {
            if (IsDisposed) return false;
            // ~0.4 s to full fade: shorter and the eye does not catch it, longer and the
            // highlight starts to look like a state of the window rather than an answer to a
            // click.
            _flash -= 0.04f;
            if (_flash < 0.02f) _flash = 0f;
            Invalidate();
            return _flash > 0f;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Chrome.PaintBase(this, g, ClientRectangle, UseGlass ? Theme.Backdrop : Theme.Bg);
            Theme.Smooth(g);

            if (_flash > 0f)
            {
                // The radius is the one DWM rounds the window with, or the outline pulls away
                // from the corner. Thickness of two pixels: a single-pixel one is barely
                // visible on a curve.
                float w = Sc(2);
                using (GraphicsPath path = Theme.Round(
                           new RectangleF(w / 2f, w / 2f, ClientSize.Width - w, ClientSize.Height - w), Sc(8)))
                using (Pen pen = new Pen(Color.FromArgb((int)(210 * _flash), Theme.Light), w))
                    g.DrawPath(pen, path);
            }

            // NoClipping guards against clipped letter tops if a line is slightly tight on a
            // particular font or DPI: let it spill into the empty background around rather than
            // be cut off.
            Chrome.DrawText(g, Caption, Theme.FTitle,
                           new Rectangle(Card.Left + Sc(Theme.Pad), Card.Top + Sc(24),
                                         Card.Width - Sc(90), Sc(28)),
                           Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                                       TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix |
                                       TextFormatFlags.NoPadding | TextFormatFlags.NoClipping);
        }

        protected override void WndProc(ref Message m)
        {
            // A dialog can be the owner of another dialog — in that case the system beep is
            // caught here exactly as it is on the main window.
            if (Chrome.SwallowBlockedClick(ref m)) return;

            base.WndProc(ref m);
            if (m.Msg != 0x0084 || (int)m.Result != 1) return;
            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
            if (p.Y < Card.Top + Sc(62)) m.Result = (IntPtr)2;   // HTCAPTION
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }

            // Ctrl+Q closes the whole program from here too. A modal dialog runs its own
            // message loop and its keys never reach the main window at all, and without this
            // there was no way out of the program with a dialog open.
            else if (e.Control && e.KeyCode == Keys.Q)
            {
                e.Handled = e.SuppressKeyPress = true;
                Close();
                Application.Exit();
            }
            base.OnKeyDown(e);
        }
    }
}
