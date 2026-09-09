using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>Корни поиска и мелкие настройки. Формат — построчный, чтобы файл можно было править руками.</summary>
    public sealed class Settings
    {
        public readonly List<string> Roots = new List<string>();
        // Подмножество Roots, временно исключённое из сканирования — папка остаётся в
        // списке (и в UI), но Scan() её пропускает, пока сюда не вернут.
        public readonly List<string> DisabledRoots = new List<string>();

        /// <summary>
        /// Колонки списка сетов: видимость, порядок и ширины одной строкой вида
        /// «Set,Modified:150,BPM:81,…». Пусто — набор по умолчанию. Хранится как есть,
        /// разбирает и собирает эту строку сам список — настройкам знать её формат незачем.
        /// </summary>
        public string SetColumns = "";

        /// <summary>То же самое для таблицы плагинов — она настраивается наравне с сетами.</summary>
        public string PluginColumns = "";

        /// <summary>Держать ли закреплённые сеты в начале списка — звёздочка в шапке.</summary>
        public bool PinnedFirst;

        /// <summary>
        /// Выключить прозрачный фон окна насовсем — окна станут плоскими и тёмными.
        ///
        /// Нужно из-за Windows 10: акрил там реализован через недокументированный
        /// SetWindowCompositionAttribute, и DWM пересчитывает размытие на каждый сдвиг
        /// окна. При перетаскивании очередь сообщений копится, окно едет за курсором с
        /// задержкой и продолжает двигаться ещё пару секунд после того, как мышь
        /// остановилась. На Windows 11 работает другая ветка (системный backdrop), и
        /// там этого нет — поэтому не выключаем сами, а отдаём переключателем.
        /// </summary>
        public bool DisableGlass;


        /// <summary>
        /// Включена ли плавная вертикальная прокрутка (доводка таймером).
        /// При false прокрутка во всех списках и панелях происходит мгновенно.
        /// </summary>
        public bool SmoothScroll = true;

        /// <summary>
        /// Схлопывать ли сеты одной папки в одну строку. По умолчанию да: у проекта
        /// обычно с десяток .als (v1, v2, final, final2), и без этого каталог — это
        /// список версий, а не список проектов.
        /// </summary>
        public bool GroupByFolder = true;

        // ------------------------------------------------------------- плагины

        /// <summary>
        /// Откуда брать список установленных плагинов: false — из базы самой Live
        /// (по умолчанию и почти всегда правильно: Live уже обошла все папки со своими
        /// настройками и разобрала бинарники, а шеллы вроде Waves развернула в сотни
        /// отдельных плагинов), true — обойти папки самим по настройкам ниже. Второе
        /// нужно, когда база Live пуста или врёт.
        /// </summary>
        public bool PluginsFromFolders;

        /// <summary>
        /// Какую установку Live спрашивать: пусто — все сразу, свежая важнее. Иначе имя
        /// папки («Live 12.4.3»). Ручной выбор нужен из-за бет: у них своя папка
        /// настроек, и свежий журнал сканера там бывает от одного-единственного
        /// плагина, который сейчас разрабатывают.
        /// </summary>
        public string PluginSource = "";

        /// <summary>Папки для ручного обхода — те же три переключателя, что в Live.</summary>
        public bool Vst2CustomOn;
        public string Vst2CustomPath = "";
        public bool Vst3SystemOn = true;
        public bool Vst3CustomOn;
        public string Vst3CustomPath = "";

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Alive");
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.cfg"); } }

        public bool IsFirstRun { get { return Roots.Count == 0; } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    if (key == "root" && val.Length > 0 && !Has(s.Roots, val)) s.Roots.Add(val);
                    else if (key == "root_off" && val.Length > 0 && !Has(s.DisabledRoots, val))
                        s.DisabledRoots.Add(val);
                    else if (key == "setcolumns") s.SetColumns = val;
                    else if (key == "plugincolumns") s.PluginColumns = val;
                    else if (key == "pinnedfirst") s.PinnedFirst = val == "1";
                    else if (key == "noglass") s.DisableGlass = val == "1";
                    else if (key == "smoothscroll") s.SmoothScroll = val == "1";
                    else if (key == "nosmoothscroll") s.SmoothScroll = val == "0";
                    else if (key == "groupbyfolder") s.GroupByFolder = val == "1";
                    else if (key == "pluginfolders") s.PluginsFromFolders = val == "1";
                    else if (key == "pluginsource") s.PluginSource = val;
                    else if (key == "vst2custom") s.Vst2CustomOn = val == "1";
                    else if (key == "vst2path") s.Vst2CustomPath = val;
                    else if (key == "vst3system") s.Vst3SystemOn = val == "1";
                    else if (key == "vst3custom") s.Vst3CustomOn = val == "1";
                    else if (key == "vst3path") s.Vst3CustomPath = val;
                }
            }
            catch { }
            Theme.SmoothScroll = s.SmoothScroll;
            return s;
        }

        // Пути в Windows регистронезависимы, а List<string>.Contains — нет, поэтому
        // без этого хелпера "C:\Ableton" и "c:\ableton" считались бы разными корнями.
        static bool Has(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static event Action<object> RootsChanged;

        public static void NotifyRootsChanged(object source)
        {
            Action<object> h = RootsChanged;
            if (h != null)
            {
                try { h(source); } catch { }
            }
        }

        public void ReloadRoots()
        {
            Settings s = Load();
            Roots.Clear();
            Roots.AddRange(s.Roots);
            DisabledRoots.Clear();
            DisabledRoots.AddRange(s.DisabledRoots);
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Alive - folders to scan for projects");
                foreach (string r in Roots) sb.Append("root=").AppendLine(r);
                foreach (string r in DisabledRoots) sb.Append("root_off=").AppendLine(r);
                sb.Append("pinnedfirst=").AppendLine(PinnedFirst ? "1" : "0");
                sb.Append("noglass=").AppendLine(DisableGlass ? "1" : "0");
                sb.Append("smoothscroll=").AppendLine(SmoothScroll ? "1" : "0");
                sb.Append("groupbyfolder=").AppendLine(GroupByFolder ? "1" : "0");
                sb.Append("pluginfolders=").AppendLine(PluginsFromFolders ? "1" : "0");
                sb.Append("pluginsource=").AppendLine(PluginSource);
                sb.Append("vst2custom=").AppendLine(Vst2CustomOn ? "1" : "0");
                sb.Append("vst2path=").AppendLine(Vst2CustomPath);
                sb.Append("vst3system=").AppendLine(Vst3SystemOn ? "1" : "0");
                sb.Append("vst3custom=").AppendLine(Vst3CustomOn ? "1" : "0");
                sb.Append("vst3path=").AppendLine(Vst3CustomPath);
                if (SetColumns.Length > 0) sb.Append("setcolumns=").AppendLine(SetColumns);
                if (PluginColumns.Length > 0) sb.Append("plugincolumns=").AppendLine(PluginColumns);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
