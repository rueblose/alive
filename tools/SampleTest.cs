using System;
using System.Collections.Generic;
using System.IO;
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

        static int Main(string[] args)
        {
            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            string arg = args.Length > 1 ? args[1] : "";

            if (cmd == "scan") Scan(arg);
            else if (cmd == "patch") Patch(arg);
            else
            {
                Console.WriteLine("usage: SampleTest.exe scan <folder or .als>");
                Console.WriteLine("       SampleTest.exe patch <set.als>");
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
            // изменилось, и что нетронутое рядом с ним уцелело.
            Dictionary<int, NewRef> rewrites = new Dictionary<int, NewRef>();
            HashSet<int> touched = new HashSet<int>();
            int n = 0;
            foreach (SampleDep d in deps)
            {
                if ((n++ % 2) != 0) continue;
                NewRef nr = new NewRef();
                nr.RelativePath = "Samples/Imported/probe " + n + ".wav";
                nr.AbsolutePath = "C:/probe/Samples/Imported/probe " + n + ".wav";
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
        }
    }
}
