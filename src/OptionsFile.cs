using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AbletonOptions
{
    /// <summary>Одна установленная версия Live с папкой Preferences.</summary>
    public sealed class LiveProfile
    {
        public string DisplayName;
        public string PreferencesDir;
        public string OptionsPath;

        public override string ToString() { return DisplayName; }

        public static List<LiveProfile> Detect()
        {
            List<LiveProfile> list = new List<LiveProfile>();
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
            if (!Directory.Exists(root)) return list;

            foreach (string dir in Directory.GetDirectories(root, "Live *"))
            {
                string prefs = Path.Combine(dir, "Preferences");
                if (!Directory.Exists(prefs)) continue;
                list.Add(FromPreferencesDir(prefs, Path.GetFileName(dir)));
            }
            // Сначала стабильные версии (беты вроде «Live 12.4.5b3» — ниже), внутри — новее сверху.
            list.Sort(delegate (LiveProfile a, LiveProfile b)
            {
                bool ba = IsPrerelease(a.DisplayName), bb = IsPrerelease(b.DisplayName);
                if (ba != bb) return ba ? 1 : -1;
                return CompareVersion(b.DisplayName, a.DisplayName);
            });
            return list;
        }

        public static LiveProfile FromPreferencesDir(string prefs, string name)
        {
            LiveProfile p = new LiveProfile();
            p.PreferencesDir = prefs;
            p.DisplayName = name;
            p.OptionsPath = Path.Combine(prefs, "Options.txt");
            return p;
        }

        /// <summary>«Live 12.4.5b3», «Live 12.5 Beta» — предрелиз; «Live 12.4.3» — нет.</summary>
        static bool IsPrerelease(string name)
        {
            for (int i = 1; i < name.Length; i++)
                if (char.IsDigit(name[i - 1]) && char.IsLetter(name[i])) return true;
            return name.IndexOf("beta", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int CompareVersion(string a, string b)
        {
            int[] va = ParseVersion(a), vb = ParseVersion(b);
            for (int i = 0; i < 4; i++)
            {
                if (va[i] != vb[i]) return va[i].CompareTo(vb[i]);
            }
            return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
        }

        static int[] ParseVersion(string s)
        {
            int[] r = new int[4];
            string digits = "";
            foreach (char c in s) if (char.IsDigit(c) || c == '.') digits += c;
            string[] parts = digits.Split(new char[] { '.' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < parts.Length && i < 4; i++)
            {
                int v;
                int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
                r[i] = v;
            }
            return r;
        }
    }

    public sealed class OptionLine
    {
        public string Name;
        public string Value;   // "" если у опции нет значения
        public OptionLine(string name, string value) { Name = name; Value = value; }
    }

    /// <summary>Содержимое Options.txt: распознанные опции + всё, что мы не знаем.</summary>
    public sealed class OptionsDoc
    {
        public readonly Dictionary<string, string> Enabled =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Опции из файла, которых нет в каталоге — сохраняем как есть.</summary>
        public readonly List<OptionLine> Custom = new List<OptionLine>();

        /// <summary>Строки без ведущего дефиса — тоже не теряем.</summary>
        public readonly List<string> Foreign = new List<string>();

        public static OptionsDoc Load(string path)
        {
            OptionsDoc d = new OptionsDoc();
            if (!File.Exists(path)) return d;

            foreach (string raw in File.ReadAllLines(path))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;

                if (!line.StartsWith("-"))
                {
                    d.Foreign.Add(line);
                    continue;
                }

                string body = line.Substring(1);
                string name, value;
                int eq = body.IndexOf('=');
                if (eq >= 0)
                {
                    name = body.Substring(0, eq).Trim();
                    value = body.Substring(eq + 1).Trim();
                }
                else
                {
                    name = body.Trim();
                    value = "";
                }
                if (name.Length == 0) continue;

                if (Catalog.Find(name) != null)
                {
                    d.Enabled[name] = value;
                }
                else if (!Catalog.KnownBad.Contains(name))
                {
                    d.Custom.Add(new OptionLine(name, value));
                }
                // иначе — заведомо нерабочая опция, молча пропускаем при чтении
            }
            return d;
        }

        public string Render()
        {
            StringBuilder sb = new StringBuilder();
            List<Opt> all = Catalog.All;
            for (int i = 0; i < all.Count; i++)
            {
                string v;
                if (!Enabled.TryGetValue(all[i].Name, out v)) continue;
                sb.Append('-').Append(all[i].Name);
                if (!string.IsNullOrEmpty(v)) sb.Append('=').Append(v);
                sb.Append("\r\n");
            }
            for (int i = 0; i < Custom.Count; i++)
            {
                sb.Append('-').Append(Custom[i].Name);
                if (!string.IsNullOrEmpty(Custom[i].Value)) sb.Append('=').Append(Custom[i].Value);
                sb.Append("\r\n");
            }
            for (int i = 0; i < Foreign.Count; i++)
                sb.Append(Foreign[i]).Append("\r\n");
            return sb.ToString();
        }

        /// <summary>Пишет файл, предварительно сделав резервную копию. Возвращает путь к копии или null.</summary>
        public string Save(string path)
        {
            string backup = null;
            if (File.Exists(path))
            {
                string existing = File.ReadAllText(path);
                if (existing == Render()) return null;   // нечего менять

                backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Copy(path, backup, true);
            }

            string dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string text = Render();
            if (text.Length == 0)
            {
                // Пустой Options.txt Live читает нормально, но чище удалить файл.
                File.Delete(path);
                return backup;
            }
            File.WriteAllText(path, text, new UTF8Encoding(false));
            return backup;
        }
    }
}
