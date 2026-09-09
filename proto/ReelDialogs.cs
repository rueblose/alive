using System;
using System.Drawing;
using System.Windows.Forms;
using AbletonManager;

namespace Reel
{
    /// <summary>
    /// Сообщение к снапшоту. Одно поле — то самое «зачем я это сохранил», которого
    /// людям не хватает в Live (см. proto\README.md).
    ///
    /// Стекло оставлено: поле тут — FieldBox, он рисует себя сам, нативного дочернего
    /// TextBox с его подмесом фона внутри нет (в отличие от NotesDialog).
    /// </summary>
    internal sealed class SnapshotDialog : GlassDialog
    {
        readonly FieldBox _text = new FieldBox();
        readonly GlassButton _cancel = new GlassButton();
        readonly GlassButton _save = new GlassButton();

        public string Value { get { return _text.Box.Text.Trim(); } }

        public SnapshotDialog(string setName, string initial)
        {
            Caption = "Snapshot";
            ClientSize = new Size(Sc(520), Sc(210));

            _text.Cue = "swapped the drop drums, kept the old bass";
            _text.Box.Text = initial ?? "";
            Controls.Add(_text);

            _cancel.Text = "Cancel";
            _cancel.FitToText(16);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);

            _save.Text = "Save";
            _save.Primary = true;
            _save.FitToText(20);
            _save.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_save);

            _set = setName ?? "";
        }

        readonly string _set;
        Rectangle _label, _hint;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            _text.Box.Focus();
            _text.Box.SelectionStart = _text.Box.Text.Length;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                DialogResult = DialogResult.OK; Close();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_save == null) return;

            int pad = Sc(Theme.Pad);
            int x = Card.Left + pad, w = Card.Width - pad * 2;
            int y = Card.Top + Sc(66);

            _label = new Rectangle(x, y, w, Sc(20));
            y += Sc(24);
            _text.SetBounds(x, y, w, Sc(Theme.ControlH));

            int by = Card.Bottom - pad - _save.Height;
            _save.Location = new Point(Card.Right - pad - _save.Width, by);
            _cancel.Location = new Point(_save.Left - Sc(10) - _cancel.Width, by);
            _hint = new Rectangle(x, by, w, _save.Height);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            Chrome.DrawText(g, "What changed in " + _set, Theme.FLabel, _label,
                            Theme.TextDim, Chrome.Left | TextFormatFlags.NoClipping);
            Chrome.DrawText(g, "Enter to save", Theme.FBadge, _hint,
                            Theme.TextDim, Chrome.Left | TextFormatFlags.VerticalCenter);
        }
    }

}
