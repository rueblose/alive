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
            // Выше прежнего: поле тегов и ряд подсказок под ним растут вниз, а заметке
            // всё равно должно остаться на что смотреть.
            ClientSize = new Size(Sc(560), Sc(480));

            _tags.Cue = "type a tag, then comma";
            _tags.SetTags(ProjectMeta.TagsOf(_dir));
            // Список тегов поменялся — поле могло стать выше или ниже, а ряд под ним
            // потерять или вернуть пилюлю: пересобираем всё окно.
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

            _cancel.Text = "Cancel";
            _cancel.FitToText(16);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);

            _save.Text = "Save";
            _save.Primary = true;
            _save.FitToText(20);
            _save.Click += delegate { Commit(); };
            Controls.Add(_save);

            // Открыли окно — можно сразу набирать тег, как было с прежним полем.
            Shown += delegate { _tags.Box.Focus(); };
        }

        void Commit()
        {
            // Набрал слово и сразу нажал Save — тег должен сохраниться, а не пропасть
            // вместе с недописанной запятой.
            _tags.CommitPending();
            ProjectMeta.Set(_dir, _tags.Tags, _note.Text);
            DialogResult = DialogResult.OK;
            Close();
        }

        /// <summary>Под полем — те теги, что уже где-то стоят и ещё не выбраны здесь.</summary>
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
            // Ctrl+Enter — сохранить: в многострочном поле обычный Enter это перенос строки.
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

            // Поле растёт вниз по числу пилюль, ряд подсказок под ним — тоже; заметка
            // забирает то, что осталось.
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

            Chrome.DrawText(g, "Tags (separate by comma)",
                            Theme.FLabel, _labelTags, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.NoClipping);
            Chrome.DrawText(g, "Notes", Theme.FLabel, _labelNote, Theme.TextDim,
                            Chrome.Left | TextFormatFlags.NoClipping);

            // Подложка под нативным полем: сам TextBox рисует только текст на своём фоне,
            // скруглить себя он не умеет.
            if (_noteBox.Width > 0) Theme.FillRound(g, _noteBox, Sc(10), Theme.Sunken);

            Chrome.DrawText(g, "Ctrl+Enter to save", Theme.FBadge,
                            new Rectangle(Card.Left + Sc(Theme.Pad), _save.Top,
                                          Card.Width, _save.Height),
                            Theme.TextDim, Chrome.Left | TextFormatFlags.VerticalCenter);
        }
    }
}
