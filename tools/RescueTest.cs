using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// Консольная проверка помощника по восстановлению: разбор журнала Live и правка
    /// копии сета. Оба куска работают с чужими файлами, которые нельзя подделать
    /// правдоподобно, поэтому проверяются на настоящих — на журнале этой машины и на
    /// настоящем .als.
    ///
    /// Сборка: tools\build-rescue-test.cmd. В bin не попадает — это инструмент проверки,
    /// а не часть программы.
    ///
    ///     RescueTest.exe logs                 все оборванные загрузки во всех журналах Live
    ///     RescueTest.exe log   &lt;сет.als&gt;      что журнал помнит про этот сет
    ///     RescueTest.exe patch &lt;сет.als&gt;      отключить все плагины копии и сверить XML
    /// </summary>
    internal static class RescueTest
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage: RescueTest.exe logs | log <set.als> | patch <set.als>");
                return 2;
            }

            try
            {
                switch (args[0])
                {
                    case "logs": return Logs();
                    case "log": return One(args[1]);
                    case "patch": return Patch(args[1]);
                    case "simulate": return Simulate(args[1]);
                    case "probe": return Probe(args[1]);
                    default:
                        Console.WriteLine("unknown command: " + args[0]);
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAILED: " + ex);
                return 1;
            }
        }

        // ------------------------------------------------------------------ журналы

        static int Logs()
        {
            List<LiveLogFile> files = LiveLog.Files();
            Console.WriteLine("log files: " + files.Count);

            int total = 0, broke = 0, loaded = 0;
            int seen = 0, ok = 0, refused = 0, hungCount = 0;
            foreach (LiveLogFile f in files)
            {
                List<LoadAttempt> all = f.All();
                int fSeen = 0, fOk = 0, fBad = 0, fHung = 0;
                foreach (LoadAttempt a in all)
                    foreach (PluginLoad p in a.Plugins)
                    {
                        fSeen++;
                        if (p.Restored) fOk++;
                        else if (p.Failed) fBad++;
                        else fHung++;
                    }
                seen += fSeen; ok += fOk; refused += fBad; hungCount += fHung;

                Console.WriteLine();
                Console.WriteLine(f.Version + "  (" + all.Count + " attempts, "
                                  + (f.Length / 1024) + " KB)   plugins: " + fSeen
                                  + " seen, " + fOk + " restored, " + fBad + " refused, " + fHung + " hung");

                foreach (LoadAttempt a in all)
                {
                    total++;
                    if (a.Result == LoadResult.Loaded) { loaded++; continue; }
                    broke++;

                    PluginLoad hung = a.Hung;
                    Console.WriteLine("  " + a.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        + "  " + Short(a.Document));
                    Console.WriteLine("      restored " + a.RestoredCount + "/" + a.Plugins.Count
                        + (hung != null ? "   STOPS IN: " + hung.Format + " " + hung.Name : "   (no plugin pending)"));
                    foreach (PluginLoad p in a.Failures)
                        Console.WriteLine("      refused:  " + p.Format + " " + p.Name);
                }
            }

            Console.WriteLine();
            Console.WriteLine("total " + total + " attempts: " + loaded + " loaded, " + broke + " broke");
            Console.WriteLine("total " + seen + " plugin loads: " + ok + " restored, "
                              + refused + " refused, " + hungCount + " hung");
            return 0;
        }

        static int One(string als)
        {
            Console.WriteLine("set: " + als);
            LoadAttempt a = LiveLog.LastAttempt(als);
            if (a == null) { Console.WriteLine("no attempt found in any Live log"); return 0; }

            Console.WriteLine("live:     " + a.LiveVersion);
            Console.WriteLine("started:  " + a.Started.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            Console.WriteLine("result:   " + a.Result);
            Console.WriteLine("saved by: " + a.CreatedBy);
            Console.WriteLine("plugins:  " + a.RestoredCount + " restored of " + a.Plugins.Count);
            foreach (PluginLoad p in a.Plugins)
                Console.WriteLine("   " + (p.Restored ? "ok    " : p.Failed ? "FAILED" : "HUNG  ")
                                  + " " + p.Format + " " + p.Name);
            return 0;
        }

        // ------------------------------------------------------------------ правка

        static int Patch(string als)
        {
            AlsInfo info = AlsFile.Read(als);
            if (!string.IsNullOrEmpty(info.Error)) { Console.WriteLine("read failed: " + info.Error); return 1; }

            List<AlsPluginSlot> targets = AlsPatch.Targets(info);
            Console.WriteLine("set:     " + als);
            Console.WriteLine("plugins: " + targets.Count + " addressable, "
                              + AlsPatch.Unaddressable(info) + " without an id");
            foreach (AlsPluginSlot s in targets)
                Console.WriteLine("   " + s.Format + "  " + s.Uid + "  " + s.Label);
            if (targets.Count == 0) { Console.WriteLine("nothing to disable"); return 0; }

            List<string> uids = new List<string>();
            foreach (AlsPluginSlot s in targets) uids.Add(s.Uid);

            string dst = Path.Combine(Path.GetTempPath(), "alive-patch-check.als");
            PluginInventory inv = PluginInventory.Load();
            int patched = AlsPatch.Neutralize(als, dst, uids, inv);
            Console.WriteLine();
            Console.WriteLine("patched " + patched + " device node(s) -> " + dst);

            // Главная проверка: в файле не должно поменяться НИЧЕГО, кроме тех самых
            // Value. Сравниваем построчно распакованный XML до и после.
            string[] before = Lines(als), after = Lines(dst);
            if (before.Length != after.Length)
            {
                Console.WriteLine("MISMATCH: line count " + before.Length + " -> " + after.Length);
                return 1;
            }

            int changed = 0, unexpected = 0;
            for (int i = 0; i < before.Length; i++)
            {
                if (before[i] == after[i]) continue;
                changed++;
                string tag = before[i].Trim();
                bool ok = tag.StartsWith("<Fields.0 ", StringComparison.Ordinal)
                       || tag.StartsWith("<UniqueId ", StringComparison.Ordinal)
                       || tag.StartsWith("<Path ", StringComparison.Ordinal);
                if (!ok) { unexpected++; Console.WriteLine("  UNEXPECTED: " + tag); }
                else if (changed <= 8)
                    Console.WriteLine("  " + tag + "\n           -> " + after[i].Trim());
            }
            Console.WriteLine("changed lines: " + changed + ", unexpected: " + unexpected);

            // И обратная проверка: разбор копии должен видеть ДРУГИЕ идентификаторы у
            // тех же самых плагинов — то есть Live их уже не узнает.
            AlsInfo patchedInfo = AlsFile.Read(dst);
            if (!string.IsNullOrEmpty(patchedInfo.Error))
            {
                Console.WriteLine("PATCHED FILE UNREADABLE: " + patchedInfo.Error);
                return 1;
            }

            HashSet<string> was = new HashSet<string>(uids, StringComparer.OrdinalIgnoreCase);
            int still = 0, known = 0;
            List<AlsPluginSlot> now = AlsPatch.Targets(patchedInfo);
            Console.WriteLine();
            Console.WriteLine("after patch: " + now.Count + " plugin(s), same names, new ids");
            foreach (AlsPluginSlot s in now)
            {
                if (was.Contains(s.Uid)) still++;
                if (inv.ByUid(s.Uid) != null) known++;
                Console.WriteLine("   " + s.Format + "  " + s.Uid + "  " + s.Label
                                  + (inv.ByUid(s.Uid) != null ? "   <-- STILL INSTALLED" : ""));
            }

            Console.WriteLine();
            Console.WriteLine("names kept:            " + (now.Count == targets.Count ? "yes" : "NO"));
            Console.WriteLine("old ids left in file:  " + still + " (must be 0)");
            Console.WriteLine("new ids Live knows:    " + known + " (must be 0)");

            bool pass = unexpected == 0 && still == 0 && known == 0 && now.Count == targets.Count;
            Console.WriteLine();
            Console.WriteLine(pass ? "PASS" : "FAIL");
            return pass ? 0 : 1;
        }

        // ------------------------------------------------------------------ сходимость

        /// <summary>
        /// Прогоняет расследование по кругу, назначая виновным по очереди каждый плагин
        /// сета. Ни одного файла и ни одного запуска Live: исход пробы известен заранее —
        /// сет откроется тогда и только тогда, когда виновник в отключённых.
        ///
        /// Проверяется ровно то, что нельзя проверить глазами: что поиск всегда сходится,
        /// называет того самого и не уходит в бесконечный круг проб.
        /// </summary>
        static int Simulate(string als)
        {
            AlsInfo info = AlsFile.Read(als);
            if (!string.IsNullOrEmpty(info.Error)) { Console.WriteLine("read failed: " + info.Error); return 1; }

            SetEntry set = new SetEntry();
            set.Path = als;
            set.Name = Path.GetFileNameWithoutExtension(als);

            List<AlsPluginSlot> all = AlsPatch.Targets(info);
            Console.WriteLine("set:     " + Path.GetFileName(als));
            Console.WriteLine("plugins: " + all.Count);
            Console.WriteLine();

            int worst = 0, failures = 0;
            foreach (AlsPluginSlot guilty in all)
            {
                RescueSession s = new RescueSession(set, null);

                // Подсказку из журнала Live гасим: она относится к настоящей истории
                // этого сета, а виновного тут назначаем мы.
                s.History = null;
                s.HistorySuspect = null;

                int probes = 0;
                while (!s.Finished && probes < 64)
                {
                    List<AlsPluginSlot> off = s.Suggest();
                    if (off.Count == 0) break;
                    probes++;
                    s.Apply(off, Has(off, guilty), null);
                }

                bool ok = s.Verdict == RescueVerdict.Culprit
                       && s.Culprit != null && s.Culprit.Uid == guilty.Uid;
                if (!ok) failures++;
                if (probes > worst) worst = probes;

                Console.WriteLine((ok ? "  ok   " : "  FAIL ") + probes + " probes  "
                                  + guilty.Name.PadRight(22) + " -> " + s.VerdictText());
            }

            // И отдельный случай: виноват вообще не плагин. Первая же проба отключает всё
            // и всё равно не открывается — расследование обязано это признать, а не искать.
            RescueSession n = new RescueSession(set, null);
            n.History = null; n.HistorySuspect = null;
            int rounds = 0;
            while (!n.Finished && rounds < 64)
            {
                List<AlsPluginSlot> off = n.Suggest();
                if (off.Count == 0) break;
                rounds++;
                n.Apply(off, false, null);
            }
            bool notPlugins = n.Verdict == RescueVerdict.NotPlugins;
            Console.WriteLine();
            Console.WriteLine((notPlugins ? "  ok   " : "  FAIL ") + rounds + " probes  "
                              + "not a plugin at all".PadRight(22) + " -> " + n.VerdictText());

            Console.WriteLine();
            Console.WriteLine("worst case: " + worst + " probes for " + all.Count + " plugins");
            Console.WriteLine(failures == 0 && notPlugins ? "PASS" : "FAIL (" + failures + " wrong verdicts)");
            return failures == 0 && notPlugins ? 0 : 1;
        }

        // ------------------------------------------------------------ жизнь пробного файла

        /// <summary>
        /// Проба появляется рядом с сетом, попадает в журнал уборки и исчезает. Проверять
        /// это стоит отдельно: пробный .als лежит в чужой папке проекта, и всё, что тут
        /// может пойти не так, кончается мусором у человека в проектах.
        ///
        /// Запускать на КОПИИ сета — файл кладётся рядом с тем, что дали.
        /// </summary>
        static int Probe(string als)
        {
            SetEntry set = new SetEntry();
            set.Path = als;
            set.Name = Path.GetFileNameWithoutExtension(als);

            RescueSession s = new RescueSession(set, PluginInventory.Load());
            if (s.Targets.Count == 0) { Console.WriteLine("no plugins to disable here"); return 1; }

            string expected = RescueProbe.PathFor(set);
            Console.WriteLine("probe path: " + expected);

            string made = s.Prepare(s.Suggest());
            bool onDisk = File.Exists(made);
            bool listed = Journal().Contains(made);
            bool named = RescueProbe.IsProbe(made) && made == expected;
            Console.WriteLine("  written:  " + onDisk + "   journalled: " + listed + "   named: " + named);

            // Каталог не должен её видеть — иначе проба приедет в список сетов рядом с настоящим.
            bool hidden = true;
            FolderScan.Find(Path.GetDirectoryName(als), ".als", true,
                            delegate (string f) { if (f == made) hidden = false; }, null);
            Console.WriteLine("  invisible to the catalog: " + hidden);

            s.Cancel();
            bool gone = !File.Exists(made);
            bool cleared = !Journal().Contains(made);
            Console.WriteLine("  removed:  " + gone + "   journal cleared: " + cleared);

            // И уборка после падения: запись без файла журнал обязан вычистить молча.
            RescueProbe.Remember(expected);
            RescueProbe.CleanupStale();
            bool swept = Journal().Count == 0;
            Console.WriteLine("  stale entry swept: " + swept);

            // Ради этого числа правка и переписана на поток: раньше распакованный XML
            // поднимался целиком, и на тяжёлом сете это под гигабайт.
            Console.WriteLine("  peak working set: "
                              + (Process.GetCurrentProcess().PeakWorkingSet64 / 1048576) + " MB");

            bool pass = onDisk && listed && named && hidden && gone && cleared && swept;
            Console.WriteLine();
            Console.WriteLine(pass ? "PASS" : "FAIL");
            return pass ? 0 : 1;
        }

        static List<string> Journal()
        {
            string p = Path.Combine(Settings.Dir, "probes.txt");
            List<string> all = new List<string>();
            if (!File.Exists(p)) return all;
            foreach (string line in File.ReadAllLines(p))
                if (line.Trim().Length > 0) all.Add(line.Trim());
            return all;
        }

        static bool Has(List<AlsPluginSlot> list, AlsPluginSlot s)
        {
            foreach (AlsPluginSlot x in list) if (x.Uid == s.Uid) return true;
            return false;
        }

        static string[] Lines(string als)
        {
            using (FileStream fs = new FileStream(als, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (GZipStream gz = new GZipStream(fs, CompressionMode.Decompress))
            using (StreamReader r = new StreamReader(gz, new UTF8Encoding(false)))
                return r.ReadToEnd().Replace("\r\n", "\n").Split('\n');
        }

        static string Short(string path)
        {
            try { return Path.GetFileName(path); }
            catch { return path; }
        }
    }
}
