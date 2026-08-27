using System;
using System.Collections.Generic;

namespace AbletonManager
{
    public sealed class PluginFilter
    {
        public bool StatusInstalled;
        public bool StatusOtherFormat;
        public bool StatusMissing;
        public bool StatusUnused;

        public HashSet<string> Formats = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Vendors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Categories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public int SetsMin = -1;
        public int SetsMax = -1;

        public int ActiveCount
        {
            get
            {
                int count = 0;
                if (StatusInstalled || StatusOtherFormat || StatusMissing || StatusUnused) count++;
                if (Formats.Count > 0) count++;
                if (Vendors.Count > 0) count++;
                if (Categories.Count > 0) count++;
                if (SetsMin >= 0 || SetsMax >= 0) count++;
                return count;
            }
        }

        public bool IsEmpty { get { return ActiveCount == 0; } }

        public void Clear()
        {
            StatusInstalled = StatusOtherFormat = StatusMissing = StatusUnused = false;
            Formats.Clear();
            Vendors.Clear();
            Categories.Clear();
            SetsMin = SetsMax = -1;
        }

        public void CopyFrom(PluginFilter src)
        {
            if (src == null) { Clear(); return; }
            StatusInstalled = src.StatusInstalled;
            StatusOtherFormat = src.StatusOtherFormat;
            StatusMissing = src.StatusMissing;
            StatusUnused = src.StatusUnused;

            Formats = new HashSet<string>(src.Formats, StringComparer.OrdinalIgnoreCase);
            Vendors = new HashSet<string>(src.Vendors, StringComparer.OrdinalIgnoreCase);
            Categories = new HashSet<string>(src.Categories, StringComparer.OrdinalIgnoreCase);

            SetsMin = src.SetsMin;
            SetsMax = src.SetsMax;
        }

        public bool Matches(PluginStat st, bool ignoreStatus = false, bool ignoreUnused = false, bool ignoreFormats = false, bool ignoreVendors = false, bool ignoreCategories = false)
        {
            if (st == null) return false;

            // Статус (Установлены / Другой формат / Не установлены)
            if (!ignoreStatus && (StatusInstalled || StatusOtherFormat || StatusMissing))
            {
                bool statusPass = false;
                if (StatusInstalled && st.Match == MatchKind.Exact) statusPass = true;
                if (StatusOtherFormat && st.Match == MatchKind.OtherFormat) statusPass = true;
                if (StatusMissing && st.Match == MatchKind.Missing) statusPass = true;
                if (!statusPass) return false;
            }

            // Не используются (И)
            if (!ignoreUnused && StatusUnused && !st.IsUnused) return false;

            // Формат
            if (!ignoreFormats && Formats.Count > 0)
            {
                bool match = false;
                string fmt = st.Format ?? "";
                bool isVst3 = string.Equals(fmt, "VST3", StringComparison.OrdinalIgnoreCase);
                bool isVst2 = string.Equals(fmt, "VST2", StringComparison.OrdinalIgnoreCase);

                if (Formats.Contains("VST3") && isVst3) match = true;
                if (Formats.Contains("VST2") && isVst2) match = true;
                if (Formats.Contains("Other") && !isVst3 && !isVst2) match = true;
                if (!match) return false;
            }

            // Разработчик
            if (!ignoreVendors && Vendors.Count > 0)
            {
                string v = st.Vendor.Length > 0 ? st.Vendor : "Unknown";
                if (!Vendors.Contains(v)) return false;
            }

            // Категория / Тип FX
            if (!ignoreCategories && Categories.Count > 0)
            {
                string cat = st.FxType.Length > 0 ? st.FxType : "Other";
                if (!Categories.Contains(cat)) return false;
            }

            // Число сетов
            if (SetsMin >= 0 && st.Sets < SetsMin) return false;
            if (SetsMax >= 0 && st.Sets > SetsMax) return false;

            return true;
        }
    }
}
