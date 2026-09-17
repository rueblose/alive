using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

        /// <summary>Сложить собранное в .zip рядом, а саму папку не оставлять.</summary>
        public bool ToZip;
    }

    /// <summary>Что именно будет сделано. Считается до показа галочек и пересчитывается на каждый щелчок.</summary>
    public sealed class CollectPlan
    {
        public string TargetDir = "";
        public bool Zip;

        /// <summary>Архив вместо папки — тем же именем, рядом. Занятое имя отсеивает FreeTarget.</summary>
        public string ZipPath { get { return TargetDir + ".zip"; } }

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

        public static CollectPlan Plan(SetEntry set, List<SampleDep> deps, CollectOptions opt)
        {
            CollectPlan plan = new CollectPlan();
            plan.TargetDir = FreeTarget(set);
            plan.Zip = opt.ToZip;

            string setDir = Path.GetDirectoryName(set.Path);
            HashSet<string> taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Первый проход — только застолбить имена внутрипроектных файлов. Раньше это
            // делалось в одном проходе вместе с раздачей имён внешним через Unique(), и
            // порядок в deps (это порядок FileRef в документе — его выбирает автор сета,
            // не мы) решал, кто первым займёт «Samples/Imported/kick.wav»: встреться
            // внешняя зависимость раньше одноимённой внутрипроектной, Unique() отдавал
            // путь ей, а внутрипроектная тем же путём застолбляла его же во второй раз —
            // обе оказывались с одним dst, и File.Copy в Run молча переписывал одну
            // другой. Засеяв taken целиком до первого вызова Unique(), результат больше
            // не зависит от того, кто раньше встретился в документе.
            foreach (SampleDep seed in deps)
            {
                if (seed.Origin != SampleOrigin.InProject) continue;
                string seedRel = Relative(setDir, seed.Path);
                if (seedRel.Length > 0) taken.Add(seedRel);
            }

            foreach (SampleDep d in deps)
            {
                if (d.Origin == SampleOrigin.Missing) { plan.NotFound.Add(d); continue; }
                if (!Wanted(d.Origin, opt)) { plan.Skipped.Add(d); continue; }

                string rel;
                if (d.Origin == SampleOrigin.InProject)
                {
                    // Структуру папки сета мы повторяем, поэтому путь в копии верен как
                    // есть — ни переименования, ни правки ссылки не нужно. taken уже
                    // засеян им первым проходом выше.
                    rel = Relative(setDir, d.Path);
                    if (rel.Length == 0) { plan.Skipped.Add(d); continue; }
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
            // CreateDirectory на уже существующей папке — тихий no-op, он не скажет,
            // создал он что-то или нет. Запоминаем сами, пока папки точно ещё нет:
            // Plan() у двух запусков по одному сету, посчитанные до того, как хоть
            // один создал папку на диске, может отдать один и тот же TargetDir.
            // В режиме архива папки не бывает вовсе — сносить в finally нечего.
            bool createdHere = !plan.Zip && !Directory.Exists(plan.TargetDir);
            Target target = null;
            try
            {
                target = plan.Zip ? (Target)new ZipTarget(plan.ZipPath) : new FolderTarget(plan.TargetDir);
                PutProjectInfo(set, target);

                int done = 0, total = plan.Copy.Count;
                foreach (SampleDep d in plan.Copy)
                {
                    cancel.ThrowIfCancellationRequested();

                    string rel;
                    if (!plan.Dest.TryGetValue(d, out rel)) continue;
                    try
                    {
                        // .amxd и .adg бывают папками-бандлами (см. RefResolver.Probe) —
                        // одним файлом их не взять. Зависимость всё равно нужно перенести
                        // целиком, со всем, что внутри.
                        if (Directory.Exists(d.Path)) target.PutDir(d.Path, rel);
                        else target.PutFile(d.Path, rel);
                    }
                    catch (Exception ex)
                    {
                        // Занятый или слишком длинный путь — не повод бросать сборку:
                        // остальные файлы человеку нужны. Но и молчать нельзя — копия
                        // без сэмпла выглядит целой, а звучит не так.
                        plan.Failed.Add(d.Name);
                        Diag.Line("collect: cannot copy " + d.Path + ": " + ex.Message);

                        // Не скопировался — ссылка обязана остаться как в оригинале, а не
                        // указывать на файл, которого в копии нет: та же спека, что и для
                        // «сэмпл не найден» — ссылка остаётся как была, число потерь идёт в отчёт.
                        foreach (int i in d.RefIndexes) plan.Rewrites.Remove(i);

                        // Бандл (.adg/.amxd — см. RefResolver.Probe) мог лечь наполовину:
                        // он переносится файл за файлом. Недокопированный бандл хуже
                        // отсутствующего — выглядит устройством, а половины пресета нет.
                        if (Directory.Exists(d.Path)) target.Undo(rel);
                    }

                    done++;
                    if (progress != null) progress(done, total, d.Name);
                }

                // Абсолютный путь достраиваем здесь, а не в Plan: TargetDir к этому
                // моменту окончателен, и производное значение не разъедется с ним.
                // В режиме архива папки TargetDir не существует, и путь верен всё равно:
                // архив зовётся её именем, и распаковка рядом с ним даёт ровно её.
                string root = plan.TargetDir.Replace('\\', '/');
                foreach (KeyValuePair<int, NewRef> kv in plan.Rewrites)
                    kv.Value.AbsolutePath = root + "/" + kv.Value.RelativePath;

                cancel.ThrowIfCancellationRequested();
                PutSet(plan, set, info, target);
                ok = true;
            }
            finally
            {
                // Архив закрывается раньше, чем finally соберётся его удалять.
                if (target != null) target.Dispose();
                // Удаляем только то, что создал этот запуск. Если TargetDir уже
                // существовал до Run — там может лежать чужая, уже собранная копия
                // (второй Plan() по тому же сету), и её мы не трогаем.
                if (!ok && createdHere) { try { Directory.Delete(plan.TargetDir, true); } catch { } }
                // Недописанный архив выглядит готовым экспортом, а внутри половина сета.
                if (!ok && plan.Zip) { try { File.Delete(plan.ZipPath); } catch { } }
            }
        }

        /// <summary>
        /// Live считает папку проектом по наличию «Ableton Project Info». Нет её у
        /// оригинала (сет лежит сам по себе) — заводим пустую: своё Live допишет туда
        /// при первом сохранении.
        /// </summary>
        static void PutProjectInfo(SetEntry set, Target target)
        {
            string src = Path.Combine(set.ProjectDir, ProjectInfo);
            string[] files = Directory.Exists(src) ? Directory.GetFiles(src) : new string[0];
            if (files.Length == 0) { target.PutEmptyDir(ProjectInfo); return; }

            try
            {
                foreach (string f in files)
                    target.PutFile(f, ProjectInfo + "/" + Path.GetFileName(f));
            }
            catch (Exception ex) { Diag.Line("collect: project info: " + ex.Message); }
        }

        /// <summary>
        /// .als кладётся последним: прерванная сборка не должна оставить экспорт, который
        /// выглядит готовым.
        ///
        /// AlsSamplePatch.Rewrite пишет в файл, поэтому для архива сет сперва собирается
        /// во временном и уходит в zip оттуда. Один файл на сет — не та заготовка, ради
        /// которой стоило бы разводить Rewrite ещё и на потоки.
        /// </summary>
        static void PutSet(CollectPlan plan, SetEntry set, AlsInfo info, Target target)
        {
            string rel = set.Name + ".als";
            if (!plan.Zip)
            {
                AlsSamplePatch.Rewrite(set.Path, Path.Combine(plan.TargetDir, rel),
                                       plan.Rewrites, info.Files.Count);
                return;
            }

            string tmp = Path.Combine(Path.GetTempPath(),
                                      "alive-" + Guid.NewGuid().ToString("N") + ".als");
            try
            {
                AlsSamplePatch.Rewrite(set.Path, tmp, plan.Rewrites, info.Files.Count);
                target.PutFile(tmp, rel);
            }
            finally { try { File.Delete(tmp); } catch { } }
        }

        // ------------------------------------------------------------------ куда кладём

        /// <summary>
        /// Куда ложится собранное: папкой на диске или сразу записями архива.
        ///
        /// Архив пишется потоком, а не приёмом «скопировать всё в папку, упаковать её,
        /// папку снести». Так вдвое меньше записи — гигабайтные паки проходят через диск
        /// один раз вместо двух, — и заодно не остаётся последовательности «собрать чужие
        /// файлы в кучу, упаковать, кучу стереть», на которую ругаются поведенческие
        /// эвристики антивирусов.
        /// </summary>
        abstract class Target : IDisposable
        {
            public abstract void PutFile(string src, string rel);
            public abstract void PutDir(string src, string rel);
            public abstract void PutEmptyDir(string rel);

            /// <summary>Убрать бандл, легший наполовину.</summary>
            public abstract void Undo(string rel);

            public virtual void Dispose() { }
        }

        sealed class FolderTarget : Target
        {
            readonly string _root;

            public FolderTarget(string root)
            {
                _root = root;
                Directory.CreateDirectory(root);
            }

            string Abs(string rel)
            {
                return Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
            }

            public override void PutFile(string src, string rel)
            {
                string dst = Abs(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }

            public override void PutDir(string src, string rel)
            {
                string dst = Abs(rel);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                CopyDir(src, dst);
            }

            public override void PutEmptyDir(string rel) { Directory.CreateDirectory(Abs(rel)); }

            public override void Undo(string rel)
            {
                try { Directory.Delete(Abs(rel), true); } catch { }
            }
        }

        sealed class ZipTarget : Target
        {
            readonly ZipArchive _zip;

            /// <summary>
            /// Fastest, а не Optimal: сэмплы — это wav и flac, жать их нечем, а разница во
            /// времени на гигабайтах заметна невооружённым глазом.
            /// </summary>
            const CompressionLevel Level = CompressionLevel.Fastest;

            public ZipTarget(string path) { _zip = ZipFile.Open(path, ZipArchiveMode.Create); }

            public override void PutFile(string src, string rel)
            {
                // Исходник может держать открытым антивирус или индексатор и отдавать
                // «занят другим процессом». Держат недолго, поэтому ждём и пробуем снова;
                // CreateEntryFromFile открывает файл ДО создания записи, так что повтор не
                // оставляет в архиве половинок.
                for (int attempt = 0; ; attempt++)
                {
                    try { _zip.CreateEntryFromFile(src, rel, Level); return; }
                    catch (Exception ex)
                    {
                        bool busy = ex is IOException || ex is UnauthorizedAccessException;
                        if (!busy || attempt == 5) throw;
                        Thread.Sleep(200);
                    }
                }
            }

            public override void PutDir(string src, string rel)
            {
                foreach (string f in Directory.GetFiles(src))
                    PutFile(f, rel + "/" + Path.GetFileName(f));
                foreach (string d in Directory.GetDirectories(src))
                    PutDir(d, rel + "/" + Path.GetFileName(d));
            }

            public override void PutEmptyDir(string rel) { _zip.CreateEntry(rel + "/"); }

            /// <summary>
            /// В архиве, открытом на запись, стирать нечего: ZipArchiveMode.Create пишет
            /// потоком и назад не ходит, а Update держал бы весь архив в памяти — на
            /// гигабайтных паках не вариант. Половина бандла так и останется внутри, но
            /// ссылку на него из .als уже сняли (см. catch в Run), поэтому для Live его
            /// там нет: лишние файлы в архиве, а не битое устройство в сете.
            /// </summary>
            public override void Undo(string rel) { }

            public override void Dispose() { _zip.Dispose(); }
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
        /// «&lt;Проект&gt;\&lt;имя сета&gt; Project_export», а занято — со счётчиком. Рядом с
        /// исходником, без промежуточной папки: экспорт ищут там же, где сам проект.
        /// </summary>
        static string FreeTarget(SetEntry set)
        {
            for (int n = 1; n < 1000; n++)
            {
                string name = set.Name + " Project_export"
                            + (n == 1 ? "" : " " + n.ToString(System.Globalization.CultureInfo.InvariantCulture));
                string dir = Path.Combine(set.ProjectDir, name);
                // Имя должно быть свободно и как папка, и как архив: в режиме .zip папка
                // после сборки удаляется, и следующий экспорт того же сета иначе нашёл бы
                // имя «свободным» и переписал прошлый архив.
                if (!Directory.Exists(dir) && !File.Exists(dir + ".zip")) return dir;
            }
            return Path.Combine(set.ProjectDir, set.Name + " Project_export " + DateTime.Now.Ticks);
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

        static long FreeSpace(string dir)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }
    }
}
