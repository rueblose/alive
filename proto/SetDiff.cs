using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace Reel
{
    public enum DiffKind { Header, Added, Removed, Changed, Info, Same }

    public sealed class DiffLine
    {
        public DiffKind Kind;
        public string Text = "";
        public int Indent;

        public DiffLine() { }
        public DiffLine(DiffKind k, string text, int indent)
        {
            Kind = k; Text = text; Indent = indent;
        }
    }

    /// <summary>
    /// Разница между двумя слепками сета — в тех словах, которыми продюсер описал бы
    /// правку сам: «добавил трек», «снял Serum с баса», «темп 145 -> 150».
    ///
    /// Текстовый diff тут не работает в принципе: .als это gzip поверх XML, где при
    /// любом сохранении меняются десятки тысяч строк — позиции окон, состояния
    /// свёрнутости, случайные Id. Поэтому сравниваются не строки, а разобранная
    /// структура, и на глаза попадает только то, что человек действительно делал.
    /// </summary>
    public static class SetDiff
    {
        const double DbEpsilon = 0.09;    // тише, чем слышно, и мельче, чем ручка фейдера
        const double PanEpsilon = 0.005;
        const int MaxSampleLines = 8;

        public static List<DiffLine> Compare(SetModel older, SetModel newer)
        {
            List<DiffLine> lines = new List<DiffLine>();

            if (older == null || newer == null)
            {
                lines.Add(new DiffLine(DiffKind.Info, "Nothing to compare with", 0));
                return lines;
            }
            if (newer.Error != null)
            {
                lines.Add(new DiffLine(DiffKind.Removed, "Cannot read set: " + newer.Error, 0));
                return lines;
            }

            // ------------------------------------------------------------ сет целиком
            List<DiffLine> head = new List<DiffLine>();

            if (!Near(older.Tempo, newer.Tempo, 0.001))
                head.Add(new DiffLine(DiffKind.Changed,
                    "Tempo " + Num(older.Tempo) + " -> " + Num(newer.Tempo), 0));

            if (older.Key != newer.Key)
                head.Add(new DiffLine(DiffKind.Changed, "Key " + Show(older.Key) + " -> " + Show(newer.Key), 0));

            if (older.Creator != newer.Creator && newer.Creator.Length > 0)
                head.Add(new DiffLine(DiffKind.Changed,
                    "Saved by " + Show(older.Creator) + " -> " + newer.Creator, 0));

            // ------------------------------------------------------------ треки
            Dictionary<string, TrackEntry> oldByKey = Index(older.Tracks);
            Dictionary<string, TrackEntry> newByKey = Index(newer.Tracks);

            List<DiffLine> body = new List<DiffLine>();

            foreach (TrackEntry t in newer.Tracks)
            {
                TrackEntry prev;
                if (!newByKey.ContainsKey(t.Key)) continue;               // дубль ключа, уже показан
                if (!oldByKey.TryGetValue(t.Key, out prev))
                {
                    body.Add(new DiffLine(DiffKind.Added, "+ track  " + t.Label + "  " + Summary(t), 0));
                    foreach (DeviceEntry d in t.Devices)
                        body.Add(new DiffLine(DiffKind.Added, "+ " + d.Label, 1));
                    continue;
                }
                AppendTrackDiff(prev, t, body);
            }

            foreach (TrackEntry t in older.Tracks)
                if (!newByKey.ContainsKey(t.Key))
                    body.Add(new DiffLine(DiffKind.Removed, "- track  " + t.Label + "  " + Summary(t), 0));

            lines.AddRange(head);
            lines.AddRange(body);

            if (lines.Count == 0)
            {
                // Двумя строками, а не одной длинной: в панели она стоит в один ряд и
                // длинную обрезало бы многоточием ровно на том месте, где начинается
                // единственно полезное — почему изменений нет.
                lines.Add(new DiffLine(DiffKind.Same, "No structural changes", 0));
                lines.Add(new DiffLine(DiffKind.Same,
                    "only window layout, view state or automation", 0));
            }

            return lines;
        }

        /// <summary>Что изменилось внутри одного трека. Заголовок трека пишется только
        /// если под ним действительно есть строки.</summary>
        static void AppendTrackDiff(TrackEntry a, TrackEntry b, List<DiffLine> outLines)
        {
            List<DiffLine> inner = new List<DiffLine>();

            if (a.Name != b.Name && !OnlyRenumbered(a.Name, b.Name))
                inner.Add(new DiffLine(DiffKind.Changed, "renamed from " + Show(a.Name), 1));

            if (a.Muted != b.Muted)
                inner.Add(new DiffLine(DiffKind.Changed, b.Muted ? "muted" : "unmuted", 1));

            if (!Near(Db(a.Volume), Db(b.Volume), DbEpsilon))
                inner.Add(new DiffLine(DiffKind.Changed,
                    "volume " + TrackEntry.Db(a.Volume) + " -> " + TrackEntry.Db(b.Volume), 1));

            if (!Near(a.Pan, b.Pan, PanEpsilon))
                inner.Add(new DiffLine(DiffKind.Changed, "pan " + Pan(a.Pan) + " -> " + Pan(b.Pan), 1));

            DeviceDiff(a, b, inner);

            if (a.ArrClips != b.ArrClips)
                inner.Add(new DiffLine(DiffKind.Changed,
                    "arrangement clips " + a.ArrClips + " -> " + b.ArrClips, 1));
            if (a.SessionClips != b.SessionClips)
                inner.Add(new DiffLine(DiffKind.Changed,
                    "session clips " + a.SessionClips + " -> " + b.SessionClips, 1));
            if (a.Notes != b.Notes)
                inner.Add(new DiffLine(DiffKind.Changed,
                    "notes " + a.Notes + " -> " + b.Notes + " (" + Signed(b.Notes - a.Notes) + ")", 1));

            SampleDiff(a, b, inner);

            if (inner.Count == 0) return;
            outLines.Add(new DiffLine(DiffKind.Header, b.Label + "   " + b.Kind, 0));
            outLines.AddRange(inner);
        }

        /// <summary>
        /// «8-Ave A#2 A long» -> «5-Ave A#2 A long». Треки, которым имя дала сама Live,
        /// несут в начале порядковый номер, и он съезжает у половины сета, стоит удалить
        /// один трек. Показывать это как переименование — значит утопить настоящую
        /// правку («убрал трек perc bass») в десятке строк, которых человек не делал.
        /// </summary>
        static bool OnlyRenumbered(string a, string b)
        {
            string ta = StripNumber(a), tb = StripNumber(b);
            if (ta == null || tb == null) return false;
            return ta.Length > 0 && string.Equals(ta, tb, StringComparison.Ordinal);
        }

        static string StripNumber(string name)
        {
            int i = 0;
            while (i < name.Length && char.IsDigit(name[i])) i++;
            if (i == 0 || i >= name.Length || name[i] != '-') return null;
            return name.Substring(i + 1);
        }

        // ------------------------------------------------------------------ устройства

        static void DeviceDiff(TrackEntry a, TrackEntry b, List<DiffLine> inner)
        {
            Dictionary<string, int> before = Counts(a.Devices);
            Dictionary<string, int> after = Counts(b.Devices);

            foreach (KeyValuePair<string, int> kv in after)
            {
                int had; before.TryGetValue(kv.Key, out had);
                for (int i = 0; i < kv.Value - had; i++)
                    inner.Add(new DiffLine(DiffKind.Added, "+ " + kv.Key, 1));
            }
            foreach (KeyValuePair<string, int> kv in before)
            {
                int now; after.TryGetValue(kv.Key, out now);
                for (int i = 0; i < kv.Value - now; i++)
                    inner.Add(new DiffLine(DiffKind.Removed, "- " + kv.Key, 1));
            }

            // Включение и выключение устройства — правка того же порядка, что и его
            // удаление: в миксе слышно ровно то же самое.
            Dictionary<string, int> offBefore = OffCounts(a.Devices);
            Dictionary<string, int> offAfter = OffCounts(b.Devices);
            foreach (KeyValuePair<string, int> kv in offAfter)
            {
                int had; offBefore.TryGetValue(kv.Key, out had);
                if (kv.Value > had && after.ContainsKey(kv.Key) && before.ContainsKey(kv.Key))
                    inner.Add(new DiffLine(DiffKind.Changed, kv.Key + " turned off", 1));
            }
            foreach (KeyValuePair<string, int> kv in offBefore)
            {
                int now; offAfter.TryGetValue(kv.Key, out now);
                if (kv.Value > now && after.ContainsKey(kv.Key) && before.ContainsKey(kv.Key))
                    inner.Add(new DiffLine(DiffKind.Changed, kv.Key + " turned on", 1));
            }
        }

        static Dictionary<string, int> Counts(List<DeviceEntry> devices)
        {
            Dictionary<string, int> d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DeviceEntry x in devices)
            {
                int n; d.TryGetValue(x.Label, out n); d[x.Label] = n + 1;
            }
            return d;
        }

        static Dictionary<string, int> OffCounts(List<DeviceEntry> devices)
        {
            Dictionary<string, int> d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (DeviceEntry x in devices)
            {
                if (x.On) continue;
                int n; d.TryGetValue(x.Label, out n); d[x.Label] = n + 1;
            }
            return d;
        }

        // ------------------------------------------------------------------ сэмплы

        static void SampleDiff(TrackEntry a, TrackEntry b, List<DiffLine> inner)
        {
            Emit(Except(b.Samples, a.SampleSet), DiffKind.Added, "+ sample ", inner);
            Emit(Except(a.Samples, b.SampleSet), DiffKind.Removed, "- sample ", inner);
        }

        static List<SampleRefEntry> Except(List<SampleRefEntry> items,
                                           Dictionary<string, SampleRefEntry> other)
        {
            List<SampleRefEntry> res = new List<SampleRefEntry>();
            foreach (SampleRefEntry s in items) if (!other.ContainsKey(s.Key)) res.Add(s);
            return res;
        }

        static void Emit(List<SampleRefEntry> items, DiffKind kind, string prefix, List<DiffLine> inner)
        {
            int shown = Math.Min(items.Count, MaxSampleLines);
            for (int i = 0; i < shown; i++)
                inner.Add(new DiffLine(kind, prefix + items[i].Name, 1));
            if (items.Count > shown)
                inner.Add(new DiffLine(kind, prefix + "and " + (items.Count - shown) + " more", 1));
        }

        // ------------------------------------------------------------------ мелочи

        static Dictionary<string, TrackEntry> Index(List<TrackEntry> tracks)
        {
            Dictionary<string, TrackEntry> d = new Dictionary<string, TrackEntry>(StringComparer.Ordinal);
            foreach (TrackEntry t in tracks) if (!d.ContainsKey(t.Key)) d[t.Key] = t;
            return d;
        }

        static string Summary(TrackEntry t)
        {
            int clips = t.ArrClips + t.SessionClips;
            string s = clips + (clips == 1 ? " clip" : " clips");
            if (t.Devices.Count > 0)
                s += ", " + t.Devices.Count + (t.Devices.Count == 1 ? " device" : " devices");
            return "(" + s + ")";
        }

        static double Db(double linear)
        {
            if (double.IsNaN(linear)) return double.NaN;
            if (linear <= 0.0000001) return -144.0;
            return 20.0 * Math.Log10(linear);
        }

        static bool Near(double a, double b, double eps)
        {
            if (double.IsNaN(a) && double.IsNaN(b)) return true;
            if (double.IsNaN(a) || double.IsNaN(b)) return false;
            return Math.Abs(a - b) < eps;
        }

        static string Num(double v)
        {
            return v.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string Pan(double v)
        {
            if (Math.Abs(v) < PanEpsilon) return "C";
            int pct = (int)Math.Round(Math.Abs(v) * 100.0);
            return (v < 0 ? "L" : "R") + pct;
        }

        static string Signed(int v) { return v > 0 ? "+" + v : v.ToString(CultureInfo.InvariantCulture); }

        static string Show(string s) { return string.IsNullOrEmpty(s) ? "-" : s; }
    }
}
