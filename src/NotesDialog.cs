using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// One project's tags and note.
    ///
    /// Glass is off here (UseGlass): the note is multi-line, which makes it a real native
    /// TextBox, and on an acrylic window it mixes in whatever is physically behind the window
    /// (measured, see Glass.ApplyBackdrop). The other dialogs keep their glass — there the
    /// fields are drawn by hand and there are no native child windows.
    /// </summary>
    public sealed class NotesDialog : GlassDialog
    {
        public override bool UseGlass { get { return false; } }

        readonly TagEditor _tags = new TagEditor();
        readonly TagSuggest _suggest = new TagSuggest();
        readonly TextBox _note = new TextBox();
        readonly GlassButton _cancel = new GlassButton();
        readonly GlassButton _save = new GlassButton();

        readonly string _dir;

        public NotesDialog(SetEntry set)
        {
            _dir = set.ProjectDir;
            Caption = set.Name;
            // Taller than before: the tag field and the row of suggestions under it grow
            // downwards, and the note still has to be left something to look at.
            ClientSize = new Size(Sc(560), Sc(480));

            _tags.Cue = "type a tag, then comma";
            _tags.SetTags(ProjectMeta.TagsOf(_dir));
            // The tag list changed — the field may have grown or shrunk, and the row below may
            // have lost or regained a pill: rebuild the whole window.
            _tags.Changed += delegate { RefreshSuggestions(); Relayout(); };
            Controls.Add(_tags);

            _suggest.Picked += delegate (string tag)
            {
                _tags.AddTag(tag);
                _tags.Box.Focus();
            };
            Controls.Add(_suggest);
            RefreshSuggestions();

            _note.Multiline = true;
            // No scrollbar: the native one cannot be painted, and a light Windows trough on a
            // dark window is the one spot the eye catches before the text itself. A long note
            // still scrolls along with the caret while typing.
            _note.ScrollBars = ScrollBars.None;
            _note.WordWrap = true;
            _note.BorderStyle = BorderStyle.None;
            _note.BackColor = Theme.Sunken;
            _note.ForeColor = Theme.Text;
            _note.Font = Theme.FBody;
            _note.AcceptsReturn = true;
            _note.Text = ProjectMeta.NoteOf(_dir);
            Controls.Add(_note);

            _cancel.Text = "Cancel";
            _cancel.FitToText(16);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);

            _save.Text = "Save";
            _save.Primary = true;
            _save.FitToText(20);
            _save.Click += delegate { Commit(); };
            Controls.Add(_save);

            // Window opened — a tag can be typed right away, as it was with the old field.
            Shown += delegate { _tags.Box.Focus(); };
        }

        void Commit()
        {
            // A word typed and Save pressed immediately — the tag has to be kept, not lost
            // together with the comma that was never typed.
            _tags.CommitPending();
            ProjectMeta.Set(_dir, _tags.Tags, _note.Text);
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Under the field — the tags already used somewhere and not yet picked
        /// here.</summary>
        void RefreshSuggestions()
        {
            List<string> rest = new List<string>();
            foreach (string tag in ProjectMeta.AllTags())
            {
                bool used = false;
                foreach (string t in _tags.Tags)
                    if (string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)) { used = true; break; }
                if (!used) rest.Add(tag);
            }
            _suggest.SetItems(rest);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Ctrl+Enter saves: in a multi-line field a plain Enter is a line break.
            if (e.Control && e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            Relayout();
        }

        void Relayout()
        {
            if (_save == null) return;

            int pad = Sc(Theme.Pad);
            int x = Card.Left + pad;
            int w = Card.Width - pad * 2;
            int y = Card.Top + Sc(66);

            _labelTags = new Rectangle(x, y, w, Sc(20));
            y += Sc(30);

            // The field grows downwards with the number of pills, and so does the suggestion
            // row under it; the note takes whatever is left.
            _tags.SetBounds(x, y, w, Sc(Theme.ControlH));
            _tags.Height = _tags.Relayout();
            y += _tags.Height + Sc(10);

            _suggest.SetBounds(x, y, w, Sc(24));
            int suggestH = _suggest.Relayout();
            _suggest.Visible = suggestH > 0;
            _suggest.Height = Math.Max(1, suggestH);
            y += (suggestH > 0 ? suggestH + Sc(18) : Sc(8));

            _labelNote = new Rectangle(x, y, w, Sc(20));
            y += Sc(30);

            int bottom = Card.Bottom - pad - _save.Height - Sc(16);
            _noteBox = new Rectangle(x, y, w, Math.Max(Sc(80), bottom - y));
            // The TextBox itself sits slightly inside the drawn frame — otherwise the text
            // clings to the edge.
            _note.SetBounds(_noteBox.X + Sc(10), _noteBox.Y + Sc(8),
                            _noteBox.Width - Sc(20), _noteBox.Height - Sc(16));

            int by = Card.Bottom - pad - _save.Height;
            _save.Location = new Point(Card.Right - pad - _save.Width, by);
            _cancel.Location = new Point(_save.Left - Sc(10) - _cancel.Width, by);
            Invalidate();
        }

        Rectangle _labelTags, _labelNote, _noteBox;

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            Chrome.DrawText(g, "Tags (separate by comma)",
                            Theme.FLabel, _labelTags, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.NoClipping);
            Chrome.DrawText(g, "Notes", Theme.FLabel, _labelNote, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.NoClipping);

            // A backing plate under the native field: the TextBox only draws text on its own
            // background, it cannot round itself off.
            if (_noteBox.Width > 0) Theme.FillRound(g, _noteBox, Sc(10), Theme.Sunken);

            Chrome.DrawText(g, "Ctrl+Enter to save", Theme.FBadge,
                            new Rectangle(Card.Left + Sc(Theme.Pad), _save.Top,
                                          Card.Width, _save.Height),
                            Theme.TextDim, Chrome.Left | TextFormatFlags.VerticalCenter);
        }
    }
}
