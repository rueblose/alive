using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>Безрамочное окно диалога — тот же фон и та же шапка, что у главного окна.</summary>
    public class GlassDialog : Form, IAnimatable
    {
        protected readonly IconButton CloseBtn = new IconButton();
        protected Rectangle Card;
        protected string Caption = "";

        /// <summary>
        /// Переопределяется в false у диалогов с полями ввода: настоящее стекло у
        /// окна ломает их дочерние TextBox непредсказуемым подмесом реального фона
        /// (замерено, см. Glass.ApplyChrome). Окно остаётся тёмным и со скруглёнными
        /// углами — просто не полупрозрачным.
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

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; }
        }

        /// <summary>Стекло — сразу по хендлу, до первого кадра, чтобы не мигало.</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (UseGlass) Glass.Apply(this); else Glass.ApplyChrome(this);
        }

        /// <summary>И ещё раз по показу: заданный до появления окна backdrop DWM иногда
        /// не подхватывает, пока визуал окна не пересоздадут.</summary>
        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) { if (UseGlass) Glass.Apply(this); else Glass.ApplyChrome(this); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Invalidate(true);
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

        // ------------------------------------------------------------------ подсветка

        float _flash;

        /// <summary>
        /// Мигнуть обводкой: «окно здесь, оно и не даёт нажать дальше». Ответ на клик
        /// мимо модального окна вместо системного звука — см. Chrome.SwallowBlockedClick.
        /// Пока кнопку держат, сообщение приходит десятками; заново разгораться на
        /// каждое не нужно, поэтому перезапускаем, только когда подсветка уже угасла.
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
            // ~0.4 c до полного угасания: короче — глаз не успевает поймать, длиннее —
            // подсветка начинает выглядеть состоянием окна, а не ответом на клик.
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
                // Радиус тот же, которым DWM скругляет окно, иначе обводка отходит от
                // угла. Толщина в два пикселя: однопиксельная на скруглении почти не видна.
                float w = Sc(2);
                using (GraphicsPath path = Theme.Round(
                           new RectangleF(w / 2f, w / 2f, ClientSize.Width - w, ClientSize.Height - w), Sc(8)))
                using (Pen pen = new Pen(Color.FromArgb((int)(210 * _flash), Theme.Light), w))
                    g.DrawPath(pen, path);
            }

            // NoClipping — подстраховка от обрезанных верхушек букв, если на конкретном
            // шрифте/DPI строке чуть тесно: пусть вылезет в пустой фон вокруг, не срежется.
            Chrome.DrawText(g, Caption, Theme.FTitle,
                           new Rectangle(Card.Left + Sc(Theme.Pad), Card.Top + Sc(24),
                                         Card.Width - Sc(90), Sc(28)),
                           Theme.Text, Chrome.Left | TextFormatFlags.NoClipping);
        }

        protected override void WndProc(ref Message m)
        {
            // Диалог бывает владельцем другого диалога (Rescue поверх Forks) — тогда
            // системный звук ловится здесь ровно так же, как у главного окна.
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

            // Ctrl+Q закрывает программу целиком и отсюда тоже. Модальный диалог крутит
            // собственный цикл сообщений, до главного окна его клавиши не доходят вовсе,
            // и без этого выйти из программы с открытым диалогом было нельзя.
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
