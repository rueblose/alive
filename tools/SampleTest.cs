using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// Стенд для модели сэмплов. Не входит в поставку — он проверяет вещь, а не является ею.
    ///
    ///     SampleTest.exe scan &lt;корень или .als&gt;   разбивка по категориям, сверка сумм
    ///
    /// Сборка: tools\build-sample-test.cmd
    /// </summary>
    internal static class SampleTest
    {
        static int _checks, _failed;

        static void Check(bool ok, string what)
        {
            _checks++;
            if (ok) return;
            _failed++;
            Console.WriteLine("FAIL: " + what);
        }

        [STAThread]
        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string arg = args.Length > 1 ? args[1] : "";

            if (cmd == "scan") Scan(arg);
            else if (cmd == "patch") Patch(arg);
            else if (cmd == "collect") Collect(arg);
            else if (cmd == "show") Show(arg);
            else
            {
                Console.WriteLine("usage: SampleTest.exe scan <folder or .als>");
                Console.WriteLine("       SampleTest.exe patch <set.als>");
                Console.WriteLine("       SampleTest.exe collect <set.als>");
                Console.WriteLine("       SampleTest.exe show <set.als>");
                return 2;
            }

            Console.WriteLine();
            if (_failed > 0)
            {
                Console.WriteLine(string.Format("{0} of {1} checks FAILED", _failed, _checks));
                return 1;
            }
            Console.WriteLine(string.Format("OK: {0} checks passed", _checks));
            return 0;
        }

        static List<string> SetsUnder(string path)
        {
            List<string> list = new List<string>();
            if (File.Exists(path)) { list.Add(path); return list; }
            if (!Directory.Exists(path)) return list;
            foreach (string f in Directory.GetFiles(path, "*.als", SearchOption.AllDirectories))
                if (f.IndexOf(@"\Backup\", StringComparison.OrdinalIgnoreCase) < 0)
                    list.Add(f);
            return list;
        }

        static void Scan(string path)
        {
            LiveEnvironment env = LiveEnvironment.Detect();
            List<string> sets = SetsUnder(path);
            Check(sets.Count > 0, "no .als found under " + path);

            int[] byOrigin = new int[6];
            long[] bytesByOrigin = new long[6];
            int devices = 0, totalRefs = 0, totalDeps = 0, dirDeps = 0;

            foreach (string file in sets)
            {
                AlsInfo info = AlsFile.Read(file);
                if (info.Error != null) continue;

                List<SampleDep> deps = SampleScan.Of(info, Path.GetDirectoryName(file), env);

                // Каждая ссылка учтена ровно один раз: сумма RefIndexes по всем
                // зависимостям равна числу отобранных FileRef, и номера не повторяются.
                HashSet<int> seen = new HashSet<int>();
                int refsHere = 0;
                foreach (SampleDep d in deps)
                {
                    Check(d.RefIndexes.Count > 0, "dep without RefIndexes in " + file);
                    foreach (int i in d.RefIndexes)
                    {
                        Check(seen.Add(i), "FileRef " + i + " counted twice in " + file);
                        Check(i >= 0 && i < info.Files.Count, "RefIndex out of range in " + file);
                        refsHere++;
                    }
                    byOrigin[(int)d.Origin]++;
                    bytesByOrigin[(int)d.Origin] += d.Size;
                    if (d.IsDevice) devices++;

                    Check(d.Origin != SampleOrigin.Missing || d.Size == 0,
                          "missing dep has a size in " + file);
                    Check(d.Origin != SampleOrigin.FactoryPack || d.PackName.Length > 0,
                          "FactoryPack dep without a pack name in " + file);

                    // Найденная зависимость-папка (.adg/.amxd бывают папками) должна
                    // иметь ненулевой размер — иначе она неотличима от «не нашли».
                    if (d.Origin != SampleOrigin.Missing && Directory.Exists(d.Path))
                    {
                        dirDeps++;
                        Check(d.Size > 0, "folder dep has zero size in " + file + ": " + d.Path);
                    }
                }

                int expected = 0;
                foreach (FileRefInfo fr in info.Files)
                {
                    bool device = string.Equals(fr.Container, "MxPatchRef", StringComparison.Ordinal);
                    if (!fr.IsSampleDependency && !device) continue;
                    if (fr.RelativePath.Length == 0 && fr.AbsolutePath.Length == 0) continue;
                    expected++;
                }
                Check(refsHere == expected,
                      string.Format("{0}: covered {1} refs, expected {2}", Path.GetFileName(file), refsHere, expected));

                totalRefs += refsHere;
                totalDeps += deps.Count;
            }

            Console.WriteLine(string.Format("sets={0}  refs={1}  distinct files={2}  devices={3}",
                                            sets.Count, totalRefs, totalDeps, devices));
            Console.WriteLine(string.Format("folder deps (size checked): {0}", dirDeps));
            Console.WriteLine();
            string[] names = { "InProject", "OtherProject", "UserLibrary", "FactoryPack", "Elsewhere", "Missing" };
            for (int i = 0; i < names.Length; i++)
                Console.WriteLine(string.Format("{0,-14} {1,6} files  {2,10:N1} MB",
                                                names[i], byOrigin[i], bytesByOrigin[i] / 1048576.0));
        }

        /// <summary>
        /// Круг: переписать пути у части ссылок, прочитать результат тем же AlsFile и
        /// убедиться, что поменялось ровно заказанное и ровно на заказанное, а всё
        /// остальное осталось прежним. Это тот самый инвариант, ради которого патчер
        /// адресует узлы по номеру, а не по содержимому.
        /// </summary>
        static void Patch(string file)
        {
            if (!File.Exists(file)) { Check(false, "no such set: " + file); return; }

            LiveEnvironment env = LiveEnvironment.Detect();
            AlsInfo before = AlsFile.Read(file);
            Check(before.Error == null, "cannot read " + file);
            if (before.Error != null) return;

            List<SampleDep> deps = SampleScan.Of(before, Path.GetDirectoryName(file), env);
            Check(deps.Count > 0, "no sample dependencies in " + file);
            if (deps.Count == 0) return;

            // Берём каждую вторую зависимость — так проверяется и что тронутое
            // изменилось, и что нетронутое рядом с ним уцелело. «Drum & Bass» в каждом
            // пробном пути — не украшение: Escape() экранирует «&» в XML, и до этого
            // проб ни один прогон стенда символ «&» вообще не трогал (пути были без
            // спецсимволов), хотя это ровно тот случай, что ревью назвало одним из двух,
            // тихо ломающих файл.
            Dictionary<int, NewRef> rewrites = new Dictionary<int, NewRef>();
            HashSet<int> touched = new HashSet<int>();
            int n = 0;
            foreach (SampleDep d in deps)
            {
                if ((n++ % 2) != 0) continue;
                NewRef nr = new NewRef();
                nr.RelativePath = "Samples/Imported/Drum & Bass " + n + ".wav";
                nr.AbsolutePath = "C:/probe/Drum & Bass/probe " + n + ".wav";
                nr.RelativePathType = 3;
                foreach (int i in d.RefIndexes) { rewrites[i] = nr; touched.Add(i); }
            }

            string dst = Path.Combine(Path.GetTempPath(), "alive-patch-probe.als");
            int patched = AlsSamplePatch.Rewrite(file, dst, rewrites, before.Files.Count);
            Check(patched == rewrites.Count,
                  string.Format("rewrote {0} nodes, asked for {1}", patched, rewrites.Count));

            AlsInfo after = AlsFile.Read(dst);
            Check(after.Error == null, "patched copy does not parse");
            if (after.Error != null) return;

            Check(after.Files.Count == before.Files.Count, "FileRef count changed");
            Check(after.Plugins.Count == before.Plugins.Count, "plugin count changed");
            Check(after.Tempo == before.Tempo, "tempo changed");
            Check(after.TotalTracks == before.TotalTracks, "track count changed");

            for (int i = 0; i < before.Files.Count && i < after.Files.Count; i++)
            {
                FileRefInfo a = before.Files[i], b = after.Files[i];
                if (touched.Contains(i))
                {
                    NewRef nr = rewrites[i];
                    Check(b.RelativePath == nr.RelativePath, "RelativePath not applied at " + i);
                    Check(b.AbsolutePath == nr.AbsolutePath, "Path not applied at " + i);
                    Check(b.RelativePathType == nr.RelativePathType, "RelativePathType not applied at " + i);
                    Check(b.LivePackName.Length == 0, "LivePackName not cleared at " + i);
                    Check(b.OriginalFileSize == a.OriginalFileSize, "OriginalFileSize touched at " + i);
                }
                else
                {
                    Check(b.RelativePath == a.RelativePath, "untouched RelativePath changed at " + i);
                    Check(b.AbsolutePath == a.AbsolutePath, "untouched Path changed at " + i);
                    Check(b.RelativePathType == a.RelativePathType, "untouched type changed at " + i);
                    Check(b.LivePackName == a.LivePackName, "untouched LivePackName changed at " + i);
                }
            }

            // AlsFile поле LivePackId не разбирает — его в модели просто нет, — а обнулённый
            // LivePackId и есть то, что отцепляет собранную копию от пака: RefResolver ходит
            // по имени пака, а сама Live — по идентификатору. Проверяем по сырому тексту,
            // независимо от модели, тем же правилом обхода узлов, что и сам патчер.
            int packChecked = CheckPackCleared(dst, rewrites);
            Check(packChecked == rewrites.Count, "pack-clear check did not cover all touched nodes");

            // Неверное ожидаемое число узлов обязано убить результат, а не записать его.
            string bad = Path.Combine(Path.GetTempPath(), "alive-patch-bad.als");
            bool threw = false;
            try { AlsSamplePatch.Rewrite(file, bad, rewrites, before.Files.Count + 1); }
            catch (InvalidDataException) { threw = true; }
            Check(threw, "count mismatch did not throw");
            Check(!File.Exists(bad), "failed patch left a file behind");

            try { File.Delete(dst); } catch { }
            Console.WriteLine(string.Format("patched {0} of {1} FileRef in {2}",
                                            patched, before.Files.Count, Path.GetFileName(file)));
            Console.WriteLine(string.Format("pack cleared (raw): {0} touched FileRef checked", packChecked));
        }

        /// <summary>
        /// Собирает сет во временную папку и проверяет главное обещание сборки: каждая
        /// ссылка собранной копии разрешается в существующий файл ВНУТРИ этой папки.
        /// Ради этого сборка и делается, и проверять это надо тем же RefResolver,
        /// которым потом будет пользоваться каталог.
        /// </summary>
        static void Collect(string file)
        {
            // Не зависит от file — CRITICAL 1 финального ревью воспроизводится без
            // единого реального файла на диске (см. комментарий у CollisionProbe).
            CollisionProbe();

            if (!File.Exists(file)) { Check(false, "no such set: " + file); return; }

            LiveEnvironment env = LiveEnvironment.Detect();
            AlsInfo info = AlsFile.Read(file);
            Check(info.Error == null, "cannot read " + file);
            if (info.Error != null) return;

            // Оригинал обязан остаться в точности тем же файлом: Run открывает src
            // только на чтение (FileShare.ReadWrite, а не эксклюзивно), и это главное
            // обещание всей ветки — «оригинал не трогается никогда». Снимок берём тут,
            // до всего, и сверяем в самом конце — после всех вызовов Run() ниже, включая
            // отменённые попытки.
            FileInfo srcBefore = new FileInfo(file);
            long srcLenBefore = srcBefore.Length;
            DateTime srcWriteBefore = srcBefore.LastWriteTimeUtc;

            SetEntry set = new SetEntry();
            set.Path = file;
            set.Name = Path.GetFileNameWithoutExtension(file);

            List<SampleDep> deps = SampleScan.Of(info, Path.GetDirectoryName(file), env);
            Check(deps.Count > 0, "no sample dependencies in " + file);
            if (deps.Count == 0) return;

            CollectOptions opt = new CollectOptions();
            opt.FromFactoryPacks = true;    // в стенде собираем всё, чтобы проверить все ветки

            CollectPlan plan = CollectAll.Plan(set, deps, opt);

            // План обязан разложить каждую зависимость ровно в одну корзину.
            int total = plan.Copy.Count + plan.Skipped.Count + plan.NotFound.Count;
            Check(total == deps.Count,
                  string.Format("plan covers {0} deps of {1}", total, deps.Count));
            Check(plan.Skipped.Count == 0, "nothing should be skipped when all options are on");

            // Разные файлы не должны попасть в одно место назначения.
            HashSet<string> dests = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SampleDep d in plan.Copy)
            {
                string rel;
                Check(plan.Dest.TryGetValue(d, out rel), "no destination for " + d.Name);
                if (rel != null) Check(dests.Add(rel), "two files land on " + rel);
            }

            // Внутрипроектные ссылки не переписываются: их путь в копии верен как есть.
            foreach (SampleDep d in plan.Copy)
                if (d.Origin == SampleOrigin.InProject)
                    foreach (int i in d.RefIndexes)
                        Check(!plan.Rewrites.ContainsKey(i), "in-project ref rewritten at " + i);

            string temp = Path.Combine(Path.GetTempPath(), "alive-collect-probe");
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch { }
            Directory.CreateDirectory(temp);
            plan.TargetDir = Path.Combine(temp, set.Name + " Project");

            CollectAll.Run(plan, set, info, null, System.Threading.CancellationToken.None);

            // done == plan.Copy.Count было тавтологией: done растёт в Run() безусловно,
            // в том числе на каждом провалившемся File.Copy — она не могла упасть, даже
            // если бы не скопировалось ни одного файла. plan.Failed — то же самое место,
            // которое Run() заполняет именно при отказе копирования.
            Check(plan.Failed.Count == 0,
                  string.Format("{0} of {1} files failed to copy", plan.Failed.Count, plan.Copy.Count));

            string copied = Path.Combine(plan.TargetDir, set.Name + ".als");
            Check(File.Exists(copied), "collected .als is missing");
            if (!File.Exists(copied)) return;

            AlsInfo after = AlsFile.Read(copied);
            Check(after.Error == null, "collected .als does not parse");
            if (after.Error != null) return;

            List<SampleDep> afterDeps = SampleScan.Of(after, Path.GetDirectoryName(copied), env);
            int outside = 0, missing = 0;
            foreach (SampleDep d in afterDeps)
            {
                if (d.Origin == SampleOrigin.Missing) { missing++; continue; }
                if (d.Origin != SampleOrigin.InProject) outside++;
            }
            Check(outside == 0, string.Format("{0} refs still point outside the collected folder", outside));
            Check(missing == plan.NotFound.Count,
                  string.Format("{0} missing after collect, {1} were missing before", missing, plan.NotFound.Count));

            Console.WriteLine(string.Format("collected {0} files, {1:N1} MB -> {2}",
                                            plan.Copy.Count, plan.TotalBytes / 1048576.0, plan.TargetDir));

            // Run() при обрыве обязан удалить только ту папку, которую создал сам:
            // TargetDir у двух Plan() по одному сету может совпасть, и снос чужой
            // уже собранной копии — потеря данных. Проверяем оба исхода отдельным
            // Run с заранее отменённым токеном: cancel.ThrowIfCancellationRequested()
            // в цикле копирования бросает на первой же итерации.
            System.Threading.CancellationTokenSource cts = new System.Threading.CancellationTokenSource();
            cts.Cancel();

            // Свежая папка — Run создаёт её сам, при обрыве обязана исчезнуть.
            CollectPlan freshPlan = CollectAll.Plan(set, deps, opt);
            freshPlan.TargetDir = Path.Combine(temp, "cancel-fresh Project");
            bool threwFresh = false;
            try { CollectAll.Run(freshPlan, set, info, null, cts.Token); }
            catch (OperationCanceledException) { threwFresh = true; }
            Check(threwFresh, "cancelled Run did not throw on a fresh target dir");
            Check(!Directory.Exists(freshPlan.TargetDir), "cancelled Run left behind a folder it created itself");

            // Уже существующая папка с чужим файлом — Run её не создавал, при обрыве
            // обязана остаться нетронутой вместе с содержимым.
            CollectPlan existingPlan = CollectAll.Plan(set, deps, opt);
            existingPlan.TargetDir = Path.Combine(temp, "cancel-existing Project");
            Directory.CreateDirectory(existingPlan.TargetDir);
            string marker = Path.Combine(existingPlan.TargetDir, "someone else's file.txt");
            File.WriteAllText(marker, "not ours", new UTF8Encoding(false));
            bool threwExisting = false;
            try { CollectAll.Run(existingPlan, set, info, null, cts.Token); }
            catch (OperationCanceledException) { threwExisting = true; }
            Check(threwExisting, "cancelled Run did not throw on a pre-existing target dir");
            Check(Directory.Exists(existingPlan.TargetDir), "cancelled Run deleted a folder it did not create");
            Check(File.Exists(marker), "cancelled Run deleted someone else's file from a pre-existing folder");

            // Тот самый снимок с начала функции — после всех Run() выше, успешного и
            // двух отменённых. Малейшее расхождение здесь означает, что где-то в Run
            // src открылся на запись, а не только на чтение.
            FileInfo srcAfter = new FileInfo(file);
            Check(srcAfter.Length == srcLenBefore, "original .als size changed after collect");
            Check(srcAfter.LastWriteTimeUtc == srcWriteBefore, "original .als write time changed after collect");

            try { Directory.Delete(temp, true); } catch { }
        }

        /// <summary>
        /// CRITICAL 1 (финальное ревью): внешняя зависимость, чьё имя файла совпадает с
        /// внутрипроектной, не должна получить то же место назначения. Раньше Plan()
        /// раздавал имена внешним через Unique() в том же проходе, где InProject только
        /// помечал своё место занятым, не проверяя результат HashSet.Add. Встреться
        /// внешняя зависимость в deps РАНЬШЕ внутрипроектной с тем же именем файла (а
        /// порядок в deps — это порядок FileRef в документе, его выбирает автор сета, не
        /// мы) — Unique() успевал отдать ей будущий путь внутрипроектной прежде, чем та
        /// его застолбит, и обе получали один Dest: Run() копировал их в один и тот же
        /// файл на диске, а переписанная ссылка внешней уже вела туда же.
        ///
        /// Реальный .als не нужен: у Plan() нет побочных эффектов, зависящих от того,
        /// существуют ли исходные файлы на диске, — только SetEntry.ProjectDir и
        /// DriveInfo.AvailableFreeSpace, а оба не бросают на «нет такой папки».
        /// </summary>
        static void CollisionProbe()
        {
            SetEntry set = new SetEntry();
            set.Name = "demo";
            set.Path = Path.Combine(Path.GetTempPath(), @"alive-collision-probe\demo Project\demo.als");
            string setDir = Path.GetDirectoryName(set.Path);

            SampleDep inProject = new SampleDep();
            inProject.Origin = SampleOrigin.InProject;
            inProject.Resolved = new ResolvedRef();
            inProject.Resolved.Status = RefStatus.Found;
            inProject.Resolved.ResolvedPath = Path.Combine(setDir, "Samples", "Imported", "kick.wav");
            inProject.RefIndexes.Add(0);
            inProject.Size = 100;

            SampleDep elsewhere = new SampleDep();
            elsewhere.Origin = SampleOrigin.Elsewhere;
            elsewhere.Resolved = new ResolvedRef();
            elsewhere.Resolved.Status = RefStatus.Found;
            elsewhere.Resolved.ResolvedPath = @"C:\Downloads\kick.wav";
            elsewhere.RefIndexes.Add(1);
            elsewhere.Size = 200;

            Check(inProject.Name == elsewhere.Name, "collision probe: setup is broken, file names differ");

            // Порядок воспроизводит находку ревью: внешняя зависимость идёт в deps
            // раньше внутрипроектной с тем же именем файла.
            List<SampleDep> deps = new List<SampleDep>();
            deps.Add(elsewhere);
            deps.Add(inProject);

            CollectPlan plan = CollectAll.Plan(set, deps, new CollectOptions());

            string relIn, relElsewhere;
            bool haveIn = plan.Dest.TryGetValue(inProject, out relIn);
            bool haveElsewhere = plan.Dest.TryGetValue(elsewhere, out relElsewhere);
            Check(haveIn, "collision probe: no destination for the in-project dependency");
            Check(haveElsewhere, "collision probe: no destination for the elsewhere dependency");
            if (haveIn && haveElsewhere)
                Check(!string.Equals(relIn, relElsewhere, StringComparison.OrdinalIgnoreCase),
                      "collision probe: an elsewhere dependency landed on the same destination as an " +
                      "in-project dependency with the same file name (" + relIn + ")");
        }

        /// <summary>
        /// Открывает окно сборки и больше ничего не делает — чтобы его можно было снять
        /// Shot.exe, не сидя за машиной. Собранный exe, который «компилируется без
        /// ошибок», ещё ничего не говорит о том, что нарисовалось.
        /// </summary>
        static void Show(string file)
        {
            if (!File.Exists(file)) { Check(false, "no such set: " + file); return; }

            SetEntry set = new SetEntry();
            set.Path = file;
            set.Name = Path.GetFileNameWithoutExtension(file);

            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            using (CollectDialog d = new CollectDialog(set, LiveEnvironment.Detect(), Settings.Load()))
                System.Windows.Forms.Application.Run(d);
        }

        /// <summary>
        /// Сырая, не зависящая от AlsFile проверка обнуления LivePackId у тронутых узлов —
        /// того самого поля, которого нет в модели FileRefInfo. Распаковывает dst сама
        /// (GZipStream поверх FileStream, как AlsSamplePatch.Rewrite) и находит &lt;FileRef&gt;
        /// тем же правилом, что и патчер: открывающий тег, не самозакрывающийся, до
        /// &lt;/FileRef&gt;. У каждого узла, чей номер есть в rewrites, утверждает пустоту
        /// LivePackId; заодно тем же проходом по тексту сверяет LivePackName — дешёвая
        /// независимая от модели проверка того, что уже проверяется через AlsFile выше.
        /// Про узлы вне rewrites ничего не утверждает: их патчер не трогает, пак у них может
        /// быть любым. Возвращает число проверенных узлов.
        /// </summary>
        static int CheckPackCleared(string dst, Dictionary<int, NewRef> rewrites)
        {
            int index = -1, checkedNodes = 0;
            using (FileStream fin = new FileStream(dst, FileMode.Open, FileAccess.Read,
                                                   FileShare.ReadWrite, 64 * 1024))
            using (GZipStream gin = new GZipStream(fin, CompressionMode.Decompress))
            using (StreamReader rin = new StreamReader(gin, new UTF8Encoding(false), false, 64 * 1024))
            {
                AlsPatch.LineReader lines = new AlsPatch.LineReader(rin);
                StringBuilder node = null;

                string line;
                while ((line = lines.Next()) != null)
                {
                    if (node == null)
                    {
                        int at = line.IndexOf("<FileRef", StringComparison.Ordinal);
                        if (at < 0 || SelfClosing(line, at)) continue;
                        index++;
                        node = new StringBuilder(line);
                    }
                    else node.Append(line);

                    if (line.IndexOf("</FileRef>", StringComparison.Ordinal) < 0) continue;

                    string text = node.ToString();
                    node = null;
                    if (!rewrites.ContainsKey(index)) continue;

                    checkedNodes++;
                    Check(AttrEmpty(text, "<LivePackId Value=\""), "LivePackId not cleared (raw) at " + index);
                    Check(AttrEmpty(text, "<LivePackName Value=\""), "LivePackName not cleared (raw) at " + index);
                }
            }
            return checkedNodes;
        }

        /// <summary>Тег найден, и сразу после открывающей кавычки значения идёт закрывающая — значение пустое.</summary>
        static bool AttrEmpty(string node, string tag)
        {
            int at = node.IndexOf(tag, StringComparison.Ordinal);
            return at >= 0 && at + tag.Length < node.Length && node[at + tag.Length] == '"';
        }

        /// <summary>
        /// Стоит ли «/» перед закрывающей скобкой тега — то есть узел пустой. Копия того же
        /// правила из AlsSamplePatch: там оно приватно классу и общей сборки не разделяет.
        /// </summary>
        static bool SelfClosing(string line, int at)
        {
            int close = line.IndexOf('>', at);
            return close > at && line[close - 1] == '/';
        }
    }
}
