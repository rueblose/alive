using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using AbletonManager;

namespace Reel
{
    public enum DepState { Ok, Missing, Unknown }

    public sealed class DepItem
    {
        public string Group = "";     // Samples | Plugins | Packs
        public string Name = "";
        public string Detail = "";
        public DepState State;
        public int Count = 1;         // сколько раз встречается в сете
        public bool IsHeader;
    }

    /// <summary>
    /// Что нужно машине, чтобы этот сет открылся целиком: сэмплы, плагины, паки.
    ///
    /// Смысл в том, чтобы узнать это ДО открытия. Сейчас единственный способ — открыть
    /// сет в Live и прочитать её собственную жалобу, а она приходит поздно, ничего не
    /// перечисляет и не говорит, где искать. Прототип отвечает на тот же вопрос за
    /// секунду и по любому снапшоту, включая старый.
    /// </summary>
    public sealed class DependencyReport
    {
        public readonly List<DepItem> Items = new List<DepItem>();
        public string SetName = "";
        public int TotalSamples, MissingSamples;
        public int TotalPlugins, MissingPlugins;
        public int MissingPacks;
        public bool PluginsUnknown;
        public string Error;

        public bool AllGood
        {
            get { return Error == null && MissingSamples == 0 && MissingPlugins == 0 && MissingPacks == 0; }
        }

        /// <summary>
        /// projectDir — папка НАСТОЯЩЕГО проекта, а не хранилища: снапшот лежит в .reel
        /// под именем-хешем, и относительные пути внутри него отсчитываются всё равно от
        /// исходной папки проекта.
        /// </summary>
        /// <param name="displayName">Как сет называется для человека. Снапшот лежит в
        /// хранилище под именем-хешем, и показывать это имя в отчёте бессмысленно.</param>
        public static DependencyReport Build(string alsPath, string projectDir,
                                             LiveEnvironment env, PluginInventory inv,
                                             string displayName)
        {
            DependencyReport rep = new DependencyReport();
            rep.SetName = string.IsNullOrEmpty(displayName) ? Path.GetFileName(alsPath) : displayName;

            AlsInfo info = AlsFile.Read(alsPath);
            if (info.Error != null) { rep.Error = info.Error; return rep; }

            // ---------------------------------------------------------------- сэмплы
            Dictionary<string, DepItem> samples = new Dictionary<string, DepItem>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, DepItem> packs = new Dictionary<string, DepItem>(StringComparer.OrdinalIgnoreCase);
            List<DepItem> sampleOrder = new List<DepItem>();
            List<DepItem> packOrder = new List<DepItem>();

            RefResolver.BeginScan();
            try
            {
                foreach (FileRefInfo fr in info.Files)
                {
                    if (!fr.IsSampleDependency) continue;      // остальное Live хранит внутри сета

                    ResolvedRef rr = RefResolver.Resolve(fr, projectDir, env);
                    if (rr.Status == RefStatus.Empty) continue;

                    if (rr.Status == RefStatus.MissingPack)
                    {
                        string packName = rr.Note.Length > 0 ? rr.Note : "unknown pack";
                        DepItem p;
                        if (!packs.TryGetValue(packName, out p))
                        {
                            p = new DepItem();
                            p.Group = "Packs"; p.Name = packName;
                            p.Detail = "pack is not installed"; p.State = DepState.Missing;
                            packs[packName] = p; packOrder.Add(p);
                        }
                        else p.Count++;
                        continue;
                    }

                    string key = rr.ResolvedPath.Length > 0 ? rr.ResolvedPath
                               : (fr.RelativePath.Length > 0 ? fr.RelativePath : fr.AbsolutePath);
                    DepItem item;
                    if (samples.TryGetValue(key, out item)) { item.Count++; continue; }

                    item = new DepItem();
                    item.Group = "Samples";
                    item.Name = NameOf(key);
                    item.Detail = rr.Status == RefStatus.Found ? Folder(rr.ResolvedPath) : key;
                    item.State = rr.Status == RefStatus.Found ? DepState.Ok : DepState.Missing;
                    samples[key] = item; sampleOrder.Add(item);
                }
            }
            finally { RefResolver.EndScan(); }

            foreach (DepItem i in sampleOrder)
            {
                rep.TotalSamples++;
                if (i.State == DepState.Missing) rep.MissingSamples++;
            }
            foreach (DepItem i in packOrder) rep.MissingPacks++;

            // ---------------------------------------------------------------- плагины
            rep.PluginsUnknown = inv == null || inv.IsEmpty;
            Dictionary<string, DepItem> plugins = new Dictionary<string, DepItem>(StringComparer.OrdinalIgnoreCase);
            List<DepItem> pluginOrder = new List<DepItem>();

            foreach (PluginRef pr in info.Plugins)
            {
                DepItem item;
                if (plugins.TryGetValue(pr.Key, out item)) { item.Count++; continue; }

                item = new DepItem();
                item.Group = "Plugins";
                item.Name = pr.Name;

                if (rep.PluginsUnknown)
                {
                    item.State = DepState.Unknown;
                    item.Detail = Format(pr.Kind) + " - Live has no plugin database on this machine";
                }
                else
                {
                    PluginMatch m = inv.Match(pr.Uid, pr.Name);
                    if (m.Kind == MatchKind.Exact)
                    {
                        item.State = DepState.Ok;
                        item.Detail = Format(pr.Kind) + Vendor(pr, m.Plugin);
                    }
                    else if (m.Kind == MatchKind.OtherFormat)
                    {
                        // Тот же плагин, но установлен в другом формате: сет откроется,
                        // однако Live подставит другой экземпляр, и пресет может не сесть.
                        item.State = DepState.Unknown;
                        item.Detail = Format(pr.Kind) + " - installed as " + m.Plugin.Format;
                    }
                    else
                    {
                        item.State = DepState.Missing;
                        item.Detail = Format(pr.Kind) + Vendor(pr, null) + " - not installed";
                    }
                }

                plugins[pr.Key] = item; pluginOrder.Add(item);
            }

            foreach (DepItem i in pluginOrder)
            {
                rep.TotalPlugins++;
                if (i.State == DepState.Missing) rep.MissingPlugins++;
            }

            // ---------------------------------------------------------------- порядок
            //
            // Потерянное — наверх: ради него отчёт и открывают.
            Sort(sampleOrder); Sort(pluginOrder);

            Section(rep, "Plugins", pluginOrder, rep.MissingPlugins);
            Section(rep, "Samples", sampleOrder, rep.MissingSamples);
            Section(rep, "Packs", packOrder, rep.MissingPacks);

            return rep;
        }

        static void Section(DependencyReport rep, string title, List<DepItem> items, int missing)
        {
            if (items.Count == 0) return;

            DepItem head = new DepItem();
            head.IsHeader = true;
            head.Group = title;
            head.Name = title + "  (" + items.Count + (missing > 0 ? ", " + missing + " missing" : "") + ")";
            head.State = missing > 0 ? DepState.Missing : DepState.Ok;
            rep.Items.Add(head);
            rep.Items.AddRange(items);
        }

        static void Sort(List<DepItem> items)
        {
            items.Sort(delegate(DepItem a, DepItem b)
            {
                int ra = Rank(a.State), rb = Rank(b.State);
                if (ra != rb) return ra - rb;
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        static int Rank(DepState s)
        {
            return s == DepState.Missing ? 0 : s == DepState.Unknown ? 1 : 2;
        }

        static string Vendor(PluginRef pr, InstalledPlugin installed)
        {
            if (installed != null && installed.Vendor.Length > 0) return " - " + installed.Vendor;
            if (pr.Manufacturer.Length > 0 && pr.VendorConfident) return " - " + pr.Manufacturer;
            return "";
        }

        static string Format(PluginKind k)
        {
            switch (k)
            {
                case PluginKind.Vst3: return "VST3";
                case PluginKind.Vst2: return "VST2";
                case PluginKind.AudioUnit: return "AU";
                default: return "M4L";
            }
        }

        static string NameOf(string path)
        {
            try { return Path.GetFileName(path.Replace('/', '\\')); }
            catch { return path; }
        }

        static string Folder(string path)
        {
            try { return Path.GetDirectoryName(path); }
            catch { return path; }
        }

        /// <summary>Отчёт текстом — то, что удобно вставить коллаборатору в переписку.</summary>
        public string ToText()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Dependencies of " + SetName);
            sb.AppendLine(new string('-', 60));
            if (Error != null) { sb.AppendLine("Cannot read set: " + Error); return sb.ToString(); }

            foreach (DepItem i in Items)
            {
                if (i.IsHeader) { sb.AppendLine(); sb.AppendLine(i.Name); continue; }
                string mark = i.State == DepState.Ok ? "  ok     "
                            : i.State == DepState.Missing ? "  MISSING "
                            : "  ?      ";
                sb.Append(mark).Append(i.Name);
                if (i.Count > 1) sb.Append("  x").Append(i.Count);
                if (i.Detail.Length > 0) sb.Append("   ").Append(i.Detail);
                sb.AppendLine();
            }

            sb.AppendLine();
            sb.AppendLine("Samples: " + TotalSamples + ", missing " + MissingSamples
                        + " | Plugins: " + TotalPlugins + ", missing " + MissingPlugins
                        + (MissingPacks > 0 ? " | Packs missing: " + MissingPacks : ""));
            return sb.ToString();
        }
    }
}
