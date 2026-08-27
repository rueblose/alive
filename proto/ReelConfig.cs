using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Reel
{
    /// <summary>
    /// Что помнит окно версий между запусками — только список последних сетов:
    /// %APPDATA%\Reel\reel.cfg.
    ///
    /// Настраивать тут больше нечего. Склад версий лежит в самой папке проекта
    /// (SnapshotStore.RootFor), а снимки делаются только руками, так что ни пути к
    /// хранилищу, ни выключателя автосъёмки здесь нет и быть не должно.
    ///
    /// Формат построчный, как в Alive: файл должен читаться и правиться руками.
    /// </summary>
    public static class ReelConfig
    {
        /// <summary>Последние открытые сеты, самый свежий первым.</summary>
        public static readonly List<string> Recent = new List<string>();

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Reel");
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "reel.cfg"); } }

        static bool _loaded;

        public static void Load()
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
                    if (eq <= 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    // Ключи storeroot/autosave/watch из прошлых версий не читаются
                    // намеренно: и хранилище, и автосъёмка убраны, а молча унаследовать
                    // настройку того, чего больше нет, — способ получить сюрприз.
                    if (key == "recent" && val.Length > 0 && !Recent.Contains(val)) Recent.Add(val);
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Forks - recently opened sets");
                for (int i = 0; i < Recent.Count && i < 12; i++)
                    sb.AppendLine("recent=" + Recent[i]);
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }

        public static void Remember(string alsPath)
        {
            if (string.IsNullOrEmpty(alsPath)) return;
            Recent.RemoveAll(delegate(string s)
            {
                return string.Equals(s, alsPath, StringComparison.OrdinalIgnoreCase);
            });
            Recent.Insert(0, alsPath);
            Save();
        }
    }
}
