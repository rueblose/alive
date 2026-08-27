using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Теги и заметка одного проекта.
    ///
    /// Стекло здесь выключено (UseGlass): заметка многострочная, а значит это настоящий
    /// нативный TextBox, и на акриловом окне он подмешивает к себе то, что физически за
    /// окном (замерено, см. Glass.ApplyBackdrop). Остальные диалоги стекло сохраняют —
    /// там поля рисуются своими руками и нативных дочерних окон нет.
    /// </summary>
    public sealed class NotesDialog : GlassDialog
    {
        public override bool UseGlass { get { return false; } }

        readonly FieldBox _tags = new FieldBox();
        readonly GlassButton _pick = new GlassButton();
        readonly TextBox _note = new TextBox();
        readonly GlassButton _cancel = new GlassButton();
        readonly GlassButton _save = new GlassButton();

        readonly string _dir;

        public NotesDialog(SetEntry set)
        {
            _dir = set.ProjectDir;
            Caption = set.Name;
            ClientSize = new Size(Sc(560), Sc(430));

            _tags.Cue = L.S("remix, collab, femobycore",
                            "drum, wip, для Саши — через запятую");
            _tags.Box.Text = ProjectMeta.JoinTags(ProjectMeta.TagsOf(_dir));
            Controls.Add(_tags);

            _pick.Text = L.S("Existing…", "Уже есть…");
            _pick.FitToText(14);
            _pick.Click += delegate { PickExisting(); };
            Controls.Add(_pick);

            _note.Multiline = true;
            // Без полосы прокрутки: нативную не покрасить, и светлый жёлоб Windows на
            // тёмном окне — единственное пятно, которое видно раньше самого текста.
            // Длинная заметка всё равно прокручивается за кареткой при наборе.
            _note.ScrollBars = ScrollBars.None;
            _note.WordWrap = true;
            _note.BorderStyle = BorderStyle.None;
            _note.BackColor = Theme.Sunken;
            _note.ForeColor = Theme.Text;
            _note.Font = Theme.FBody;
            _note.AcceptsReturn = true;
            _note.Text = ProjectMeta.NoteOf(_dir);
            Controls.Add(_note);

            _cancel.Text = L.S("Cancel", "Отмена");
            _cancel.FitToText(16);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);

            _save.Text = L.S("Save", "Сохранить");
            _save.Primary = true;
            _save.FitToText(20);
            _save.Click += delegate { Commit(); };
            Controls.Add(_save);
        }

        void Commit()
        {
            ProjectMeta.Set(_dir, ProjectMeta.ParseTags(_tags.Box.Text), _note.Text);
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Дописать тег, который уже где-то стоит, — чтобы не вспоминать написание.</summary>
        void PickExisting()
        {
            List<string> already = ProjectMeta.ParseTags(_tags.Box.Text);
            ContextMenuStrip m = DarkMenu.Create();
            foreach (string tag in ProjectMeta.AllTags())
            {
                bool used = false;
                foreach (string t in already)
                    if (string.Equals(t, tag, StringComparison.CurrentCultureIgnoreCase)) { used = true; break; }
                if (used) continue;

                string captured = tag;
                ToolStripMenuItem mi = new ToolStripMenuItem(tag);
                mi.Click += delegate
                {
                    List<string> now = ProjectMeta.ParseTags(_tags.Box.Text);
                    now.Add(captured);
                    _tags.Box.Text = ProjectMeta.JoinTags(now);
                    _tags.Invalidate();
                };
                m.Items.Add(mi);
            }
            if (m.Items.Count == 0)
                m.Items.Add(new ToolStripMenuItem(L.S("no tags yet", "тегов пока нет")) { Enabled = false });
            m.Show(_pick, new Point(0, _pick.Height + Sc(4)));
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // Ctrl+Enter — сохранить: в многострочном поле обычный Enter это перенос строки.
            if (e.Control && e.KeyCode == Keys.Enter) { Commit(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_save == null) return;

            int pad = Sc(Theme.Pad);
            int x = Card.Left + pad;
            int w = Card.Width - pad * 2;
            int y = Card.Top + Sc(66);

            _labelTags = new Rectangle(x, y, w, Sc(20));
            y += Sc(24);

            int pickW = _pick.Width;
            _tags.SetBounds(x, y, w - pickW - Sc(8), Sc(Theme.ControlH));
            _pick.SetBounds(x + w - pickW, y, pickW, Sc(Theme.ControlH));
            y += Sc(Theme.ControlH) + Sc(18);

            _labelNote = new Rectangle(x, y, w, Sc(20));
            y += Sc(24);

            int bottom = Card.Bottom - pad - _save.Height - Sc(16);
            _noteBox = new Rectangle(x, y, w, Math.Max(Sc(80), bottom - y));
            // Сам TextBox чуть внутри нарисованной рамки — иначе текст липнет к краю.
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

            Chrome.DrawText(g, L.S("Tags (separate by comma)", "Теги (через запятую)"),
                            Theme.FLabel, _labelTags, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.NoClipping);
            Chrome.DrawText(g, L.S("Notes", "Заметки"), Theme.FLabel, _labelNote, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.NoClipping);

            // Подложка под нативным полем: сам TextBox рисует только текст на своём фоне,
            // скруглить себя он не умеет.
            if (_noteBox.Width > 0) Theme.FillRound(g, _noteBox, Sc(10), Theme.Sunken);

            Chrome.DrawText(g, L.S("Ctrl+Enter to save", "Ctrl+Enter — сохранить"), Theme.FBadge,
                            new Rectangle(Card.Left + Sc(Theme.Pad), _save.Top,
                                          Card.Width, _save.Height),
                            Theme.TextDim, Chrome.Left | TextFormatFlags.VerticalCenter);
        }
    }
}
