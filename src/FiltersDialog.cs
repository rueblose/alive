using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Все условия отбора в одном окне. Внизу всё время видно, сколько сетов проходит —
    /// иначе набирать фильтры вслепую и каждый раз закрывать окно, чтобы узнать результат.
    /// </summary>
    public sealed class FiltersDialog : GlassDialog
    {
        readonly SetFilter _filter = new SetFilter();
        readonly List<SetEntry> _sets;

        readonly FieldBox _from = new FieldBox();
        readonly FieldBox _to = new FieldBox();
        readonly TagField _versions = new TagField();
        readonly TagField _roots = new TagField();
        readonly TagField _scales = new TagField();
        readonly TagField _tags = new TagField();
        readonly FieldBox _tracksMin = new FieldBox();
        readonly FieldBox _tracksMax = new FieldBox();
        readonly FieldBox _pluginsMin = new FieldBox();
        readonly FieldBox _pluginsMax = new FieldBox();
        readonly PillToggle _pluginsMissing = new PillToggle();
        readonly PillToggle _pluginsAll = new PillToggle();

        // Тег-поля показывают только то, что реально встречается в сетах, поэтому
        // индекс пункта уже не равен значению — держим отдельную карту.
        readonly List<int> _rootValues = new List<int>();    // -1 = «без тональности»
        readonly List<int> _scaleValues = new List<int>();
        readonly PillToggle _complete = new PillToggle();
        readonly PillToggle _missing = new PillToggle();
        readonly PillToggle _unreadable = new PillToggle();
        readonly PillToggle _previewHasRenders = new PillToggle();
        readonly PillToggle _previewNoRenders = new PillToggle();
        readonly GlassButton _reset = new GlassButton();
        readonly GlassButton _apply = new GlassButton();

        /// <summary>
        /// Условия применяются на лету: окно закрывает список, который фильтрует, и без
        /// живого отклика набирать фильтры приходилось вслепую. Кнопка снизу теперь
        /// просто «Ok» — закрыть, а не «применить».
        /// </summary>
        public event Action Changed;

        readonly List<Rectangle> _labels = new List<Rectangle>();
        readonly List<string> _labelTexts = new List<string>();
        bool _laying;
        int _matches;

        /// <summary>Готовый набор условий — забирать после DialogResult.OK.</summary>
        public SetFilter Result { get { return _filter; } }

        public FiltersDialog(SetFilter current, List<SetEntry> sets, List<string> versions)
        {
            _sets = sets;
            _filter.CopyFrom(current);
            Caption = "Filters";
            ClientSize = new Size(Sc(760), Sc(568));

            _from.Box.Text = SetFilter.FormatDate(_filter.From);
            _to.Box.Text = SetFilter.FormatDate(_filter.To);
            _tracksMin.Box.Text = SetFilter.FormatCount(_filter.TracksMin);
            _tracksMax.Box.Text = SetFilter.FormatCount(_filter.TracksMax);
            _pluginsMin.Box.Text = SetFilter.FormatCount(_filter.PluginsMin);
            _pluginsMax.Box.Text = SetFilter.FormatCount(_filter.PluginsMax);

            foreach (FieldBox f in new FieldBox[] { _from, _to, _tracksMin, _tracksMax, _pluginsMin, _pluginsMax })
            {
                f.Box.TextChanged += delegate { Collect(); };
                Controls.Add(f);
            }
            Cue(_from, "from  2026-01");
            Cue(_to, "to  2026-08-07");

            // Дату можно по-прежнему набрать руками — ParseDate понимает и «2026»,
            // и «2026-08». Календарь слева для тех случаев, когда проще ткнуть.
            DatePicker(_from, false);
            DatePicker(_to, true);
            Cue(_tracksMin, "min");
            Cue(_tracksMax, "max");
            Cue(_pluginsMin, "min");
            Cue(_pluginsMax, "max");

            _versions.SetOptions(versions);
            _versions.Placeholder = "any version";
            _versions.SetSelected(IndexesOf(versions, _filter.Versions));

            BuildKeyOptions();
            _roots.Placeholder = "any root";
            _roots.SetSelected(IndexesForValues(_rootValues, _filter.KeyRoots));

            _scales.Placeholder = "any scale";
            _scales.SetSelected(IndexesForValues(_scaleValues, _filter.KeyScales));

            _tags.SetOptions(TagsInSets());
            _tags.Placeholder = "any tag";
            _tags.SetSelected(IndexesOf(_tags.Options, _filter.Tags));

            foreach (TagField t in new TagField[] { _versions, _roots, _scales, _tags })
            {
                t.Changed += delegate { Collect(); LayoutRows(); };
                Controls.Add(t);
            }

            // Две взаимоисключающие галки: «есть дыры» и «всё на месте».
            _pluginsMissing.Text = "some not installed";
            _pluginsMissing.Checked = _filter.PluginsMissingOnly;
            _pluginsMissing.FitToText();
            _pluginsMissing.CheckedChanged += delegate
            {
                if (_pluginsMissing.Checked) _pluginsAll.Checked = false;
                Collect();
            };
            Controls.Add(_pluginsMissing);

            _pluginsAll.Text = "all installed";
            _pluginsAll.Checked = _filter.PluginsAllInstalled;
            _pluginsAll.FitToText();
            _pluginsAll.CheckedChanged += delegate
            {
                if (_pluginsAll.Checked) _pluginsMissing.Checked = false;
                Collect();
            };
            Controls.Add(_pluginsAll);

            _complete.Text = "complete";
            _missing.Text = "missing files";
            _unreadable.Text = "unreadable";
            _complete.Checked = _filter.FilesComplete;
            _missing.Checked = _filter.FilesMissing;
            _unreadable.Checked = _filter.FilesUnreadable;
            foreach (PillToggle p in new PillToggle[] { _complete, _missing, _unreadable })
            {
                p.FitToText();
                p.CheckedChanged += delegate { Collect(); };
                Controls.Add(p);
            }

            _previewHasRenders.Text = "has renders";
            _previewNoRenders.Text = "no renders";
            _previewHasRenders.Checked = _filter.PreviewHasRenders;
            _previewNoRenders.Checked = _filter.PreviewNoRenders;
            foreach (PillToggle p in new PillToggle[] { _previewHasRenders, _previewNoRenders })
            {
                p.FitToText();
                p.CheckedChanged += delegate { Collect(); };
                Controls.Add(p);
            }

            _reset.Text = "Reset";
            _reset.Click += delegate { ResetAll(); };
            _reset.FitToText(18);
            Controls.Add(_reset);

            _apply.Text = "Ok";
            _apply.Primary = true;
            _apply.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            _apply.FitToText(18);
            Controls.Add(_apply);

            // Ровно одна ширина на обе кнопки: «Ok» короче «Reset», и по своему тексту
            // выходил заметно уже — пара читалась как случайная, а не как пара.
            int pairW = Math.Max(_reset.Width, _apply.Width);
            _reset.Width = _apply.Width = pairW;

            Collect();
        }

        // Своя подсказка, а не системная EM_SETCUEBANNER — см. комментарий у FieldBox.Cue.
        static void Cue(FieldBox f, string text) { f.Cue = text; f.Invalidate(); }

        /// <summary>Значок календаря слева в поле; выбранный день ложится в текст —
        /// дальше его разбирает тот же ParseDate, что и набранный вручную.</summary>
        static void DatePicker(FieldBox f, bool upperBound)
        {
            f.IconLeft = Glyph.Calendar;
            f.IconLeftClicked += delegate
            {
                CalendarPopup.Show(f, SetFilter.ParseDate(f.Box.Text, upperBound),
                                   delegate (DateTime d) { f.Box.Text = SetFilter.FormatDate(d); });
            };
        }

        static List<int> IndexesOf(List<string> options, List<string> values)
        {
            List<int> res = new List<int>();
            for (int i = 0; i < options.Count; i++)
                if (values.Contains(options[i])) res.Add(i);
            return res;
        }

        static List<int> IndexesForValues(List<int> optionValues, List<int> selected)
        {
            List<int> res = new List<int>();
            for (int i = 0; i < optionValues.Count; i++)
                if (selected.Contains(optionValues[i])) res.Add(i);
            return res;
        }

        /// <summary>
        /// В списки нот и ладов кладём только то, что реально встречается в сетах — иначе
        /// приходится листать 35 ладов, из которых используются пять. Плюс отдельный
        /// пункт «без тональности» для старых сетов, где её вообще нет.
        /// </summary>
        void BuildKeyOptions()
        {
            bool[] rootSeen = new bool[12];
            bool[] scaleSeen = new bool[Scales.ScaleCount];
            bool noKey = false;

            if (_sets != null)
                foreach (SetEntry s in _sets)
                {
                    if (s.ScaleRoot < 0) { noKey = true; continue; }
                    if (s.ScaleRoot < 12) rootSeen[s.ScaleRoot] = true;
                    if (s.ScaleIndex >= 0 && s.ScaleIndex < scaleSeen.Length) scaleSeen[s.ScaleIndex] = true;
                }

            bool anyKey = false;
            for (int r = 0; r < 12; r++) if (rootSeen[r]) { anyKey = true; break; }

            List<string> rootLabels = new List<string>();
            _rootValues.Clear();
            if (anyKey) { rootLabels.Add("(any key)"); _rootValues.Add(-2); }
            if (noKey) { rootLabels.Add("(no key)"); _rootValues.Add(-1); }
            for (int r = 0; r < 12; r++)
                if (rootSeen[r]) { rootLabels.Add(Scales.RootChoices[r]); _rootValues.Add(r); }
            _roots.SetOptions(rootLabels);

            List<string> scaleLabels = new List<string>();
            _scaleValues.Clear();
            for (int i = 0; i < scaleSeen.Length; i++)
                if (scaleSeen[i]) { scaleLabels.Add(Scales.ScaleName(i)); _scaleValues.Add(i); }
            _scales.SetOptions(scaleLabels);
        }

        /// <summary>
        /// Теги, которые реально стоят на этих сетах, по алфавиту. Не ProjectMeta.AllTags():
        /// там лежат метки и тех проектов, которых в списке уже нет, — отбирать по ним
        /// нечего.
        /// </summary>
        List<string> TagsInSets()
        {
            List<string> all = new List<string>();
            if (_sets != null)
                foreach (SetEntry s in _sets)
                    foreach (string t in ProjectMeta.TagsOf(s.ProjectDir))
                        if (!all.Contains(t)) all.Add(t);
            all.Sort(StringComparer.CurrentCultureIgnoreCase);
            return all;
        }

        // ------------------------------------------------------------------ данные

        void Collect()
        {
            _filter.From = SetFilter.ParseDate(_from.Box.Text, false);
            _filter.To = SetFilter.ParseDate(_to.Box.Text, true);

            _filter.Versions.Clear();
            foreach (int i in _versions.Selected) _filter.Versions.Add(_versions.Options[i]);

            _filter.KeyRoots.Clear();
            foreach (int i in _roots.Selected) _filter.KeyRoots.Add(_rootValues[i]);
            _filter.KeyScales.Clear();
            foreach (int i in _scales.Selected) _filter.KeyScales.Add(_scaleValues[i]);

            _filter.Tags.Clear();
            foreach (int i in _tags.Selected) _filter.Tags.Add(_tags.Options[i]);

            _filter.TracksMin = SetFilter.ParseCount(_tracksMin.Box.Text);
            _filter.TracksMax = SetFilter.ParseCount(_tracksMax.Box.Text);
            _filter.PluginsMin = SetFilter.ParseCount(_pluginsMin.Box.Text);
            _filter.PluginsMax = SetFilter.ParseCount(_pluginsMax.Box.Text);
            _filter.PluginsMissingOnly = _pluginsMissing.Checked;
            _filter.PluginsAllInstalled = _pluginsAll.Checked;

            _filter.FilesComplete = _complete.Checked;
            _filter.FilesMissing = _missing.Checked;
            _filter.FilesUnreadable = _unreadable.Checked;

            _filter.PreviewHasRenders = _previewHasRenders.Checked;
            _filter.PreviewNoRenders = _previewNoRenders.Checked;

            _matches = 0;
            if (_sets != null)
                foreach (SetEntry s in _sets) if (_filter.Matches(s)) _matches++;

            UpdateFacets();
            Invalidate();
            if (Changed != null) Changed();
        }

        void UpdateFacets()
        {
            if (_sets == null) return;

            // 1. Versions
            _versions.DisabledOptions.Clear();
            HashSet<string> validVersions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SetEntry s in _sets)
            {
                if (_filter.Matches(s, ignoreVersions: true))
                    if (!string.IsNullOrEmpty(s.ShortVersion)) validVersions.Add(s.ShortVersion);
            }
            for (int i = 0; i < _versions.Options.Count; i++)
            {
                if (!validVersions.Contains(_versions.Options[i]))
                    _versions.DisabledOptions.Add(i);
            }

            // 2. Key Roots
            _roots.DisabledOptions.Clear();
            HashSet<int> validRoots = new HashSet<int>();
            foreach (SetEntry s in _sets)
            {
                if (_filter.Matches(s, ignoreKeyRoots: true))
                {
                    validRoots.Add(s.ScaleRoot);
                    if (s.ScaleRoot >= 0) validRoots.Add(-2); // любая тональность
                }
            }
            for (int i = 0; i < _rootValues.Count; i++)
            {
                if (!validRoots.Contains(_rootValues[i]))
                    _roots.DisabledOptions.Add(i);
            }

            // 3. Key Scales
            _scales.DisabledOptions.Clear();
            HashSet<int> validScales = new HashSet<int>();
            foreach (SetEntry s in _sets)
            {
                if (_filter.Matches(s, ignoreKeyScales: true))
                {
                    if (s.ScaleIndex >= 0) validScales.Add(s.ScaleIndex);
                }
            }
            for (int i = 0; i < _scaleValues.Count; i++)
            {
                if (!validScales.Contains(_scaleValues[i]))
                    _scales.DisabledOptions.Add(i);
            }

            // 4. Tags
            _tags.DisabledOptions.Clear();
            HashSet<string> validTags = new HashSet<string>();
            foreach (SetEntry s in _sets)
            {
                if (_filter.Matches(s, ignoreTags: true))
                    foreach (string t in ProjectMeta.TagsOf(s.ProjectDir)) validTags.Add(t);
            }
            for (int i = 0; i < _tags.Options.Count; i++)
            {
                if (!validTags.Contains(_tags.Options[i]))
                    _tags.DisabledOptions.Add(i);
            }

            // 5. Plugins State Toggles
            int countPluginsMissing = 0, countPluginsAll = 0;
            foreach (SetEntry s in _sets)
            {
                if (_filter.Matches(s, ignorePluginsState: true))
                {
                    if (s.MissingPlugins > 0) countPluginsMissing++;
                    if (s.MissingPlugins == 0) countPluginsAll++;
                }
            }
            _pluginsMissing.Enabled = countPluginsMissing > 0 || _pluginsMissing.Checked;
            _pluginsAll.Enabled = countPluginsAll > 0 || _pluginsAll.Checked;

            // 6. File Integrity Toggles
            int countComplete = 0, countFileMissing = 0, countUnreadable = 0;
            foreach (SetEntry s in _sets)
            {
                if (_filter.Matches(s, ignoreFileState: true))
                {
                    bool unreadable = s.Error.Length > 0;
                    bool missing = !unreadable && s.MissingFiles > 0;
                    bool complete = !unreadable && !missing;
                    if (complete) countComplete++;
                    if (missing) countFileMissing++;
                    if (unreadable) countUnreadable++;
                }
            }
            _complete.Enabled = countComplete > 0 || _complete.Checked;
            _missing.Enabled = countFileMissing > 0 || _missing.Checked;
            _unreadable.Enabled = countUnreadable > 0 || _unreadable.Checked;

            // 7. Render Preview Toggles
            int countHasRenders = 0, countNoRenders = 0;
            foreach (SetEntry s in _sets)
            {
                if (_filter.Matches(s, ignoreRenders: true))
                {
                    if (s.HasRenders) countHasRenders++;
                    else countNoRenders++;
                }
            }
            _previewHasRenders.Enabled = countHasRenders > 0 || _previewHasRenders.Checked;
            _previewNoRenders.Enabled = countNoRenders > 0 || _previewNoRenders.Checked;
        }

        void ResetAll()
        {
            _from.Box.Text = ""; _to.Box.Text = "";
            _tracksMin.Box.Text = ""; _tracksMax.Box.Text = "";
            _pluginsMin.Box.Text = ""; _pluginsMax.Box.Text = "";
            _pluginsMissing.Checked = false;
            _pluginsAll.Checked = false;
            _versions.SetSelected(new int[0]);
            _roots.SetSelected(new int[0]);
            _scales.SetSelected(new int[0]);
            _tags.SetSelected(new int[0]);
            _complete.Checked = false; _missing.Checked = false; _unreadable.Checked = false;
            _previewHasRenders.Checked = false; _previewNoRenders.Checked = false;
            Collect();
            LayoutRows();
        }

        // --------------------------------------------------------------- раскладка

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutRows();
        }

        void LayoutRows()
        {
            if (_laying) return;
            _laying = true;
            try
            {
                _labels.Clear(); _labelTexts.Clear();

                int pad = Sc(Theme.Pad);
                int labelW = Sc(170);   // «Plugin count» в 140 не помещался и обрезался
                int left = pad + labelW;
                int right = ClientSize.Width - pad;
                int fieldW = right - left;
                int ch = Sc(Theme.ControlH);
                int y = Sc(72);   // без пояснительной строки первому ряду хватает отступа от заголовка
                int rowGap = Sc(14);

                // дата
                int half = (fieldW - Sc(10)) / 2;
                Label("Modified", pad, y, labelW);
                _from.SetBounds(left, y, half, ch);
                _to.SetBounds(left + half + Sc(10), y, half, ch);
                y += ch + rowGap;

                // версии
                Label("Live version", pad, y, labelW);
                y = TagRow(_versions, left, y, fieldW) + rowGap;

                // тональность
                Label("Key root", pad, y, labelW);
                y = TagRow(_roots, left, y, fieldW) + rowGap;

                Label("Scale", pad, y, labelW);
                y = TagRow(_scales, left, y, fieldW) + rowGap;

                Label("Tags", pad, y, labelW);
                y = TagRow(_tags, left, y, fieldW) + rowGap;

                // Счётчики — половинками во всю ширину, как «from/to» выше: раньше пара
                // коротких полей кончалась на своей вертикали, и в одном столбце было
                // четыре разных правых края.
                Label("Tracks", pad, y, labelW);
                _tracksMin.SetBounds(left, y, half, ch);
                _tracksMax.SetBounds(left + half + Sc(10), y, half, ch);
                y += ch + rowGap;

                Label("Plugin count", pad, y, labelW);
                _pluginsMin.SetBounds(left, y, half, ch);
                _pluginsMax.SetBounds(left + half + Sc(10), y, half, ch);
                y += ch + rowGap;

                Label("Plugins", pad, y, labelW);
                _pluginsAll.Location = new Point(left, y);
                _pluginsMissing.Location = new Point(_pluginsAll.Right + Sc(8), y);
                y += ch + rowGap;

                // состояние файлов
                Label("Files", pad, y, labelW);
                int x = left;
                foreach (PillToggle p in new PillToggle[] { _complete, _missing, _unreadable })
                {
                    p.Location = new Point(x, y);
                    x += p.Width + Sc(8);
                }
                y += ch + rowGap;

                // превью
                Label("Preview", pad, y, labelW);
                int px = left;
                foreach (PillToggle p in new PillToggle[] { _previewHasRenders, _previewNoRenders })
                {
                    p.Location = new Point(px, y);
                    px += p.Width + Sc(8);
                }
                y += ch + Sc(22);

                int by = y + Sc(4);
                _apply.Location = new Point(right - _apply.Width, by);
                _reset.Location = new Point(_apply.Left - Sc(10) - _reset.Width, by);

                int need = by + _apply.Height + pad;
                if (ClientSize.Height != need) ClientSize = new Size(ClientSize.Width, need);
                Invalidate();
            }
            finally { _laying = false; }
        }

        void Label(string text, int x, int y, int w)
        {
            _labelTexts.Add(text);
            _labels.Add(new Rectangle(x, y, w - Sc(10), Sc(Theme.ControlH)));
        }

        int TagRow(TagField t, int x, int y, int w)
        {
            t.SetBounds(x, y, w, Sc(Theme.ControlH));
            int h = t.Relayout();
            t.Height = h;
            return y + h;
        }

        // -------------------------------------------------------------- отрисовка

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            for (int i = 0; i < _labels.Count; i++)
                Chrome.DrawText(g, _labelTexts[i], Theme.FBody, _labels[i], Theme.TextDim, Chrome.Left);

            string count = _matches + " sets match";
            Chrome.DrawText(g, count, Theme.FBody,
                new Rectangle(Sc(Theme.Pad), _apply.Top, Math.Max(0, _reset.Left - Sc(40)), _apply.Height),
                _matches == 0 ? Theme.Red : Theme.TextDim, Chrome.Left);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                DialogResult = DialogResult.OK;
                Close();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
