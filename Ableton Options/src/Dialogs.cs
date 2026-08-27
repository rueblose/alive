using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace AbletonOptions
{
    /// <summary>Безрамочное стеклянное окно — общая основа для диалогов.</summary>
    public class GlassDialog : Form
    {
        protected readonly CloseButton CloseBtn = new CloseButton();
        protected Rectangle Card;
        protected string Caption = "";

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public GlassDialog()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterParent;
            ShowInTaskbar = false;
            KeyPreview = true;
            BackColor = Color.FromArgb(0x14, 0x14, 0x17);
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

            CloseBtn.Click += delegate { Close(); };
            Controls.Add(CloseBtn);
        }

        protected int Sc(int v) { return (int)Math.Round(v * (DeviceDpi / 96f)); }

        protected override CreateParams CreateParams
        {
            get { CreateParams cp = base.CreateParams; cp.ClassStyle |= 0x00020000; return cp; }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
                int dark = 1; DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            }
            catch { }
            Invalidate(true);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Card = new Rectangle(0, 0, ClientSize.Width, ClientSize.Height);
            CloseBtn.Location = new Point(Card.Right - Sc(20) - CloseBtn.Width, Card.Top + Sc(18));
            Invalidate(true);
        }

        protected override void OnMove(EventArgs e) { base.OnMove(e); Invalidate(true); }

        protected override void OnPaintBackground(PaintEventArgs e) { }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Backdrop.Paint(g, ClientRectangle, Bounds);
            using (SolidBrush b = new SolidBrush(Theme.CardFill))
                g.FillRectangle(b, ClientRectangle);
            Theme.Smooth(g);

            TextRenderer.DrawText(g, Caption, Theme.UI(11.5f, FontStyle.Regular),
                                  new Rectangle(Card.Left + Sc(22), Card.Top + Sc(16),
                                                Card.Width - Sc(70), Sc(34)),
                                  Theme.Text, Chrome.Left);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg != 0x0084 || (int)m.Result != 1) return;
            int raw = m.LParam.ToInt32();
            Point p = PointToClient(new Point((short)(raw & 0xFFFF), (short)((raw >> 16) & 0xFFFF)));
            if (p.Y < Card.Top + Sc(54)) m.Result = (IntPtr)2;   // HTCAPTION
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { Close(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected TextBox MakeCodeBox(bool readOnly)
        {
            TextBox tb = new TextBox();
            tb.Multiline = true;
            tb.ReadOnly = readOnly;
            tb.ScrollBars = ScrollBars.Vertical;
            tb.WordWrap = false;
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Color.FromArgb(0xFF, 0x18, 0x18, 0x1C);
            tb.ForeColor = Theme.Mono;
            tb.Font = Theme.Code(10f);
            Controls.Add(tb);
            return tb;
        }
    }

    /// <summary>Предпросмотр итогового Options.txt.</summary>
    public sealed class TextDialog : GlassDialog
    {
        readonly TextBox _text;
        readonly GlassButton _copy = new GlassButton();
        readonly GlassButton _ok = new GlassButton();

        public TextDialog(string caption, string text)
        {
            Caption = caption;
            ClientSize = new Size(700, 560);

            _text = MakeCodeBox(true);
            _text.Text = text;

            _copy.Text = L.S("Copy", "Скопировать");
            _copy.FitToText(16);
            _copy.Click += delegate { if (_text.TextLength > 0) Clipboard.SetText(_text.Text); };
            Controls.Add(_copy);

            _ok.Text = L.S("Close", "Закрыть");
            _ok.Primary = true;
            _ok.FitToText(18);
            _ok.Click += delegate { Close(); };
            Controls.Add(_ok);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_text == null) return;
            int pad = Sc(22);
            int top = Card.Top + Sc(58);
            int bottom = Card.Bottom - pad - _ok.Height - Sc(14);
            _text.SetBounds(Card.Left + pad + Sc(10), top + Sc(10),
                            Card.Width - pad * 2 - Sc(20), Math.Max(Sc(60), bottom - top - Sc(20)));
            _copy.Location = new Point(Card.Left + pad, Card.Bottom - pad - _ok.Height);
            _ok.Location = new Point(Card.Right - pad - _ok.Width, Card.Bottom - pad - _ok.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_text == null) return;
            Rectangle r = Rectangle.Inflate(_text.Bounds, Sc(10), Sc(10));
            Theme.FillRound(e.Graphics, r, Sc(10), Color.FromArgb(0xFF, 0x18, 0x18, 0x1C));
            Theme.DrawRound(e.Graphics, r, Sc(10), Theme.FieldBorder, 1f);
        }
    }

    /// <summary>Опции, которых нет в каталоге: правим руками, чтобы ничего не потерялось.</summary>
    public sealed class CustomOptionsDialog : GlassDialog
    {
        readonly TextBox _text;
        readonly GlassButton _ok = new GlassButton();
        readonly GlassButton _cancel = new GlassButton();
        readonly string _hint;

        public readonly List<OptionLine> Result = new List<OptionLine>();

        public CustomOptionsDialog(List<OptionLine> current)
        {
            Caption = L.S("Custom options", "Свои опции");
            ClientSize = new Size(660, 480);

            _hint = L.S(
                "Options this app doesn't know about. Everything from your Options.txt that wasn't "
                + "recognised lands here and is never lost.\nOne option per line, as -OptionName or -OptionName=value.",
                "Опции, которых нет в каталоге программы. Сюда попадает всё, что было в твоём "
                + "Options.txt и что программа не узнала — эти строки не теряются.\n"
                + "Одна опция на строку, в формате -ИмяОпции или -ИмяОпции=значение.");

            _text = MakeCodeBox(false);

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (OptionLine l in current)
            {
                sb.Append('-').Append(l.Name);
                if (!string.IsNullOrEmpty(l.Value)) sb.Append('=').Append(l.Value);
                sb.Append("\r\n");
            }
            _text.Text = sb.ToString();

            _ok.Text = L.S("Apply", "Применить");
            _ok.Primary = true;
            _ok.FitToText(18);
            _ok.Click += delegate { Parse(); DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_ok);

            _cancel.Text = L.S("Cancel", "Отмена");
            _cancel.FitToText(16);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);
        }

        Rectangle _rHint;

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_text == null) return;
            int pad = Sc(22);
            int w = Card.Width - pad * 2;
            int hintH = TextRenderer.MeasureText(_hint, Theme.FSmall, new Size(w, int.MaxValue), Chrome.Wrap).Height;
            _rHint = new Rectangle(Card.Left + pad, Card.Top + Sc(56), w, hintH);

            int top = _rHint.Bottom + Sc(14);
            int bottom = Card.Bottom - pad - _ok.Height - Sc(14);
            _text.SetBounds(Card.Left + pad + Sc(10), top + Sc(10), w - Sc(20),
                            Math.Max(Sc(60), bottom - top - Sc(20)));

            _ok.Location = new Point(Card.Right - pad - _ok.Width, Card.Bottom - pad - _ok.Height);
            _cancel.Location = new Point(_ok.Left - Sc(8) - _cancel.Width, _ok.Top);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (_text == null) return;
            TextRenderer.DrawText(e.Graphics, _hint, Theme.FSmall, _rHint, Theme.TextDim, Chrome.Wrap);
            Rectangle r = Rectangle.Inflate(_text.Bounds, Sc(10), Sc(10));
            Theme.FillRound(e.Graphics, r, Sc(10), Color.FromArgb(0xFF, 0x18, 0x18, 0x1C));
            Theme.DrawRound(e.Graphics, r, Sc(10), Theme.FieldBorder, 1f);
        }

        void Parse()
        {
            Result.Clear();
            string[] lines = _text.Text.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("-")) line = line.Substring(1);
                int eq = line.IndexOf('=');
                string name = eq >= 0 ? line.Substring(0, eq).Trim() : line;
                string val = eq >= 0 ? line.Substring(eq + 1).Trim() : "";
                if (name.Length == 0) continue;
                Result.Add(new OptionLine(name, val));
            }
        }
    }
}
