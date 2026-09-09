using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Короткое сообщение в углу содержимого: показали, подержали пару секунд, погасили.
    /// Ничего не спрашивает и ответа не ждёт, поэтому это контрол на форме, а не диалог:
    /// модальное окно ради «идёт запуск» перекрыло бы работу ровно там, где прерывать её
    /// незачем, и требовало бы закрыть себя руками.
    /// </summary>
    public sealed class Toast : GlassControl
    {
        string _text = "";
        float _alpha;
        bool _fadingOut;
        readonly Timer _hideTimer = new Timer();

        int InnerPad { get { return Sc(16); } }
        int TextLeft { get { return InnerPad; } }

        // Вырезает контрол по форме пилюли — без этого за скруглёнными углами
        // оставался бы прямоугольник, закрывающий содержимое под контролом.
        protected override float PillRadius { get { return Sc(Theme.CardR); } }

        public Toast()
        {
            Visible = false;
            // Мышь сквозь него проходит к тому, что под ним: сообщение появляется само,
            // поверх плиток, и ловить клики, которых ему не адресовали, ему нечего.
            Enabled = false;
            Font = Theme.FBody;

            _hideTimer.Tick += delegate
            {
                _hideTimer.Stop();
                _fadingOut = true;
                AnimEngine.Register(this);
            };
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            UpdateRegion();
        }

        void UpdateRegion()
        {
            if (Width <= 0 || Height <= 0) return;
            float r = PillRadius;
            if (r <= 0f)
            {
                if (Region != null) { Region.Dispose(); Region = null; }
                return;
            }
            using (GraphicsPath p = Theme.Round(new RectangleF(0, 0, Width, Height), r))
            {
                Region old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        /// <summary>Показать текст и погасить через ms миллисекунд.</summary>
        public void Post(string text, int ms)
        {
            _text = text ?? "";
            _fadingOut = false;
            Visible = true;
            BringToFront();

            _hideTimer.Stop();
            _hideTimer.Interval = Math.Max(1, ms);
            _hideTimer.Start();

            AnimEngine.Register(this);
            Invalidate();
        }

        /// <summary>
        /// Встать в левый нижний угол переданной области, с отступом от нижнего края —
        /// впритык к футеру плашка выглядела бы приклеенной к нему, а не отдельным
        /// плавающим сообщением. Размер по ширине — по самому тексту: сообщение короткое
        /// и заранее неизвестное, а коробка фиксированной ширины либо обрезала бы его,
        /// либо зияла пустотой.
        /// </summary>
        public void PlaceIn(Rectangle content)
        {
            Size sz = TextRenderer.MeasureText(_text, Font, new Size(Sc(420), Sc(40)),
                TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

            int w = Math.Min(content.Width, TextLeft + sz.Width + InnerPad);
            int h = Sc(44);
            int margin = Sc(18);
            int y = Math.Max(content.Top, content.Bottom - h - margin);
            SetBounds(content.Left, y, w, h);
            UpdateRegion();
        }

        public override bool OnAnimTick()
        {
            if (IsDisposed) return false;

            float target = _fadingOut ? 0f : 1f;
            float d = target - _alpha;
            if (Math.Abs(d) <= 0.01f)
            {
                _alpha = target;
                Invalidate();
                if (_alpha <= 0f) { Visible = false; return false; }
                // Полностью показан — дальше плашка неподвижна до срабатывания
                // _hideTimer; тикать впустую незачем.
                return false;
            }

            _alpha += d * 0.25f;
            Invalidate();
            return true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _hideTimer.Dispose();
                if (Region != null) { Region.Dispose(); Region = null; }
            }
            base.Dispose(disposing);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            if (_alpha <= 0.01f) return;

            Color baseBg = Theme.Backdrop;
            Color cardColor = Theme.Interpolate(baseBg, Theme.SurfacePressed, _alpha);
            RectangleF card = new RectangleF(0, 0, Width, Height);
            float r = PillRadius;
            g.Clear(cardColor);
            Theme.FillRound(g, card, r, cardColor);

            Chrome.DrawText(g, _text, Font,
                new Rectangle(TextLeft, 0, Math.Max(0, Width - TextLeft - InnerPad), Height),
                Theme.Interpolate(baseBg, Theme.Text, _alpha), Chrome.Left);
        }
    }
}
