using System;
using System.Collections.Generic;
using System.IO;

namespace AbletonManager
{
    /// <summary>
    /// Откуда взялся медиафайл, на который ссылается сет. Категории — те же четыре, что
    /// в диалоге «Collect All and Save» самой Live, плюс «уже в проекте»: про эти Live
    /// не спрашивает, они и так на месте.
    /// </summary>
    public enum SampleOrigin
    {
        InProject,     // внутри папки самого сета — при сборке копируется всегда
        OtherProject,  // внутри чужой папки «* Project»
        UserLibrary,   // Documents\Ableton\User Library
        FactoryPack,   // установленный пак, Core Library или Builtin
        Elsewhere,     // просто где-то на диске
        Missing        // не нашли
    }

    /// <summary>
    /// Один медиафайл, нужный сету, — со всеми ссылками, которые на него ведут.
    ///
    /// Именно файл, а не ссылка: один сет ссылается на свою папку Samples сотнями
    /// клипов, и на реальной библиотеке 28 631 ссылка сводится примерно к 1100 разным
    /// файлам. Копировать и показывать надо файлы, а номера ссылок нужны потом
    /// патчеру — переписать придётся каждую.
    /// </summary>
    public sealed class SampleDep
    {
        public FileRefInfo Ref;            // одна из ссылок — из неё берутся путь и имя пака
        public ResolvedRef Resolved;       // где нашёлся
        public SampleOrigin Origin;
        public string PackName = "";       // заполнено у FactoryPack
        public long Size;                  // с диска; 0, если не нашли или не смогли посчитать
        public bool IsDevice;              // .amxd (MxPatchRef), а не сэмпл

        /// <summary>Номера FileRef в порядке документа — по ним адресует AlsSamplePatch.</summary>
        public readonly List<int> RefIndexes = new List<int>();

        public string Path
        {
            get { return Resolved != null ? Resolved.ResolvedPath : ""; }
        }

        public string Name
        {
            get
            {
                try { return System.IO.Path.GetFileName(Path); }
                catch { return ""; }
            }
        }
    }

    /// <summary>
    /// Какие медиафайлы нужны сету и откуда они. Ничего не читает с диска сверх того,
    /// что уже прочитал AlsFile, и ничего не пишет.
    /// </summary>
    public static class SampleScan
    {
        /// <summary>
        /// setDir — папка самого .als, а не папка проекта. Так же считает ProjectIndex.Build,
        /// и расходиться этим двум нельзя: иначе «в проекте» тут и «потеряно» в каталоге
        /// говорили бы о разных вещах.
        /// </summary>
        public static List<SampleDep> Of(AlsInfo info, string setDir, LiveEnvironment env)
        {
            List<SampleDep> list = new List<SampleDep>();
            if (info == null) return list;

            // Два уровня свёртки. Сначала по самой ссылке: одинаковые ссылки разрешаются
            // одинаково, и звать RefResolver 28 тысяч раз незачем. Потом по найденному
            // пути: разные ссылки (одна через пак, другая абсолютным путём) приводят к
            // одному файлу, а копировать его надо один раз.
            Dictionary<string, SampleDep> byRaw = new Dictionary<string, SampleDep>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, SampleDep> byFile = new Dictionary<string, SampleDep>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < info.Files.Count; i++)
            {
                FileRefInfo fr = info.Files[i];
                bool device = string.Equals(fr.Container, "MxPatchRef", StringComparison.Ordinal);
                if (!fr.IsSampleDependency && !device) continue;

                string raw = fr.RelativePathType.ToString(System.Globalization.CultureInfo.InvariantCulture)
                           + "|" + fr.RelativePath + "|" + fr.AbsolutePath + "|" + fr.LivePackName;

                SampleDep dep;
                if (byRaw.TryGetValue(raw, out dep))
                {
                    if (dep != null) dep.RefIndexes.Add(i);
                    continue;
                }

                ResolvedRef rr = RefResolver.Resolve(fr, setDir, env);
                if (rr.Status == RefStatus.Empty)
                {
                    byRaw[raw] = null;      // пустышка — помним, чтобы не разрешать снова
                    continue;
                }

                string fileKey = rr.Status == RefStatus.Found
                    ? rr.ResolvedPath
                    : "?" + (fr.RelativePath.Length > 0 ? fr.RelativePath : fr.AbsolutePath);

                if (byFile.TryGetValue(fileKey, out dep))
                {
                    dep.RefIndexes.Add(i);
                    byRaw[raw] = dep;
                    continue;
                }

                dep = new SampleDep();
                dep.Ref = fr;
                dep.Resolved = rr;
                dep.IsDevice = device;
                dep.RefIndexes.Add(i);
                dep.Origin = Classify(rr, fr, setDir, env, out dep.PackName);
                dep.Size = dep.Origin == SampleOrigin.Missing ? 0L : SizeOf(rr.ResolvedPath);

                byRaw[raw] = dep;
                byFile[fileKey] = dep;
                list.Add(dep);
            }

            return list;
        }

        /// <summary>
        /// Порядок проверок значим. «В проекте» идёт первым: проект, лежащий внутри
        /// User Library, — это всё равно свой проект, и его сэмплы не «из библиотеки».
        /// </summary>
        static SampleOrigin Classify(ResolvedRef rr, FileRefInfo fr, string setDir,
                                     LiveEnvironment env, out string packName)
        {
            packName = "";
            if (rr.Status != RefStatus.Found)
            {
                if (rr.Status == RefStatus.MissingPack) packName = fr.LivePackName ?? "";
                return SampleOrigin.Missing;
            }

            string p = rr.ResolvedPath;

            if (Under(p, setDir)) return SampleOrigin.InProject;

            if (fr.RelativePathType == 5 && fr.LivePackName.Length > 0)
            {
                packName = fr.LivePackName;
                return SampleOrigin.FactoryPack;
            }

            foreach (KeyValuePair<string, string> kv in env.Packs)
                if (Under(p, kv.Value)) { packName = kv.Key; return SampleOrigin.FactoryPack; }

            if (fr.RelativePathType == 7 || Under(p, env.Builtin) || Under(p, env.CoreLibrary))
            {
                packName = "Core Library";
                return SampleOrigin.FactoryPack;
            }

            if (fr.RelativePathType == 6 || Under(p, env.UserLibrary)) return SampleOrigin.UserLibrary;

            if (InSomeProject(p)) return SampleOrigin.OtherProject;

            return SampleOrigin.Elsewhere;
        }

        /// <summary>Лежит ли путь внутри корня. Сравнение по полным путям, иначе «…\Samples2» сошёлся бы за «…\Samples».</summary>
        static bool Under(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
            try
            {
                string a = System.IO.Path.GetFullPath(root).TrimEnd('\\', '/') + "\\";
                string b = System.IO.Path.GetFullPath(path);
                return b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>Тем же правилом, что SetEntry.ProjectDir: не более четырёх уровней вверх.</summary>
        static bool InSomeProject(string path)
        {
            try
            {
                DirectoryInfo d = new DirectoryInfo(System.IO.Path.GetDirectoryName(path));
                for (int i = 0; i < 4 && d != null; i++)
                {
                    if (d.Name.EndsWith(" Project", StringComparison.OrdinalIgnoreCase)) return true;
                    d = d.Parent;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// .adg и .amxd бывают папками (см. RefResolver.Probe) — их размер это сумма
        /// файлов внутри, а не 0. 0 у найденной (Origin != Missing) зависимости иначе
        /// неотличим от «не нашли», что противоречит комментарию у SampleDep.Size.
        /// </summary>
        static long SizeOf(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (fi.Exists) return fi.Length;
            }
            catch { return 0L; }

            if (!Directory.Exists(path)) return 0L;
            return DirSize(path);
        }

        /// <summary>
        /// Рекурсивная сумма размеров файлов в папке. Обход вручную, а не через
        /// Directory.GetFiles(path, "*", SearchOption.AllDirectories): тот бросает на
        /// первой недоступной подпапке и не отдаёт то, что уже насчитал. Здесь же
        /// недоступная подпапка обрывает счёт только для себя — берём максимум того,
        /// что смогли посчитать.
        /// </summary>
        static long DirSize(string dir) { return DirSize(dir, 0); }

        /// <summary>
        /// Предел глубины — тем же числом, что RenderIndex.Walk. Без него junction-цикл
        /// в файловой системе (а бандлы .adg/.amxd — обычные папки, никто не мешает
        /// смонтировать внутрь себя) раздувает сумму: обход не отличает повторный визит
        /// в ту же папку от новой, только глубину. Крах это не ловит — на реальном цикле
        /// его обрывает PathTooLongException внутри try выше, — но без предела число
        /// успевает завыситься в разы, пока путь не дорастёт до этой длины.
        /// </summary>
        static long DirSize(string dir, int depth)
        {
            long total = 0L;
            if (depth > 4) return total;

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch { return total; }
            foreach (string f in files)
            {
                try { total += new FileInfo(f).Length; }
                catch { }
            }

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch { return total; }
            foreach (string d in subdirs)
                total += DirSize(d, depth + 1);

            return total;
        }
    }
}
