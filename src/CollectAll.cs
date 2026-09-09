using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace AbletonManager
{
    /// <summary>
    /// Что копировать при сборке. Те же четыре вопроса, что задаёт «Collect All and Save»
    /// самой Live. Файлы, уже лежащие в папке сета, не спрашиваются — Live о них тоже
    /// не спрашивает.
    /// </summary>
    public sealed class CollectOptions
    {
        public bool FromElsewhere = true;
        public bool FromOtherProjects = true;
        public bool FromUserLibrary = true;

        /// <summary>
        /// Единственный выключенный по умолчанию. Пак есть у любого, кто его купил, а
        /// весит он на порядок больше всего остального вместе взятого: включённым по
        /// умолчанию он превращает «собрать проект» в «скопировать полбиблиотеки» у того,
        /// кто нажал не глядя.
        /// </summary>
        public bool FromFactoryPacks;
    }

    /// <summary>Что именно будет сделано. Считается до показа галочек и пересчитывается на каждый щелчок.</summary>
    public sealed class CollectPlan
    {
        public string TargetDir = "";
        public readonly List<SampleDep> Copy = new List<SampleDep>();
        public readonly List<SampleDep> Skipped = new List<SampleDep>();
        public readonly List<SampleDep> NotFound = new List<SampleDep>();
        public long TotalBytes;
        public long FreeBytes;

        /// <summary>Что скопировать не вышло — занято, слишком длинный путь. Заполняет Run.</summary>
        public readonly List<string> Failed = new List<string>();

        /// <summary>Номер FileRef -> новый путь. Пусто у внутрипроектных: их путь верен как есть.</summary>
        public readonly Dictionary<int, NewRef> Rewrites = new Dictionary<int, NewRef>();

        /// <summary>Куда ляжет файл — относительно TargetDir, прямыми слэшами.</summary>
        public readonly Dictionary<SampleDep, string> Dest = new Dictionary<SampleDep, string>();
    }

    /// <summary>
    /// Сборка проекта в переносимую папку. Логика отдельно от окна — тем же делением,
    /// что RescueSession и RescueDialog.
    ///
    /// Оригинал не трогается: собранное кладётся в новую папку, исходный .als
    /// открывается только на чтение.
    /// </summary>
    public static class CollectAll
    {
        const string ImportedDir = "Samples/Imported";
        const string DevicesDir = "Devices";
        const string ProjectInfo = "Ableton Project Info";

        public static bool Wanted(SampleOrigin o, CollectOptions opt)
        {
            switch (o)
            {
                case SampleOrigin.InProject: return true;      // уже наш, копируется всегда
                case SampleOrigin.Elsewhere: return opt.FromElsewhere;
                case SampleOrigin.OtherProject: return opt.FromOtherProjects;
                case SampleOrigin.UserLibrary: return opt.FromUserLibrary;
                case SampleOrigin.FactoryPack: return opt.FromFactoryPacks;
                default: return false;
            }
        }

        public static CollectPlan Plan(SetEntry set, AlsInfo info, List<SampleDep> deps, CollectOptions opt)
        {
            CollectPlan plan = new CollectPlan();
            plan.TargetDir = FreeTarget(set);

            string setDir = Path.GetDirectoryName(set.Path);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (SampleDep d in deps)
            {
                if (d.Origin == SampleOrigin.Missing) { plan.NotFound.Add(d); continue; }
                if (!Wanted(d.Origin, opt)) { plan.Skipped.Add(d); continue; }

                string rel;
                if (d.Origin == SampleOrigin.InProject)
                {
                    // Структуру папки сета мы повторяем, поэтому путь в копии верен как
                    // есть — ни переименования, ни правки ссылки не нужно.
                    rel = Relative(setDir, d.Path);
                    if (rel.Length == 0) { plan.Skipped.Add(d); continue; }
                    taken.Add(rel);
                }
                else
                {
                    string folder = d.IsDevice ? DevicesDir : ImportedDir;
                    rel = Unique(taken, folder + "/" + d.Name);

                    NewRef nr = new NewRef();
                    nr.RelativePath = rel;
                    // AbsolutePath не заполняем: он производный от TargetDir, а тот ещё
                    // может смениться между планом и сборкой. Достроим его в Run.
                    nr.RelativePathType = 3;
                    nr.ClearPack = true;
                    foreach (int i in d.RefIndexes) plan.Rewrites[i] = nr;
                }

                plan.Dest[d] = rel;
                plan.Copy.Add(d);
                plan.TotalBytes += d.Size;
            }

            plan.FreeBytes = FreeSpace(plan.TargetDir);
            return plan;
        }

        public static void Run(CollectPlan plan, SetEntry set, AlsInfo info,
                               Action<int, int, string> progress, CancellationToken cancel)
        {
            bool ok = false;
            try
            {
                Directory.CreateDirectory(plan.TargetDir);
                CopyProjectInfo(set, plan.TargetDir);

                int done = 0, total = plan.Copy.Count;
                foreach (SampleDep d in plan.Copy)
                {
                    cancel.ThrowIfCancellationRequested();

                    string rel;
                    if (!plan.Dest.TryGetValue(d, out rel)) continue;
                    string dst = Path.Combine(plan.TargetDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst));
                        // .amxd и .adg бывают папками-бандлами (см. RefResolver.Probe) —
                        // File.Copy на каталоге бросает. Зависимость всё равно нужно
                        // перенести целиком, со всем, что внутри.
                        if (Directory.Exists(d.Path)) CopyDir(d.Path, dst);
                        else File.Copy(d.Path, dst, true);
                    }
                    catch (Exception ex)
                    {
                        // Занятый или слишком длинный путь — не повод бросать сборку:
                        // остальные файлы человеку нужны. Но и молчать нельзя — копия
                        // без сэмпла выглядит целой, а звучит не так.
                        plan.Failed.Add(d.Name);
                        Diag.Line("collect: cannot copy " + d.Path + ": " + ex.Message);
                    }

                    done++;
                    if (progress != null) progress(done, total, d.Name);
                }

                // Абсолютный путь достраиваем здесь, а не в Plan: TargetDir к этому
                // моменту окончателен, и производное значение не разъедется с ним.
                string root = plan.TargetDir.Replace('\\', '/');
                foreach (KeyValuePair<int, NewRef> kv in plan.Rewrites)
                    kv.Value.AbsolutePath = root + "/" + kv.Value.RelativePath;

                // .als пишется последним: прерванная сборка не должна оставить папку,
                // которая выглядит готовой.
                cancel.ThrowIfCancellationRequested();
                string als = Path.Combine(plan.TargetDir, set.Name + ".als");
                AlsSamplePatch.Rewrite(set.Path, als, plan.Rewrites, info.Files.Count);
                ok = true;
            }
            finally
            {
                // Папку создали мы в этот запуск, чужого в ней нет.
                if (!ok) { try { Directory.Delete(plan.TargetDir, true); } catch { } }
            }
        }

        /// <summary>
        /// Рекурсивная копия папки-бандла (.adg, .amxd) со всем содержимым. В отличие от
        /// SampleScan.DirSize здесь ошибку подпапки не глотаем: частично скопированный
        /// девайс хуже, чем явный отказ, который попадёт в plan.Failed через catch в Run.
        /// </summary>
        static void CopyDir(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            foreach (string f in Directory.GetFiles(src))
                File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            foreach (string d in Directory.GetDirectories(src))
                CopyDir(d, Path.Combine(dst, Path.GetFileName(d)));
        }

        // ------------------------------------------------------------------ пути

        /// <summary>
        /// «&lt;Проект&gt;\Collected\&lt;имя сета&gt; Project», а занято — со счётчиком.
        /// Суффикс « Project» не декоративный: по нему SetEntry.ComputeProjectDir опознаёт
        /// самостоятельный проект, и без него собранная копия схлопнулась бы в каталоге
        /// с исходником в одну строку.
        /// </summary>
        static string FreeTarget(SetEntry set)
        {
            string root = Path.Combine(set.ProjectDir, "Collected");
            for (int n = 1; n < 1000; n++)
            {
                string name = n == 1 ? set.Name + " Project"
                                     : set.Name + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + " Project";
                string dir = Path.Combine(root, name);
                if (!Directory.Exists(dir)) return dir;
            }
            return Path.Combine(root, set.Name + " " + DateTime.Now.Ticks + " Project");
        }

        /// <summary>Путь относительно корня, прямыми слэшами. Пусто, если путь не внутри корня.</summary>
        static string Relative(string root, string path)
        {
            try
            {
                string a = Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                string b = Path.GetFullPath(path);
                if (!b.StartsWith(a, StringComparison.OrdinalIgnoreCase)) return "";
                return b.Substring(a.Length).Replace('\\', '/');
            }
            catch { return ""; }
        }

        /// <summary>Развести совпавшие имена: «kick.wav», «kick 2.wav», «kick 3.wav».</summary>
        static string Unique(HashSet<string> taken, string rel)
        {
            if (taken.Add(rel)) return rel;

            string dir = "", name = rel;
            int slash = rel.LastIndexOf('/');
            if (slash >= 0) { dir = rel.Substring(0, slash + 1); name = rel.Substring(slash + 1); }

            string stem = name, ext = "";
            int dot = name.LastIndexOf('.');
            if (dot > 0) { stem = name.Substring(0, dot); ext = name.Substring(dot); }

            for (int n = 2; n < 100000; n++)
            {
                string candidate = dir + stem + " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture) + ext;
                if (taken.Add(candidate)) return candidate;
            }
            string last = dir + stem + " " + Guid.NewGuid().ToString("N") + ext;
            taken.Add(last);
            return last;
        }

        /// <summary>
        /// Live считает папку проектом по наличию «Ableton Project Info». Нет её у
        /// оригинала (сет лежит сам по себе) — заводим пустую: своё Live допишет туда
        /// при первом сохранении.
        /// </summary>
        static void CopyProjectInfo(SetEntry set, string targetDir)
        {
            string dst = Path.Combine(targetDir, ProjectInfo);
            Directory.CreateDirectory(dst);

            string src = Path.Combine(set.ProjectDir, ProjectInfo);
            if (!Directory.Exists(src)) return;
            try
            {
                foreach (string f in Directory.GetFiles(src))
                    File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), true);
            }
            catch (Exception ex) { Diag.Line("collect: project info: " + ex.Message); }
        }

        static long FreeSpace(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }
    }
}
