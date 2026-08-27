using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Reel
{
    public sealed class Snapshot
    {
        public string Hash = "";        // sha1 содержимого .als
        public DateTime TimeUtc;
        public long Size;
        public string Source = "";      // имя .als, с которого снят снапшот
        public string Message = "";

        /// <summary>
        /// Имя файла в папке snapshots: «rrobin 2026-08-21 2158 653fb35a.als».
        /// Пусто у записей старого формата — там файл лежал под именем-хешем,
        /// см. SnapshotStore.PathOf.
        /// </summary>
        public string ObjectName = "";

        public string ObjectPath = "";

        /// <summary>
        /// Снят осознанно — кнопкой Snapshot / Ctrl+S (SnapshotStore.CaptureManual).
        /// false — всё, что сделал сам Reel без спроса: первый снимок при открытии,
        /// автосохранение из Watch-режима, подстраховка перед Restore, импорт папки
        /// Backup. Граница ровно та же, что между Capture() и CaptureManual().
        /// </summary>
        public bool Manual;

        public DateTime Time { get { return TimeUtc.ToLocalTime(); } }

        /// <summary>Столько же знаков, сколько стоит в имени файла снимка, — чтобы
        /// строку в списке и файл в проводнике можно было сличить глазами.</summary>
        public string Short { get { return Hash.Length >= 8 ? Hash.Substring(0, 8) : Hash; } }
    }

    /// <summary>
    /// Хранилище версий одного проекта.
    ///
    /// Снапшот адресуется хешем содержимого: два одинаковых сохранения занимают на диске
    /// один файл, а не два. Это не мелочь — именно раздувание хранилища хоронит попытки
    /// вести .als в обычном git, и именно от него люди отказываются от версионирования
    /// вообще. Сам .als уже сжат (gzip поверх XML), так что жать повторно нечего:
    /// дедупликация — единственное, что тут реально экономит.
    ///
    /// История лежит рядом в history.tsv построчно: файл должен оставаться читаемым
    /// глазами и чиниться руками, даже если прототип испортит собственный индекс.
    /// </summary>
    public sealed class SnapshotStore
    {
        public string ProjectDir = "";
        public string Root = "";
        public readonly List<Snapshot> Entries = new List<Snapshot>();   // новые сверху
        public string Error;

        const string HistoryName = "history.tsv";

        public static SnapshotStore Open(string projectDir)
        {
            SnapshotStore s = new SnapshotStore();
            s.ProjectDir = projectDir;
            s.Root = RootFor(projectDir);
            s.MigrateFromReel();
            s.Load();
            return s;
        }

        /// <summary>
        /// Где лежит история этого проекта: Backup\Alive внутри самой папки проекта,
        /// и больше нигде — общего склада на отдельном диске у Forks нет.
        ///
        /// Backup — та самая папка, куда сохраняет свои копии сама Live, и это выбрано
        /// нарочно: каталог Alive обходит дерево проекта сам (FolderScan.Find) и
        /// пропускает ровно одну папку — «Backup». Лежи снимки где-то ещё, каждый из
        /// них приехал бы в каталог отдельным сетом. Подпапка Alive отделяет наши
        /// версии от бэкапов самой Live, лежащих рядом, а .als в ней лежат сразу, без
        /// ещё одного уровня — папку должно быть видно глазами и в проводнике.
        /// </summary>
        public static string RootFor(string projectDir)
        {
            return Path.Combine(Path.Combine(projectDir, "Backup"), "Alive");
        }

        /// <summary>
        /// Переезд со старого места (.reel внутри проекта, снимки в .reel\Backup).
        /// Молча бросить чужую историю нельзя, а сливать две — незачем: переносим,
        /// только если на новом месте ещё пусто.
        /// </summary>
        void MigrateFromReel()
        {
            try
            {
                string old = Path.Combine(ProjectDir, ".reel");
                if (!File.Exists(Path.Combine(old, HistoryName))) return;
                if (File.Exists(Path.Combine(Root, HistoryName))) return;

                Directory.CreateDirectory(Root);
                foreach (string f in Directory.GetFiles(Path.Combine(old, "Backup")))
                {
                    string to = Path.Combine(Root, Path.GetFileName(f));
                    if (!File.Exists(to)) File.Move(f, to);
                }
                File.Move(Path.Combine(old, HistoryName), Path.Combine(Root, HistoryName));
            }
            catch (Exception ex) { Error = ex.Message; }
        }

        /// <summary>
        /// Как называется файл снимка: «rrobin 2026-08-21 [653fb35a].als».
        ///
        /// Раньше имя было хешем целиком, и папка со снимками выглядела свалкой из
        /// сорокасимвольных строк — по ней нельзя было ни искать, ни понять, что перед
        /// тобой, не открывая программу. Имя сета и дата отвечают на оба вопроса сразу.
        ///
        /// Времени в имени нет намеренно: в одну дату снимков бывает несколько, но
        /// различает их не минута, а содержимое, — и восемь знаков хеша в квадратных
        /// скобках говорят об этом честнее. Скобки те же, что у бэкапов самой Live
        /// («rrobin [2025-12-31 221434].als»), так что папка читается привычно.
        ///
        /// Дата местная, а не UTC: в списке показана местная, и расхождение между
        /// именем файла и строкой в окне сбивало бы с толку.
        /// </summary>
        internal static string ObjectNameFor(string source, DateTime timeUtc, string hash)
        {
            string stem = SafeName(Path.GetFileNameWithoutExtension(source ?? ""));
            if (stem.Length == 0) stem = "set";

            string when = timeUtc.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string tail = hash.Length >= 8 ? hash.Substring(0, 8) : hash;
            return stem + " " + when + " [" + tail + "].als";
        }

        static string SafeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        // ------------------------------------------------------------------ чтение

        void Load()
        {
            Entries.Clear();
            try
            {
                string history = Path.Combine(Root, HistoryName);
                if (!File.Exists(history)) return;

                foreach (string raw in File.ReadAllLines(history, Encoding.UTF8))
                {
                    if (raw.Length == 0 || raw[0] == '#') continue;
                    string[] p = raw.Split('\t');
                    if (p.Length < 5) continue;

                    Snapshot s = new Snapshot();
                    s.Hash = p[0];
                    long ticks;
                    if (long.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ticks))
                        s.TimeUtc = new DateTime(ticks, DateTimeKind.Utc);
                    long size;
                    if (long.TryParse(p[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out size))
                        s.Size = size;
                    s.Source = p[3];

                    // Формат дописывался дважды, и обе прошлые версии должны читаться:
                    //   5 полей — самый первый (…\tsource\tmessage);
                    //   6 полей — со столбцом manual;
                    //   7 полей — плюс имя файла снапшота.
                    // Различаются по числу столбцов. Старые записи считаем авто — там
                    // всё равно нет ни одной, снятой осознанно, — а файл ищем по
                    // прежней раскладке objects\xx\<hash>.als.
                    if (p.Length >= 7)
                    {
                        s.Manual = p[4] == "1";
                        s.ObjectName = p[5];
                        s.Message = Unescape(p[6]);
                    }
                    else if (p.Length == 6)
                    {
                        s.Manual = p[4] == "1";
                        s.Message = Unescape(p[5]);
                    }
                    else s.Message = Unescape(p[4]);

                    s.ObjectPath = PathOf(s.ObjectName, s.Hash);
                    Entries.Add(s);
                }
                Entries.Sort(delegate(Snapshot a, Snapshot b) { return b.TimeUtc.CompareTo(a.TimeUtc); });
                MigrateObjects();
            }
            catch (Exception ex) { Error = ex.Message; }
        }

        /// <summary>
        /// Приводит файлы склада к нынешней раскладке и нынешним именам: и снимки
        /// самого первого формата (objects\xx\&lt;hash&gt;.als), и те, что названы прошлой
        /// схемой «имя дата время хеш».
        ///
        /// Нужно не ради красоты: пока снимки лежат в objects, каталог Alive показывает
        /// каждый из них отдельным сетом с именем-хешем (см. комментарий у SnapDir).
        /// А смешанные схемы имён в одной папке сводят на нет всё, ради чего имена
        /// вообще делались читаемыми.
        ///
        /// Файл сначала переезжает, и только потом за ним правится запись в истории:
        /// оборванная посередине миграция оставляет запись указывать на живой файл.
        /// </summary>
        void MigrateObjects()
        {
            // Один и тот же хеш может стоять у нескольких записей — файл у них общий,
            // и имя ему полагается тоже одно.
            Dictionary<string, string> byHash = new Dictionary<string, string>(StringComparer.Ordinal);
            bool moved = false;

            foreach (Snapshot s in Entries)
            {
                string want;
                if (!byHash.TryGetValue(s.Hash, out want))
                {
                    want = ObjectNameFor(s.Source, s.TimeUtc, s.Hash);
                    byHash[s.Hash] = want;
                }
                if (s.ObjectName == want) continue;

                string from = s.ObjectPath;
                string to = PathOf(want, s.Hash);
                if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) continue;

                try
                {
                    if (File.Exists(from))
                    {
                        if (!File.Exists(to))
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(to));
                            File.Move(from, to);
                        }
                        else File.Delete(from);      // тот же хеш уже переехал под этим именем
                    }
                    else if (!File.Exists(to)) continue;   // файла нет ни там, ни там — запись не трогаем

                    s.ObjectName = want;
                    s.ObjectPath = to;
                    moved = true;
                }
                catch { /* не смогли — запись остаётся на прежнем пути, он ещё жив */ }
            }

            if (!moved) return;
            SaveHistory();

            // Пустые objects\xx и сама objects — иначе от старой раскладки остаётся
            // дерево пустых папок, и непонятно, переехало что-то или нет.
            try
            {
                string objects = Path.Combine(Root, "objects");
                if (!Directory.Exists(objects)) return;
                foreach (string d in Directory.GetDirectories(objects))
                    if (Directory.GetFileSystemEntries(d).Length == 0) Directory.Delete(d);
                if (Directory.GetFileSystemEntries(objects).Length == 0) Directory.Delete(objects);
            }
            catch { }
        }

        // ------------------------------------------------------------------ запись

        /// <summary>
        /// Снять снапшот файла без участия пользователя: то, что Reel делает сам —
        /// первый снимок при открытии, автосохранение в Watch-режиме, подстраховка
        /// перед Restore, импорт бэкапов Live. Всегда Manual = false; для снимка по
        /// кнопке/Ctrl+S см. CaptureManual.
        ///
        /// Возвращает null, если содержимое совпало с последним снапшотом того же
        /// файла — сохранение без единой правки не должно засорять историю (Live
        /// пишет .als и при простом закрытии окна).
        /// </summary>
        public Snapshot Capture(string alsPath, string message, DateTime timeUtc)
        {
            byte[] data = ReadAll(alsPath);
            if (data == null) return null;

            string source = Path.GetFileName(alsPath);
            string hash = Sha1(data);

            Snapshot last = LastOf(source);
            if (last != null && last.Hash == hash) return null;

            Snapshot s = new Snapshot();
            s.Hash = hash;
            s.TimeUtc = timeUtc;
            s.Size = data.LongLength;
            s.Source = source;
            s.Message = (message ?? "").Trim();
            s.ObjectName = ReuseOrName(hash, source, timeUtc);
            s.ObjectPath = PathOf(s.ObjectName, hash);

            if (!StoreObject(s, data)) return null;

            Entries.Insert(0, s);
            Entries.Sort(delegate(Snapshot a, Snapshot b) { return b.TimeUtc.CompareTo(a.TimeUtc); });
            return s;
        }

        public Snapshot Capture(string alsPath, string message)
        {
            return Capture(alsPath, message, DateTime.UtcNow);
        }

        /// <summary>
        /// Снапшот от пользователя — кнопка Snapshot / Ctrl+S. В отличие от Capture():
        /// - если хеш совпал с последним снапшотом того же файла, а тот был авто
        ///   (Manual == false — неважно, что именно там написано: "auto", "Live backup"
        ///   или пустая строка), помечает ЕГО ЖЕ осознанным и подставляет текст
        ///   пользователя — превращает фоновую отметку в настоящую;
        /// - если последний снапшот уже был ручным, а пользователь ввёл новый текст,
        ///   создаёт вторую запись с тем же содержимым — отдельную метку на то же
        ///   состояние файла ("смёл драмы", потом отдельно "готово к миксу");
        /// - если и хеш, и текст совпадают — изменений нет, возвращает null.
        /// </summary>
        public Snapshot CaptureManual(string alsPath, string message)
        {
            Error = null;
            byte[] data = ReadAll(alsPath);
            if (data == null)
            {
                Error = "Cannot read the set file (it might be locked by another process)";
                return null;
            }

            string source = Path.GetFileName(alsPath);
            string hash = Sha1(data);
            string msg = (message ?? "").Trim();

            Snapshot last = LastOf(source);
            if (last != null && last.Hash == hash)
            {
                if (last.Manual && string.Equals(last.Message, msg, StringComparison.Ordinal))
                    return null;                                  // ничего не изменилось

                if (!last.Manual)
                {
                    last.Message = msg;
                    last.Manual = true;
                    SaveHistory();
                    return last;
                }

                // Последний снапшот уже ручной: пустой ввод не плодит дубль без метки.
                if (msg.Length == 0) return null;
            }

            Snapshot s = new Snapshot();
            s.Hash = hash;
            s.TimeUtc = DateTime.UtcNow;
            s.Size = data.LongLength;
            s.Source = source;
            s.Message = msg;
            s.Manual = true;
            s.ObjectName = ReuseOrName(hash, source, s.TimeUtc);
            s.ObjectPath = PathOf(s.ObjectName, hash);

            if (!StoreObject(s, data)) return null;

            Entries.Insert(0, s);
            Entries.Sort(delegate(Snapshot a, Snapshot b) { return b.TimeUtc.CompareTo(a.TimeUtc); });
            return s;
        }

        /// <summary>
        /// Вернуть сет к состоянию снапшота. Текущее содержимое перед этим уходит в
        /// историю само: восстановление не должно быть операцией, после которой некуда
        /// вернуться.
        /// </summary>
        public bool Restore(Snapshot s, string targetPath, out string note)
        {
            note = "";
            try
            {
                if (!File.Exists(s.ObjectPath)) { note = "snapshot file is gone from the store"; return false; }

                if (File.Exists(targetPath))
                {
                    // Подстраховка помечается ручной не для красоты: в списке нет
                    // деления на свои и автоматические, и запись, снятая перед откатом,
                    // должна быть видна наравне со всеми — вернуться к ней нужно чаще
                    // всего именно тогда, когда откат оказался ошибкой.
                    Snapshot saved = Capture(targetPath, "before restoring " + s.Short);
                    if (saved != null) { saved.Manual = true; SaveHistory(); }
                    note = saved != null ? "current state saved as " + saved.Short : "current state already in history";
                }
                File.Copy(s.ObjectPath, targetPath, true);
                return true;
            }
            catch (Exception ex) { note = ex.Message; return false; }
        }

        public Snapshot LastOf(string source)
        {
            foreach (Snapshot s in Entries)
                if (string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>Предыдущий снапшот того же файла — с ним и сравниваем по умолчанию.</summary>
        public Snapshot Previous(Snapshot s)
        {
            int i = Entries.IndexOf(s);
            if (i < 0) return null;
            for (int j = i + 1; j < Entries.Count; j++)
                if (string.Equals(Entries[j].Source, s.Source, StringComparison.OrdinalIgnoreCase))
                    return Entries[j];
            return i + 1 < Entries.Count ? Entries[i + 1] : null;
        }

        /// <summary>Сколько занято на диске и сколько сэкономлено дедупликацией.</summary>
        public string Describe()
        {
            Dictionary<string, long> unique = new Dictionary<string, long>(StringComparer.Ordinal);
            long naive = 0;
            foreach (Snapshot s in Entries)
            {
                naive += s.Size;
                unique[s.Hash] = s.Size;
            }
            long real = 0;
            foreach (KeyValuePair<string, long> kv in unique) real += kv.Value;

            string text = Entries.Count + (Entries.Count == 1 ? " snapshot" : " snapshots")
                        + ", " + Size(real) + " on disk";
            if (naive > real) text += "  (saved " + Size(naive - real) + " by dedup)";
            return text;
        }

        /// <summary>Хеш файла на диске — им проверяется, отличается ли текущий .als от
        /// последнего снапшота. null, если файл не прочитался.</summary>
        public static string HashFile(string path)
        {
            byte[] data = ReadAll(path);
            return data == null ? null : Sha1(data);
        }

        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB";
            return (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
        }

        // ------------------------------------------------------------------ внутреннее

        /// <summary>
        /// Полный путь к файлу снапшота. Пустое имя — запись старого формата, файл
        /// лежит там, где его положила прошлая версия: objects\xx\<hash>.als.
        /// </summary>
        string PathOf(string objectName, string hash)
        {
            if (!string.IsNullOrEmpty(objectName))
                return Path.Combine(Root, objectName);

            string two = hash.Length >= 2 ? hash.Substring(0, 2) : "00";
            return Path.Combine(Path.Combine(Root, "objects"), Path.Combine(two, hash + ".als"));
        }

        /// <summary>
        /// Имя файла для этого содержимого. Если такой хеш уже лежит в истории —
        /// отдаём имя, под которым он там сохранён: дедупликация должна оставаться
        /// дедупликацией, один и тот же байт-в-байт сет не должен лежать на диске
        /// дважды только потому, что снят в другую минуту.
        /// </summary>
        string ReuseOrName(string hash, string source, DateTime timeUtc)
        {
            foreach (Snapshot e in Entries)
                if (e.Hash == hash && e.ObjectName.Length > 0) return e.ObjectName;
            return ObjectNameFor(source, timeUtc, hash);
        }

        /// <summary>Записать файл снапшота, если его ещё нет. false — не смогли.</summary>
        bool StoreObject(Snapshot s, byte[] data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(s.ObjectPath));
                if (!File.Exists(s.ObjectPath)) File.WriteAllBytes(s.ObjectPath, data);
                Append(s);
                return true;
            }
            catch (Exception ex) { Error = ex.Message; return false; }
        }

        void Append(Snapshot s)
        {
            Directory.CreateDirectory(Root);
            string history = Path.Combine(Root, HistoryName);
            bool fresh = !File.Exists(history);

            StringBuilder sb = new StringBuilder();
            if (fresh) sb.AppendLine(HistoryHeader);
            sb.AppendLine(Row(s));

            File.AppendAllText(history, sb.ToString(), new UTF8Encoding(false));
        }

        public void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Root);
                string history = Path.Combine(Root, HistoryName);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine(HistoryHeader);

                List<Snapshot> chrono = new List<Snapshot>(Entries);
                chrono.Sort(delegate(Snapshot a, Snapshot b) { return a.TimeUtc.CompareTo(b.TimeUtc); });

                foreach (Snapshot s in chrono) sb.AppendLine(Row(s));

                File.WriteAllText(history, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Error = ex.Message; }
        }

        const string HistoryHeader = "# hash\tutc-ticks\tbytes\tsource\tmanual\tfile\tmessage";

        /// <summary>Одна строка history.tsv. Сообщение идёт последним: только в нём
        /// бывает что угодно, и порча одной записи не съезжает на соседние столбцы.</summary>
        static string Row(Snapshot s)
        {
            return s.Hash + "\t"
                 + s.TimeUtc.Ticks.ToString(CultureInfo.InvariantCulture) + "\t"
                 + s.Size.ToString(CultureInfo.InvariantCulture) + "\t"
                 + s.Source + "\t"
                 + (s.Manual ? "1" : "0") + "\t"
                 + s.ObjectName + "\t"
                 + Escape(s.Message);
        }

        /// <summary>Live держит файл открытым в момент сохранения — пробуем несколько раз.</summary>
        static byte[] ReadAll(string path)
        {
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                          FileShare.ReadWrite))
                    {
                        byte[] buf = new byte[fs.Length];
                        int done = 0;
                        while (done < buf.Length)
                        {
                            int n = fs.Read(buf, done, buf.Length - done);
                            if (n <= 0) break;
                            done += n;
                        }
                        return buf;
                    }
                }
                catch
                {
                    System.Threading.Thread.Sleep(150);
                }
            }
            return null;
        }

        static string Sha1(byte[] data)
        {
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(data);
                StringBuilder sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        static string Escape(string s)
        {
            return (s ?? "").Replace("\\", "\\\\").Replace("\t", " ")
                            .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");
        }

        static string Unescape(string s)
        {
            StringBuilder sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char next = s[++i];
                if (next == 'n') sb.Append(' ');
                else sb.Append(next);
            }
            return sb.ToString();
        }
    }
}
