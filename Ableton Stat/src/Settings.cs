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
        public bool IncludeBackups;

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
        /// Схлопывать ли сеты одной папки в одну строку. По умолчанию да: у проекта
        /// обычно с десяток .als (v1, v2, final, final2), и без этого каталог — это
        /// список версий, а не список проектов.
        /// </summary>
        public bool GroupByFolder = true;

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
                    else if (key == "backups") s.IncludeBackups = val == "1";
                    else if (key == "setcolumns") s.SetColumns = val;
                    else if (key == "plugincolumns") s.PluginColumns = val;
                    else if (key == "pinnedfirst") s.PinnedFirst = val == "1";
                    else if (key == "groupbyfolder") s.GroupByFolder = val == "1";
                }
            }
            catch { }
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

        public void Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Alive - folders to scan for projects");
                foreach (string r in Roots) sb.Append("root=").AppendLine(r);
                foreach (string r in DisabledRoots) sb.Append("root_off=").AppendLine(r);
                sb.Append("backups=").AppendLine(IncludeBackups ? "1" : "0");
                sb.Append("pinnedfirst=").AppendLine(PinnedFirst ? "1" : "0");
                sb.Append("groupbyfolder=").AppendLine(GroupByFolder ? "1" : "0");
                if (SetColumns.Length > 0) sb.Append("setcolumns=").AppendLine(SetColumns);
                if (PluginColumns.Length > 0) sb.Append("plugincolumns=").AppendLine(PluginColumns);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
