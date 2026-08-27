using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>Чем кончилось всё расследование.</summary>
    public enum RescueVerdict
    {
        None,           // ещё ищем
        NotPlugins,     // сет не открылся даже со всеми отключёнными — дело не в плагинах
        Culprit,        // виноват ровно один, он в Culprit
        Group,          // виноватых несколько; известен набор, отключение которого помогает
        NoProbeYet      // проб ещё не было
    }

    /// <summary>
    /// Пробные копии сета: имя, журнал и уборка.
    ///
    /// Проба кладётся РЯДОМ с оригиналом, а не во временную папку, и это не лень.
    /// Относительные ссылки на сэмплы Live отсчитывает от папки, где лежит сам .als;
    /// унеси копию в %TEMP% — и Live честно доложит, что потеряла все файлы проекта.
    /// Такая проба проверяла бы не плагины, а собственную кривизну.
    /// </summary>
    public static class RescueProbe
    {
        public const string Suffix = ".alive-probe.als";

        /// <summary>Список проб, лежащих на диске прямо сейчас, — чтобы убрать их после падения.</summary>
        static string JournalPath { get { return Path.Combine(Settings.Dir, "probes.txt"); } }

        public static bool IsProbe(string path)
        {
            return !string.IsNullOrEmpty(path)
                && path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase);
        }

        public static string PathFor(SetEntry set)
        {
            return Path.Combine(set.Directory, set.Name + Suffix);
        }

        /// <summary>
        /// Убрать пробы, оставшиеся от прошлого запуска. Программа падает редко, но
        /// проба — это .als в чужой папке проекта, и оставить его там насовсем нельзя:
        /// человек однажды откроет его вместо своего сета и не поймёт, почему полсета
        /// без плагинов. Зовётся на старте, до первого сканирования.
        /// </summary>
        public static void CleanupStale()
        {
            List<string> listed = ReadJournal();
            if (listed.Count == 0) return;

            int gone = 0;
            foreach (string p in listed)
            {
                // Из журнала удаляем только то, что и правда проба: файл журнала лежит
                // в открытом виде и однажды окажется поправлен руками.
                if (!IsProbe(p)) continue;
                try { if (File.Exists(p)) { File.Delete(p); gone++; } }
                catch (Exception ex) { Diag.Fail("rescue: cleanup " + p, ex); }
            }

            WriteJournal(new List<string>());
            if (gone > 0) Diag.Line("rescue: removed " + gone + " stale probe(s)");
        }

        public static void Remember(string probe)
        {
            List<string> all = ReadJournal();
            foreach (string p in all)
                if (string.Equals(p, probe, StringComparison.OrdinalIgnoreCase)) return;
            all.Add(probe);
            WriteJournal(all);
        }

        public static void Forget(string probe)
        {
            List<string> all = ReadJournal();
            List<string> kept = new List<string>();
            foreach (string p in all)
                if (!string.Equals(p, probe, StringComparison.OrdinalIgnoreCase)) kept.Add(p);
            if (kept.Count != all.Count) WriteJournal(kept);
        }

        /// <summary>
        /// Удалить пробу и вычеркнуть её из журнала. Тихо: убирать мусор — не повод для
        /// окна с ошибкой. Из журнала вычёркиваем только то, что действительно исчезло:
        /// файл могла держать открытым сама Live, и тогда убрать его сможет лишь
        /// следующий запуск программы — по этой самой записи.
        /// </summary>
        public static void Drop(string probe)
        {
            if (string.IsNullOrEmpty(probe)) return;
            try { if (File.Exists(probe)) File.Delete(probe); }
            catch (Exception ex) { Diag.Fail("rescue: drop " + probe, ex); }
            if (!File.Exists(probe)) Forget(probe);
        }

        static List<string> ReadJournal()
        {
            List<string> all = new List<string>();
            try
            {
                if (!File.Exists(JournalPath)) return all;
                foreach (string raw in File.ReadAllLines(JournalPath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length > 0) all.Add(line);
                }
            }
            catch (Exception ex) { Diag.Fail("rescue: read journal", ex); }
            return all;
        }

        static void WriteJournal(List<string> all)
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);
                File.WriteAllLines(JournalPath, all.ToArray(), new UTF8Encoding(false));
            }
            catch (Exception ex) { Diag.Fail("rescue: write journal", ex); }
        }
    }

    /// <summary>
    /// Расследование одного сета, который не открывается.
    ///
    /// Логика поиска держится на одном допущении: ломает ОДИН плагин. Тогда каждая проба
    /// — это ответ на вопрос «лежит ли виновник внутри отключённого набора»:
    ///
    ///     открылось  → виновник среди отключённых  → подозреваемые ∩= отключённые
    ///     не открылось → виновник не среди них     → подозреваемые −= отключённые
    ///
    /// Так работает любой набор, а не только ровная половина, — значит человек может
    /// ткнуть галочки сам, и расследование это учтёт, а не собьётся.
    ///
    /// Допущение проверяемое: если подозреваемых не осталось ни одного, виноват не один,
    /// и вместо имени выдаётся набор Working — тот, отключение которого сет открывало.
    /// Это всегда правда, даже когда красивого ответа нет.
    /// </summary>
    public sealed class RescueSession
    {
        public readonly SetEntry Set;
        public readonly AlsInfo Info;
        public readonly List<AlsPluginSlot> Targets;
        public readonly int Unaddressable;
        public readonly string Error;

        readonly PluginInventory _inv;

        /// <summary>Что журнал Live помнит о прошлых попытках открыть этот сет.</summary>
        public LoadAttempt History;
        public AlsPluginSlot HistorySuspect;

        /// <summary>Кто ещё может быть виновен.</summary>
        public readonly List<AlsPluginSlot> Suspects = new List<AlsPluginSlot>();

        /// <summary>Наименьший известный набор, отключение которого сет открывало.</summary>
        public List<AlsPluginSlot> Working;

        public RescueVerdict Verdict = RescueVerdict.NoProbeYet;
        public AlsPluginSlot Culprit;

        public int Round;
        public string ProbePath = "";
        public List<AlsPluginSlot> ProbeDisabled = new List<AlsPluginSlot>();
        public DateTime ProbeStarted;

        readonly List<string> _log = new List<string>();
        readonly Dictionary<string, LiveLogFile> _logs =
            new Dictionary<string, LiveLogFile>(StringComparer.OrdinalIgnoreCase);

        public RescueSession(SetEntry set, PluginInventory inv)
        {
            Set = set;
            _inv = inv;

            Info = AlsFile.Read(set.Path);
            if (!string.IsNullOrEmpty(Info.Error))
            {
                // Сам .als не читается — тут никакие плагины уже ни при чём, и об этом
                // надо сказать прямо, а не гонять человека по пробам.
                Error = Info.Error;
                Targets = new List<AlsPluginSlot>();
                return;
            }

            // Дочитываем то, чего минимальный SetEntry мог не знать — например, у
            // вызывающего был только путь к файлу (см. ReelWindow.RescueCurrent), а не
            // запись из каталога. Читать .als второй раз только за этим не стоит: у
            // тяжёлых сетов один проход и так не бесплатен (см. AlsPatch).
            if (set.Creator.Length == 0) set.Creator = Info.Creator;

            Targets = AlsPatch.Targets(Info);
            Unaddressable = AlsPatch.Unaddressable(Info);
            Suspects.AddRange(Targets);

            Diagnose();
        }

        public bool HasTargets { get { return Targets.Count > 0; } }
        public bool Finished { get { return Verdict == RescueVerdict.Culprit
                                          || Verdict == RescueVerdict.NotPlugins
                                          || Verdict == RescueVerdict.Group; } }

        /// <summary>Строки хода расследования — их же показывает окно и уносит отчёт.</summary>
        public IList<string> Trail { get { return _log; } }

        // ------------------------------------------------------------------ диагноз

        /// <summary>
        /// Спросить журнал Live до всяких проб. Если сет уже роняли, Live записала, на
        /// каком плагине оборвалась, — и первую пробу можно начинать сразу с него.
        /// </summary>
        void Diagnose()
        {
            History = LiveLog.LastAttempt(Set.Path);
            if (History == null) return;

            PluginLoad hung = History.Hung;
            if (hung != null) HistorySuspect = Match(hung.Name);

            if (History.Result == LoadResult.Loaded)
                Note("Live's log says this set opened fine on " +
                     History.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
            else if (hung != null)
                Note("Live's log stops inside " + hung.Format + " " + hung.Name +
                     " (" + History.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ")");
            else
                Note("Live's log has an unfinished attempt from " +
                     History.Started.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// Имя из журнала — в плагин сета. Сперва точно: и журнал, и .als берут имя из
        /// одного и того же места, так что обычно оно совпадает буква в букву. Мягкое
        /// сравнение — для случая «Serum_x64» против «Serum (64 Bit)».
        /// </summary>
        public AlsPluginSlot Match(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (AlsPluginSlot s in Targets)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)) return s;

            string norm = PluginInventory.Normalize(name);
            if (norm.Length == 0) return null;
            foreach (AlsPluginSlot s in Targets)
                if (PluginInventory.Normalize(s.Name) == norm) return s;
            return null;
        }

        // ------------------------------------------------------------------ что пробовать

        /// <summary>
        /// Что предложить отключить в следующей пробе. Предложение, а не приказ: галочки
        /// в окне человек правит сам, и Apply разберётся с любым набором.
        /// </summary>
        public List<AlsPluginSlot> Suggest()
        {
            List<AlsPluginSlot> pick = new List<AlsPluginSlot>();
            if (Targets.Count == 0) return pick;

            // Первая проба: если журнал уже назвал подозреваемого — сразу его, одного.
            // Угадали — расследование кончилось на первой же пробе, а не на пятой.
            if (Working == null)
            {
                if (Round == 0 && HistorySuspect != null) { pick.Add(HistorySuspect); return pick; }
                pick.AddRange(Targets);
                return pick;
            }

            if (Suspects.Count > 1)
            {
                int half = Suspects.Count / 2;
                for (int i = 0; i < half; i++) pick.Add(Suspects[i]);
                return pick;
            }

            if (Suspects.Count == 1) { pick.Add(Suspects[0]); return pick; }

            pick.AddRange(Working);
            return pick;
        }

        /// <summary>
        /// Собрать пробную копию с отключёнными disable. Возвращает путь или бросает —
        /// не записать пробу значит не начать, и молчать об этом нельзя.
        /// </summary>
        public string Prepare(List<AlsPluginSlot> disable)
        {
            if (disable == null || disable.Count == 0)
                throw new InvalidOperationException("Pick at least one plugin to disable.");

            Cancel();

            List<string> uids = new List<string>();
            foreach (AlsPluginSlot s in disable) uids.Add(s.Uid);

            string probe = RescueProbe.PathFor(Set);
            RescueProbe.Remember(probe);            // сначала в журнал, потом на диск: упасть можно и между
            int patched = AlsPatch.Neutralize(Set.Path, probe, uids, _inv);
            if (patched == 0)
            {
                RescueProbe.Drop(probe);
                throw new InvalidOperationException("None of those plugins were found inside the set.");
            }

            ProbePath = probe;
            ProbeDisabled = new List<AlsPluginSlot>(disable);
            ProbeStarted = DateTime.Now;
            Round++;

            // Журналы Live читаем с текущего конца: что было до пробы, уже разобрано
            // в Diagnose, и путать прошлые попытки с этой не нужно.
            _logs.Clear();
            foreach (LiveLogFile f in LiveLog.Files())
            {
                f.SkipToEnd();
                _logs[f.Path] = f;
            }

            Note("Probe " + Round + ": " + Describe(disable) + " disabled");
            return probe;
        }

        /// <summary>Открыть пробу в Live — тем же способом, каким её открыл бы двойной щелчок.</summary>
        public void Launch()
        {
            if (ProbePath.Length == 0) throw new InvalidOperationException("No probe prepared.");
            Process.Start(new ProcessStartInfo(ProbePath) { UseShellExecute = true });
        }

        public void Cancel()
        {
            if (ProbePath.Length == 0) return;
            RescueProbe.Drop(ProbePath);
            ProbePath = "";
            ProbeDisabled = new List<AlsPluginSlot>();
        }

        // ------------------------------------------------------------------ исход пробы

        /// <summary>
        /// Что журналы Live говорят о текущей пробе. null — Live её ещё не открывала.
        /// Файлы перечитываются каждый раз заново: версия Live, которую человек запустит,
        /// заранее не известна, а свежая установка могла и не иметь Log.txt до сих пор.
        /// </summary>
        public LoadAttempt Poll()
        {
            if (ProbePath.Length == 0) return null;

            foreach (LiveLogFile f in LiveLog.Files())
                if (!_logs.ContainsKey(f.Path)) _logs[f.Path] = f;   // с нуля: файл появился только что

            LoadAttempt best = null;
            foreach (LiveLogFile f in _logs.Values)
            {
                foreach (LoadAttempt a in f.ReadNew())
                {
                    if (!LiveLog.SamePath(a.Document, ProbePath)) continue;
                    if (best == null || a.Started >= best.Started) best = a;
                }
            }
            return best;
        }

        /// <summary>Запущена ли Live прямо сейчас — по ней отличаем «ещё грузит» от «умерла».</summary>
        public static bool LiveIsRunning()
        {
            Process[] all;
            try { all = Process.GetProcesses(); }
            catch (Exception ex) { Diag.Fail("rescue: process list", ex); return false; }

            bool found = false;
            foreach (Process p in all)
            {
                try
                {
                    // Имя процесса — это имя exe: «Ableton Live 12 Suite». Сравниваем по
                    // началу, потому что редакция и версия у всех разные.
                    if (!found && p.ProcessName.StartsWith("Ableton Live", StringComparison.OrdinalIgnoreCase))
                        found = true;
                }
                catch { }
                // Каждый Process держит системный хендл, а спрашивают отсюда раз в секунду.
                finally { p.Dispose(); }
            }
            return found;
        }

        /// <summary>
        /// Учесть исход пробы и сузить круг. opened — сет открылся целиком.
        ///
        /// Набор отключённого передаётся явно, а не берётся из ProbeDisabled: тогда ход
        /// расследования можно прогнать без единого файла на диске и без Live — чем и
        /// проверяется сходимость (см. tools\RescueTest.cs, команда simulate).
        /// </summary>
        public void Apply(List<AlsPluginSlot> off, bool opened, LoadAttempt attempt)
        {
            if (off == null) off = ProbeDisabled;
            Cancel();

            if (opened)
            {
                Note("  → opened with " + off.Count + " disabled");
                if (Working == null || off.Count < Working.Count) Working = new List<AlsPluginSlot>(off);
                Intersect(Suspects, off);
            }
            else
            {
                string where = "";
                if (attempt != null && attempt.Hung != null) where = " — stopped inside " + attempt.Hung.Name;
                Note("  → did not open" + where);

                // Журнал назвал плагин, на котором оборвалось: он и виновен, дальше
                // делить пополам незачем. Берём подсказку только если этот плагин
                // действительно был включён — иначе она про что-то другое.
                AlsPluginSlot named = attempt != null && attempt.Hung != null
                                    ? Match(attempt.Hung.Name) : null;
                if (named != null && !Contains(off, named) && Contains(Suspects, named))
                {
                    Suspects.Clear();
                    Suspects.Add(named);
                }
                else Subtract(Suspects, off);
            }

            Settle(opened, off);
        }

        void Settle(bool opened, List<AlsPluginSlot> off)
        {
            // Отключили всё, что можно, и всё равно не открылось — плагины ни при чём.
            if (!opened && Working == null && off.Count == Targets.Count)
            {
                Verdict = RescueVerdict.NotPlugins;
                Note("Verdict: the plugins are not the problem — the set fails with all of them disabled.");
                return;
            }

            if (Working == null) { Verdict = RescueVerdict.None; return; }

            if (Suspects.Count == 0)
            {
                // Пусто — значит виновен не один плагин, и красивого имени не будет.
                // Зато Working проверен на деле: с ним сет открывался.
                Verdict = RescueVerdict.Group;
                Note("Verdict: more than one plugin is involved. Disabling " +
                     Describe(Working) + " opens the set.");
                return;
            }

            if (opened && off.Count == 1 && Suspects.Count == 1)
            {
                Culprit = Suspects[0];
                Verdict = RescueVerdict.Culprit;
                Note("Verdict: " + Culprit.Format + " " + Culprit.Name + " breaks this set.");
                return;
            }

            Verdict = RescueVerdict.None;
        }

        // ------------------------------------------------------------------ спасённая копия

        /// <summary>
        /// Что отключать в спасённой копии: найденного виновника, а если виновных
        /// несколько — весь проверенный набор.
        /// </summary>
        public List<AlsPluginSlot> RescueSelection()
        {
            List<AlsPluginSlot> pick = new List<AlsPluginSlot>();
            if (Culprit != null) pick.Add(Culprit);
            else if (Working != null) pick.AddRange(Working);
            return pick;
        }

        public string RescuedPath
        {
            get { return Path.Combine(Set.Directory, Set.Name + " (rescued).als"); }
        }

        /// <summary>
        /// Копия рядом с оригиналом, где виновный плагин обезличен. Оригинал не трогаем
        /// ни при каком исходе: он ещё пригодится, когда плагин обновят или переставят.
        /// </summary>
        public string SaveRescued(List<AlsPluginSlot> disable)
        {
            if (disable == null || disable.Count == 0)
                throw new InvalidOperationException("Nothing to disable in the rescued copy.");

            string dst = Unique(RescuedPath);
            List<string> uids = new List<string>();
            foreach (AlsPluginSlot s in disable) uids.Add(s.Uid);

            AlsPatch.Neutralize(Set.Path, dst, uids, _inv);
            Note("Saved " + Path.GetFileName(dst));
            return dst;
        }

        static string Unique(string path)
        {
            if (!File.Exists(path)) return path;
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 2; i < 1000; i++)
            {
                string p = Path.Combine(dir, name + " " + i.ToString(CultureInfo.InvariantCulture) + ext);
                if (!File.Exists(p)) return p;
            }
            return path;
        }

        // ------------------------------------------------------------------ отчёт

        public string Report()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Alive — project rescue report");
            sb.AppendLine("Set:      " + Set.Path);
            if (Set.Creator.Length > 0) sb.AppendLine("Saved by: " + Set.Creator);
            sb.AppendLine("Plugins:  " + Targets.Count + " third-party"
                          + (Unaddressable > 0 ? " (" + Unaddressable + " unidentifiable)" : ""));
            sb.AppendLine();

            if (History != null)
            {
                sb.AppendLine("Live's own log (" + History.LiveVersion + "):");
                sb.AppendLine("  attempt at " + History.Started.ToString("yyyy-MM-dd HH:mm:ss",
                                                                        CultureInfo.InvariantCulture));
                sb.AppendLine("  restored " + History.RestoredCount + " of " + History.Plugins.Count + " plugins");
                foreach (PluginLoad p in History.Failures)
                    sb.AppendLine("  refused:  " + p.Format + " " + p.Name);
                if (History.Hung != null)
                    sb.AppendLine("  stops in: " + History.Hung.Format + " " + History.Hung.Name);
                sb.AppendLine();
            }

            if (_log.Count > 0)
            {
                sb.AppendLine("Probes:");
                foreach (string line in _log) sb.AppendLine("  " + line);
                sb.AppendLine();
            }

            sb.AppendLine("Verdict: " + VerdictText());
            return sb.ToString();
        }

        public string VerdictText()
        {
            switch (Verdict)
            {
                case RescueVerdict.Culprit:
                    return Culprit.Format + " " + Culprit.Name + " breaks this set.";
                case RescueVerdict.Group:
                    return "more than one plugin is involved; disabling " + Describe(Working)
                         + " opens the set.";
                case RescueVerdict.NotPlugins:
                    return "not a plugin problem — the set fails with every third-party plugin disabled.";
                case RescueVerdict.NoProbeYet:
                    return "no probe run yet.";
                default:
                    return Suspects.Count + " plugin(s) still suspected.";
            }
        }

        // ------------------------------------------------------------------ мелочи

        void Note(string line)
        {
            _log.Add(line);
            Diag.Line("rescue: " + line);
        }

        public static string Describe(List<AlsPluginSlot> list)
        {
            if (list == null || list.Count == 0) return "nothing";
            if (list.Count == 1) return list[0].Name;
            if (list.Count <= 3)
            {
                string[] names = new string[list.Count];
                for (int i = 0; i < list.Count; i++) names[i] = list[i].Name;
                return string.Join(", ", names);
            }
            return list.Count + " plugins";
        }

        static bool Contains(List<AlsPluginSlot> list, AlsPluginSlot s)
        {
            foreach (AlsPluginSlot x in list)
                if (string.Equals(x.Uid, s.Uid, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static void Intersect(List<AlsPluginSlot> target, List<AlsPluginSlot> keep)
        {
            for (int i = target.Count - 1; i >= 0; i--)
                if (!Contains(keep, target[i])) target.RemoveAt(i);
        }

        static void Subtract(List<AlsPluginSlot> target, List<AlsPluginSlot> drop)
        {
            for (int i = target.Count - 1; i >= 0; i--)
                if (Contains(drop, target[i])) target.RemoveAt(i);
        }
    }
}
