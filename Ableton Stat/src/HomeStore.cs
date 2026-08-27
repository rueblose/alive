using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>
    /// Что помнит главная страница: закреплённые проекты. Отдельный файл, а не
    /// settings.cfg — по духу это пользовательские данные о конкретных проектах, а не
    /// настройка программы.
    ///
    /// Намеренно не заводим ни теги, ни рейтинги, ни коллекции: закрепление одним
    /// нажатием закрывает 90% потребности «держать под рукой», а всё остальное
    /// требовало бы ухода за собой.
    /// </summary>
    public static class HomeStore
    {
        static readonly List<string> _pins = new List<string>();
        static bool _loaded;

        static string FilePath { get { return Path.Combine(Settings.Dir, "home.cfg"); } }

        static void Load()
        {
            if (_loaded) return;
            _loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq);
                    string val = line.Substring(eq + 1);

                    if (key == "pin" && val.Length > 0 && !Contains(_pins, val)) _pins.Add(val);
                }
            }
            catch { }
        }

        static bool Contains(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Save()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Alive - home page: pinned projects");
                foreach (string p in _pins) sb.Append("pin=").AppendLine(p);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        // ------------------------------------------------------------- закрепление

        public static bool IsPinned(string path)
        {
            Load();
            return Contains(_pins, path ?? "");
        }

        public static void TogglePin(string path)
        {
            Load();
            if (string.IsNullOrEmpty(path)) return;
            for (int i = 0; i < _pins.Count; i++)
                if (string.Equals(_pins[i], path, StringComparison.OrdinalIgnoreCase))
                {
                    _pins.RemoveAt(i);
                    Save();
                    return;
                }
            _pins.Add(path);
            Save();
        }

        /// <summary>Порядок закрепления сохраняем: первым закрепили — первым и показываем.</summary>
        public static List<string> Pins { get { Load(); return new List<string>(_pins); } }
    }
}
