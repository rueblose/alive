using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// The filter window for the plugins tab: by state, format, category/FX type, developer,
    /// and the number of sets a plugin is used in.
    /// </summary>
    public sealed class PluginFiltersDialog : GlassDialog
    {
        readonly PluginFilter _filter = new PluginFilter();
        readonly List<PluginStat> _allStats;

        readonly PillToggle _statusInstalled = new PillToggle();
        readonly PillToggle _statusOtherFormat = new PillToggle();
        readonly PillToggle _statusMissing = new PillToggle();

        readonly PillToggle _fmtVst3 = new PillToggle();
        readonly PillToggle _fmtVst2 = new PillToggle();
        readonly PillToggle _fmtOther = new PillToggle();

        readonly TagField _categoriesTag = new TagField();
        readonly TagField _vendorsTag = new TagField();

        readonly FieldBox _setsMin = new FieldBox();
        readonly FieldBox _setsMax = new FieldBox();

        readonly GlassButton _reset = new GlassButton();
        readonly GlassButton _apply = new GlassButton();

        /// <summary>
        /// Conditions apply live: the plugin list behind the window refreshes on every change.
        /// </summary>
        public event Action Changed;

        readonly List<string> _categoryList = new List<string>();
        readonly List<string> _vendorList = new List<string>();

        readonly List<Rectangle> _labels = new List<Rectangle>();
        readonly List<string> _labelTexts = new List<string>();
        bool _laying;
        int _matches;

        public PluginFilter Result { get { return _filter; } }

        public PluginFiltersDialog(PluginFilter current, List<PluginStat> allStats)
        {
            _allStats = allStats ?? new List<PluginStat>();
            _filter.CopyFrom(current);
            Caption = "Plugin Filters";
            ClientSize = new Size(Sc(740), Sc(540));

            _statusInstalled.Text = "Installed";
            _statusMissing.Text = "Not Installed";
            _statusOtherFormat.Text = "Other Format";

            _statusInstalled.Checked = _filter.StatusInstalled;
            _statusMissing.Checked = _filter.StatusMissing;
            _statusOtherFormat.Checked = _filter.StatusOtherFormat;

            foreach (PillToggle p in new PillToggle[] { _statusInstalled, _statusMissing, _statusOtherFormat })
            {
                p.FitToText();
                p.CheckedChanged += delegate { Collect(); };
                Controls.Add(p);
            }

            _fmtVst3.Text = "VST3";
            _fmtVst2.Text = "VST2";
            _fmtOther.Text = "Other";

            _fmtVst3.Checked = _filter.Formats.Contains("VST3");
            _fmtVst2.Checked = _filter.Formats.Contains("VST2");
            _fmtOther.Checked = _filter.Formats.Contains("Other");

            foreach (PillToggle p in new PillToggle[] { _fmtVst3, _fmtVst2, _fmtOther })
            {
                p.FitToText();
                p.CheckedChanged += delegate { Collect(); };
                Controls.Add(p);
            }

            BuildOptions();

            _categoriesTag.SetOptions(_categoryList);
            _categoriesTag.Placeholder = "any type/category";
            _categoriesTag.SetSelected(IndexesOf(_categoryList, _filter.Categories));

            _vendorsTag.SetOptions(_vendorList);
            _vendorsTag.Placeholder = "any developer";
            _vendorsTag.SetSelected(IndexesOf(_vendorList, _filter.Vendors));

            foreach (TagField t in new TagField[] { _categoriesTag, _vendorsTag })
            {
                t.Changed += delegate { Collect(); LayoutControls(); };
                Controls.Add(t);
            }

            _setsMin.Box.Text = _filter.SetsMin >= 0 ? _filter.SetsMin.ToString() : "";
            _setsMax.Box.Text = _filter.SetsMax >= 0 ? _filter.SetsMax.ToString() : "";
            foreach (FieldBox f in new FieldBox[] { _setsMin, _setsMax })
            {
                f.Box.TextChanged += delegate { Collect(); };
                Controls.Add(f);
            }
            Cue(_setsMin, "from");
            Cue(_setsMax, "to");

            _reset.Text = "Reset";
            _reset.FitToText(18);
            _reset.Click += delegate
            {
                _filter.Clear();
                _statusInstalled.Checked = _statusOtherFormat.Checked = _statusMissing.Checked = false;
                _fmtVst3.Checked = _fmtVst2.Checked = _fmtOther.Checked = false;
                _categoriesTag.SetSelected(new int[0]);
                _vendorsTag.SetSelected(new int[0]);
                _setsMin.Box.Text = _setsMax.Box.Text = "";
                Collect();
                LayoutControls();
            };
            Controls.Add(_reset);

            _apply.Text = "Ok";
            _apply.Primary = true;
            _apply.Click += delegate
            {
                DialogResult = DialogResult.OK;
                Close();
            };
            _apply.FitToText(18);
            Controls.Add(_apply);

            int pairW = Math.Max(_reset.Width, _apply.Width);
            _reset.Width = _apply.Width = pairW;

            LayoutControls();
            Collect();
        }

        void Cue(FieldBox f, string cueText)
        {
            f.Cue = cueText;
            f.Invalidate();
        }

        void BuildOptions()
        {
            HashSet<string> cats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> vends = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (PluginStat st in _allStats)
            {
                if (!string.IsNullOrEmpty(st.FxType)) cats.Add(st.FxType);
                if (!string.IsNullOrEmpty(st.Vendor)) vends.Add(st.Vendor);
            }

            _categoryList.AddRange(cats);
            _categoryList.Sort(StringComparer.OrdinalIgnoreCase);

            _vendorList.AddRange(vends);
            _vendorList.Sort(StringComparer.OrdinalIgnoreCase);
        }

        static List<int> IndexesOf(List<string> options, HashSet<string> selected)
        {
            List<int> result = new List<int>();
            for (int i = 0; i < options.Count; i++)
                if (selected.Contains(options[i])) result.Add(i);
            return result;
        }

        void Collect()
        {
            _filter.StatusInstalled = _statusInstalled.Checked;
            _filter.StatusOtherFormat = _statusOtherFormat.Checked;
            _filter.StatusMissing = _statusMissing.Checked;

            _filter.Formats.Clear();
            if (_fmtVst3.Checked) _filter.Formats.Add("VST3");
            if (_fmtVst2.Checked) _filter.Formats.Add("VST2");
            if (_fmtOther.Checked) _filter.Formats.Add("Other");

            _filter.Categories.Clear();
            foreach (int idx in _categoriesTag.Selected)
                if (idx >= 0 && idx < _categoryList.Count) _filter.Categories.Add(_categoryList[idx]);

            _filter.Vendors.Clear();
            foreach (int idx in _vendorsTag.Selected)
                if (idx >= 0 && idx < _vendorList.Count) _filter.Vendors.Add(_vendorList[idx]);

            int sMin, sMax;
            _filter.SetsMin = int.TryParse(_setsMin.Box.Text.Trim(), out sMin) && sMin >= 0 ? sMin : -1;
            _filter.SetsMax = int.TryParse(_setsMax.Box.Text.Trim(), out sMax) && sMax >= 0 ? sMax : -1;

            _matches = 0;
            foreach (PluginStat st in _allStats)
                if (_filter.Matches(st)) _matches++;

            UpdateFacets();
            Invalidate();
            if (Changed != null) Changed();
        }

        void UpdateFacets()
        {
            if (_allStats == null) return;

            // 1. Categories / FX Type
            _categoriesTag.DisabledOptions.Clear();
            HashSet<string> validCats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginStat st in _allStats)
            {
                if (_filter.Matches(st, ignoreCategories: true))
                {
                    string cat = st.FxType.Length > 0 ? st.FxType : "Other";
                    validCats.Add(cat);
                }
            }
            for (int i = 0; i < _categoryList.Count; i++)
            {
                if (!validCats.Contains(_categoryList[i]))
                    _categoriesTag.DisabledOptions.Add(i);
            }

            // 2. Vendors / Developers
            _vendorsTag.DisabledOptions.Clear();
            HashSet<string> validVendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (PluginStat st in _allStats)
            {
                if (_filter.Matches(st, ignoreVendors: true))
                {
                    string v = st.Vendor.Length > 0 ? st.Vendor : "Unknown";
                    validVendors.Add(v);
                }
            }
            for (int i = 0; i < _vendorList.Count; i++)
            {
                if (!validVendors.Contains(_vendorList[i]))
                    _vendorsTag.DisabledOptions.Add(i);
            }

            // 3. Status Pill Toggles
            int countInstalled = 0, countOtherFormat = 0, countMissing = 0;
            foreach (PluginStat st in _allStats)
            {
                if (_filter.Matches(st, ignoreStatus: true))
                {
                    if (st.Match == MatchKind.Exact) countInstalled++;
                    if (st.Match == MatchKind.OtherFormat) countOtherFormat++;
                    if (st.Match == MatchKind.Missing) countMissing++;
                }
            }
            _statusInstalled.Enabled = countInstalled > 0 || _statusInstalled.Checked;
            _statusOtherFormat.Enabled = countOtherFormat > 0 || _statusOtherFormat.Checked;
            _statusMissing.Enabled = countMissing > 0 || _statusMissing.Checked;

            // 4. Format Pill Toggles
            int countVst3 = 0, countVst2 = 0, countOther = 0;
            foreach (PluginStat st in _allStats)
            {
                if (_filter.Matches(st, ignoreFormats: true))
                {
                    string fmt = st.Format ?? "";
                    bool isVst3 = string.Equals(fmt, "VST3", StringComparison.OrdinalIgnoreCase);
                    bool isVst2 = string.Equals(fmt, "VST2", StringComparison.OrdinalIgnoreCase);
                    if (isVst3) countVst3++;
                    else if (isVst2) countVst2++;
                    else countOther++;
                }
            }
            _fmtVst3.Enabled = countVst3 > 0 || _fmtVst3.Checked;
            _fmtVst2.Enabled = countVst2 > 0 || _fmtVst2.Checked;
            _fmtOther.Enabled = countOther > 0 || _fmtOther.Checked;
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutControls(); }

        void LayoutControls()
        {
            if (_laying || ClientSize.Width <= 0) return;
            _laying = true;
            try
            {
                _labels.Clear();
                _labelTexts.Clear();

                int pad = Sc(Theme.Pad);
                int y = Sc(70);
                int labelW = Sc(140);
                int left = pad + labelW;
                int right = ClientSize.Width - pad;
                int contentW = right - left;
                int rowGap = Sc(14);
                int ch = Sc(Theme.ControlH);

                // State
                Label("Status", pad, y, labelW);
                int sx = left;
                foreach (PillToggle p in new PillToggle[] { _statusInstalled, _statusMissing, _statusOtherFormat })
                {
                    p.Location = new Point(sx, y);
                    sx += p.Width + Sc(8);
                }
                y += ch + rowGap;

                // Format
                Label("Format", pad, y, labelW);
                int fx = left;
                foreach (PillToggle p in new PillToggle[] { _fmtVst3, _fmtVst2, _fmtOther })
                {
                    p.Location = new Point(fx, y);
                    fx += p.Width + Sc(8);
                }
                y += ch + rowGap;

                // FX type / category
                Label("FX Type", pad, y, labelW);
                y = TagRow(_categoriesTag, left, y, contentW) + rowGap;

                // Developer
                Label("Developer", pad, y, labelW);
                y = TagRow(_vendorsTag, left, y, contentW) + rowGap;

                // Set count
                Label("Sets Count", pad, y, labelW);
                int numW = Sc(120);
                _setsMin.SetBounds(left, y, numW, ch);
                _setsMax.SetBounds(left + numW + Sc(12), y, numW, ch);
                y += ch + Sc(24);

                // Reset and apply buttons
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

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            for (int i = 0; i < _labels.Count; i++)
                Chrome.DrawText(g, _labelTexts[i], Theme.FBody, _labels[i], Theme.TextDim, Chrome.Left);

            string count = _matches + " plugins match";
            Chrome.DrawText(g, count, Theme.FBody,
                new Rectangle(Sc(Theme.Pad), _apply.Top, Math.Max(0, _reset.Left - Sc(40)), _apply.Height),
                _matches == 0 ? Theme.Red : Theme.TextDim, Chrome.Left);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                Collect();
                DialogResult = DialogResult.OK;
                Close();
                e.Handled = e.SuppressKeyPress = true;
                return;
            }
            base.OnKeyDown(e);
        }
    }
}
