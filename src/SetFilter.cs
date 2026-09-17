using System;
using System.Collections.Generic;
using System.Globalization;

namespace AbletonManager
{
    /// <summary>
    /// The set of conditions for the sets list. An empty condition filters nothing out, so "no
    /// filters" and "filters reset" are one and the same state.
    /// </summary>
    public sealed class SetFilter
    {
        public DateTime? From, To;                                  // by modification date
        public readonly List<string> Versions = new List<string>(); // ShortVersion, «12.4.3»
        // 0..11 (C..B); -1 — a set with no key; -2 — "any key" (it has one at all)
        public readonly List<int> KeyRoots = new List<int>();
        public readonly List<int> KeyScales = new List<int>();      // scale index
        public readonly List<string> Tags = new List<string>();     // project tags, any of the selected ones
        public int TracksMin = -1, TracksMax = -1;
        public int PluginsMin = -1, PluginsMax = -1;
        public bool FilesComplete, FilesMissing, FilesUnreadable;   // none = any
        public bool PluginsMissingOnly;                             // only sets with holes in their plugins
        public bool PluginsAllInstalled;                           // only sets where everything is installed
        public bool PreviewHasRenders, PreviewNoRenders;

        public bool IsEmpty { get { return ActiveCount == 0; } }

        /// <summary>How many condition groups are on — this number sits on the
        /// button.</summary>
        public int ActiveCount
        {
            get
            {
                int n = 0;
                if (From.HasValue || To.HasValue) n++;
                if (Versions.Count > 0) n++;
                if (KeyRoots.Count > 0 || KeyScales.Count > 0) n++;
                if (Tags.Count > 0) n++;
                if (TracksMin >= 0 || TracksMax >= 0) n++;
                if (PluginsMin >= 0 || PluginsMax >= 0 || PluginsMissingOnly || PluginsAllInstalled) n++;
                if (FilesComplete || FilesMissing || FilesUnreadable) n++;
                if (PreviewHasRenders || PreviewNoRenders) n++;
                return n;
            }
        }

        public bool Matches(SetEntry s, bool ignoreVersions = false, bool ignoreKeyRoots = false, bool ignoreKeyScales = false,
                            bool ignorePluginsState = false, bool ignoreFileState = false, bool ignoreRenders = false,
                            bool ignoreTags = false)
        {
            DateTime modified = s.Modified.ToLocalTime();
            if (From.HasValue && modified < From.Value) return false;
            if (To.HasValue && modified > To.Value) return false;

            if (!ignoreVersions && Versions.Count > 0 && !Versions.Contains(s.ShortVersion)) return false;

            // Keys are compared by number, not by text: "C#" and "Db" are one note, and how it
            // gets written depends on the PreferFlatRootNote box in the set itself. Special
            // values: -1 — "no key", -2 — "any key" (it has one at all).
            if (!ignoreKeyRoots && KeyRoots.Count > 0)
            {
                bool ok = KeyRoots.Contains(s.ScaleRoot)
                       || (s.ScaleRoot >= 0 && KeyRoots.Contains(-2));
                if (!ok) return false;
            }
            if (!ignoreKeyScales && KeyScales.Count > 0 && !KeyScales.Contains(s.ScaleIndex)) return false;

            // Several tags mean "or", just like versions: pick "drum" and "vocal" and we show
            // everything marked with at least one of them. Tags live in a separate file rather
            // than in the set, so we only reach for them when the filter on them is actually
            // on.
            if (!ignoreTags && Tags.Count > 0)
            {
                bool any = false;
                foreach (string t in ProjectMeta.TagsOf(s.ProjectDir))
                    if (Tags.Contains(t)) { any = true; break; }
                if (!any) return false;
            }

            if (TracksMin >= 0 && s.Tracks < TracksMin) return false;
            if (TracksMax >= 0 && s.Tracks > TracksMax) return false;
            if (PluginsMin >= 0 && s.Plugins.Length < PluginsMin) return false;
            if (PluginsMax >= 0 && s.Plugins.Length > PluginsMax) return false;

            if (!ignorePluginsState)
            {
                if (PluginsMissingOnly && s.MissingPlugins == 0) return false;
                if (PluginsAllInstalled && s.MissingPlugins > 0) return false;
            }

            if (!ignoreFileState && (FilesComplete || FilesMissing || FilesUnreadable))
            {
                bool unreadable = s.Error.Length > 0;
                bool missing = !unreadable && s.MissingFiles > 0;
                bool complete = !unreadable && !missing;
                if (!((FilesComplete && complete) || (FilesMissing && missing)
                                                  || (FilesUnreadable && unreadable))) return false;
            }

            if (!ignoreRenders && (PreviewHasRenders || PreviewNoRenders))
            {
                bool has = s.HasRenders;
                bool no = !s.HasRenders;
                if (!((PreviewHasRenders && has) || (PreviewNoRenders && no))) return false;
            }

            return true;
        }

        public void Clear()
        {
            From = To = null;
            Versions.Clear(); KeyRoots.Clear(); KeyScales.Clear(); Tags.Clear();
            TracksMin = TracksMax = PluginsMin = PluginsMax = -1;
            FilesComplete = FilesMissing = FilesUnreadable = false;
            PluginsMissingOnly = PluginsAllInstalled = false;
            PreviewHasRenders = PreviewNoRenders = false;
        }

        public void CopyFrom(SetFilter o)
        {
            Clear();
            From = o.From; To = o.To;
            Versions.AddRange(o.Versions);
            KeyRoots.AddRange(o.KeyRoots);
            KeyScales.AddRange(o.KeyScales);
            Tags.AddRange(o.Tags);
            TracksMin = o.TracksMin; TracksMax = o.TracksMax;
            PluginsMin = o.PluginsMin; PluginsMax = o.PluginsMax;
            PluginsMissingOnly = o.PluginsMissingOnly;
            PluginsAllInstalled = o.PluginsAllInstalled;
            FilesComplete = o.FilesComplete;
            FilesMissing = o.FilesMissing;
            FilesUnreadable = o.FilesUnreadable;
            PreviewHasRenders = o.PreviewHasRenders;
            PreviewNoRenders = o.PreviewNoRenders;
        }

        // -------------------------------------------------------------------- dates

        /// <summary>
        /// Parses a date leniently: "2026" is the whole year, "2026-08" the whole month,
        /// "2026-08-07" the day. For an upper bound the end of the period is taken, or "up to
        /// 2026" would cut off everything past the first of January.
        /// </summary>
        public static DateTime? ParseDate(string text, bool upperBound)
        {
            if (text == null) return null;
            string t = text.Trim().Replace('/', '-').Replace('.', '-');
            if (t.Length == 0) return null;

            string[] parts = t.Split('-');
            int year, month = 1, day = 1;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out year)) return null;
            if (year < 1900 || year > 2200) return null;

            bool hasMonth = parts.Length > 1 && int.TryParse(parts[1], NumberStyles.Integer,
                                                             CultureInfo.InvariantCulture, out month);
            bool hasDay = parts.Length > 2 && int.TryParse(parts[2], NumberStyles.Integer,
                                                          CultureInfo.InvariantCulture, out day);
            if (hasMonth && (month < 1 || month > 12)) return null;
            if (!hasMonth) month = 1;
            if (hasDay && (day < 1 || day > DateTime.DaysInMonth(year, month))) return null;
            if (!hasDay) day = 1;

            DateTime d = new DateTime(year, month, day);
            if (!upperBound) return d;

            if (!hasMonth) return new DateTime(year, 12, 31, 23, 59, 59);
            if (!hasDay) return new DateTime(year, month, DateTime.DaysInMonth(year, month), 23, 59, 59);
            return d.AddDays(1).AddSeconds(-1);
        }

        public static string FormatDate(DateTime? d)
        {
            return d.HasValue ? d.Value.ToString("yyyy-MM-dd") : "";
        }

        /// <summary>A number from the field; empty or junk means "no limit".</summary>
        public static int ParseCount(string text)
        {
            if (text == null) return -1;
            int v;
            if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) return -1;
            return v < 0 ? -1 : v;
        }

        public static string FormatCount(int v) { return v < 0 ? "" : v.ToString(CultureInfo.InvariantCulture); }
    }
}
