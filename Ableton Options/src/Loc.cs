using System;
using System.IO;

namespace AbletonOptions
{
    /// <summary>Локализация. English по умолчанию, русский — переключателем в шапке.</summary>
    public static class L
    {
        public static bool Ru;

        public static event EventHandler Changed;

        public static string S(string en, string ru)
        {
            return Ru && !string.IsNullOrEmpty(ru) ? ru : en;
        }

        public static void SetRussian(bool ru)
        {
            if (Ru == ru) return;
            Ru = ru;
            Save();
            if (Changed != null) Changed(null, EventArgs.Empty);
        }

        static string ConfigPath
        {
            get
            {
                string dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AbletonOptions");
                return Path.Combine(dir, "ui.cfg");
            }
        }

        public static void Load()
        {
            try
            {
                if (!File.Exists(ConfigPath)) return;
                foreach (string line in File.ReadAllLines(ConfigPath))
                    if (line.Trim() == "lang=ru") Ru = true;
            }
            catch { /* настройка интерфейса — не повод падать */ }
        }

        static void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(ConfigPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(ConfigPath, "lang=" + (Ru ? "ru" : "en"));
            }
            catch { }
        }
    }
}
