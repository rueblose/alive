# Core сэмплов и Collect All — план реализации

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** дать Alive модель зависимостей сета по сэмплам, патчер путей в копии `.als`, и кнопку Collect All в инспекторе, собирающую проект в переносимую папку.

**Architecture:** две новые библиотечные единицы без UI (`SampleScan` — что за файлы нужны сету и откуда они; `AlsSamplePatch` — копия `.als` с переписанными путями) и один потребитель поверх них (`CollectAll` + `CollectDialog`). Всё строится на уже написанных `AlsFile`, `RefResolver`, `LiveEnvironment` и повторяет приём `AlsPatch`: потоковое чтение gzip, правка отдельных `Value` в накопленном узле, запись всегда в новый файл. Оригинальный `.als` не открывается на запись ни на одном пути кода.

**Tech Stack:** C# для .NET Framework 4.x, компилятор `csc.exe` из `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319`. WinForms, всё рисование своё (`GlassDialog`, `GlassButton`, `PillToggle`). Сборка приложения — `build.cmd`.

**Spec:** [docs/superpowers/specs/2026-09-09-samples-core-collect-all-design.md](../specs/2026-09-09-samples-core-collect-all-design.md)

## Global Constraints

- **Язык C# 5.** `csc.exe` из .NET Framework 4.x. Нельзя: интерполяция строк (`$"..."`), `nameof`, expression-bodied members, `?.`, автосвойства с инициализатором. Форматирование — `string.Format`, числа — с `CultureInfo.InvariantCulture`.
- **Комментарии и документация — по-русски**, интерфейс приложения — по-английски. Так во всём репозитории.
- **Никаких новых зависимостей.** Только `System`, `System.Core`, `System.Drawing`, `System.Windows.Forms`, `System.Xml`.
- **Оригинальный `.als` не открывается на запись никогда.** Все правки пишутся в новый файл.
- **Пути в `.als` — прямые слэши** (`/`) и в `RelativePath`, и в `Path`, независимо от того, что это Windows.
- **Значения в `.als` экранируются по XML.** При записи кодировать `&`, `<`, `>`, `"`.
- **Разрешение ссылок отсчитывается от папки самого `.als`**, а не от `SetEntry.ProjectDir`. Именно так делает `ProjectIndex.Build` (`string dir = Path.GetDirectoryName(file)`), и расхождение здесь развело бы классификацию с уже показанным в каталоге счётчиком `MissingFiles`.
- **Файлы кода — в `src/`**, консольные стенды — в `tools/` и в `bin` не попадают.
- Каждый коммит заканчивается строкой `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

## Замечание о проверках

В проекте нет фреймворка тестов: он собирается `csc.exe` напрямую, а проверяется консольными стендами в `tools/` (`RescueTest.cs`). План следует этому же укладу.

«Красный» в этом укладе — это **ошибка компиляции** стенда (класса ещё нет) или **непройденное утверждение** в нём. Стенд печатает `FAIL: …` и возвращает код выхода 1; в конце — `OK: N checks passed`.

**Существующий `tools/build-rescue-test.cmd` сейчас сломан** — он собирает `DialogShow.cs`, а тот ссылается на удалённый `src/OptionsDialog.cs`. Чинить его в этом плане не надо: Задача 1 заводит отдельный `tools/build-sample-test.cmd`, ни от чего постороннего не зависящий.

## Структура файлов

| файл | ответственность |
|---|---|
| `src/SampleScan.cs` | **создать.** `SampleOrigin`, `SampleDep`, `SampleScan.Of` — какие медиафайлы нужны сету, где они и к какой из категорий Live относятся |
| `src/AlsSamplePatch.cs` | **создать.** `NewRef`, `AlsSamplePatch.Rewrite` — копия `.als` с заменёнными путями у выбранных `FileRef` |
| `src/CollectAll.cs` | **создать.** `CollectOptions`, `CollectPlan`, `CollectAll.Plan`/`Run` — что и куда копируем, и само копирование |
| `src/CollectDialog.cs` | **создать.** Окно с четырьмя галочками Live и полосой прогресса |
| `src/AlsPatch.cs` | **правка.** `LineReader` становится `internal`, чтобы патчер сэмплов не заводил вторую копию |
| `src/Settings.cs` | **правка.** Четыре флага галочек, строкой в `settings.cfg` |
| `src/DetailPanel.cs` | **правка.** Кнопка `Collect All` и событие `CollectRequested` |
| `src/MainForm.cs` | **правка.** Подписка на событие, открытие окна, тост по завершении |
| `tools/SampleTest.cs` | **создать.** Консольный стенд: `scan`, `patch`, `collect`, `show` |
| `tools/build-sample-test.cmd` | **создать.** Сборка стенда и `Shot.exe` |

`FolderScan.cs` не трогается: по решению из спеки собранная копия остаётся видимой в каталоге.

---

### Task 1: `SampleScan` — зависимости сета и их происхождение

**Files:**
- Create: `src/SampleScan.cs`
- Create: `tools/SampleTest.cs`
- Create: `tools/build-sample-test.cmd`

**Interfaces:**
- Consumes: `AlsInfo`, `FileRefInfo` (`src/AlsFile.cs`), `RefResolver.Resolve`, `ResolvedRef`, `RefStatus` (`src/RefResolver.cs`), `LiveEnvironment` (`src/LiveEnvironment.cs`)
- Produces:
  - `enum SampleOrigin { InProject, OtherProject, UserLibrary, FactoryPack, Elsewhere, Missing }`
  - `sealed class SampleDep` с полями `Ref` (`FileRefInfo`), `Resolved` (`ResolvedRef`), `Origin` (`SampleOrigin`), `PackName` (`string`), `Size` (`long`), `IsDevice` (`bool`), `RefIndexes` (`List<int>`), и свойствами `Path` (`string`), `Name` (`string`)
  - `static List<SampleDep> SampleScan.Of(AlsInfo info, string setDir, LiveEnvironment env)`

- [ ] **Step 1: Написать стенд с падающей проверкой**

Создать `tools/SampleTest.cs`:

```csharp
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
            else
            {
                Console.WriteLine("usage: SampleTest.exe scan <folder or .als>");
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
            int devices = 0, totalRefs = 0, totalDeps = 0;

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
            Console.WriteLine();
            string[] names = { "InProject", "OtherProject", "UserLibrary", "FactoryPack", "Elsewhere", "Missing" };
            for (int i = 0; i < names.Length; i++)
                Console.WriteLine(string.Format("{0,-14} {1,6} files  {2,10:N1} MB",
                                                names[i], byOrigin[i], bytesByOrigin[i] / 1048576.0));
        }
    }
}
```

Создать `tools/build-sample-test.cmd`:

```bat
@echo off
setlocal
rem Console harness for the sample model. Not shipped - this checks the thing,
rem it is not part of the thing.
rem
rem   build-sample-test.cmd [output folder]     default: %TEMP%\alive-sample-test
rem
rem ASCII only: cmd.exe reads batch files in the OEM codepage.

set OUT=%~1
if "%OUT%"=="" set OUT=%TEMP%\alive-sample-test
if not exist "%OUT%" mkdir "%OUT%"

set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo csc.exe from .NET Framework 4.x was not found.
  exit /b 1
)

set ROOT=%~dp0..
set REFS=/reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll

rem Whole src except Program.cs - SampleTest brings its own Main.
set LIST=%TEMP%\alive-sample-sources.rsp
if exist "%LIST%" del "%LIST%"
for /f "delims=" %%F in ('dir /b "%ROOT%\src\*.cs" ^| findstr /v /i /x "Program.cs"') do (
  echo "%ROOT%\src\%%F">>"%LIST%"
)

"%CSC%" /nologo /target:exe /platform:anycpu /codepage:65001 /main:AliveTools.SampleTest ^
  /out:"%OUT%\SampleTest.exe" %REFS% @"%LIST%" "%ROOT%\tools\SampleTest.cs"
if errorlevel 1 goto fail

echo OK: %OUT%\SampleTest.exe
endlocal
exit /b 0

:fail
echo.
echo BUILD FAILED
exit /b 1
```

- [ ] **Step 2: Собрать и убедиться, что не собирается**

```bash
tools/build-sample-test.cmd
```

Ожидается: `BUILD FAILED` с ошибками вида `error CS0246: The type or namespace name 'SampleScan' could not be found`. Это и есть «красный» в этом проекте — класса ещё нет.

- [ ] **Step 3: Написать `src/SampleScan.cs`**

```csharp
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
        public long Size;                  // с диска; 0, если не нашли
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

        static long SizeOf(string path)
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                return fi.Exists ? fi.Length : 0L;
            }
            catch { return 0L; }
        }
    }
}
```

- [ ] **Step 4: Собрать и прогнать стенд**

```bash
tools/build-sample-test.cmd && "$TEMP/alive-sample-test/SampleTest.exe" scan "E:\Music\Ableton Projects\Series 2"
```

Ожидается: `OK: N checks passed` и разбивка по шести категориям. Проверить глазами, что `FactoryPack` непустой (в библиотеке есть паки) и что `distinct files` на порядок меньше `refs` — если они равны, свёртка не работает.

- [ ] **Step 5: Коммит**

```bash
git add src/SampleScan.cs tools/SampleTest.cs tools/build-sample-test.cmd
git commit -m "Сэмплы: модель зависимостей сета

SampleScan отвечает на вопрос «какие медиафайлы нужны сету и откуда они»:
берёт SampleRef и MxPatchRef, разрешает их существующим RefResolver и
раскладывает по четырём категориям диалога Live плюс «уже в проекте».

Свёртка двухуровневая. По самой ссылке — потому что одинаковые ссылки
разрешаются одинаково, а их в сете десятки тысяч. По найденному пути —
потому что разные ссылки приводят к одному файлу, а копировать его надо
один раз.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: `AlsSamplePatch` — копия сета с переписанными путями

**Files:**
- Create: `src/AlsSamplePatch.cs`
- Modify: `src/AlsPatch.cs` — `sealed class LineReader` → `internal sealed class LineReader`
- Modify: `tools/SampleTest.cs` — команда `patch`

**Interfaces:**
- Consumes: `AlsPatch.LineReader` (после правки видимости), `AlsFile.Read`, `SampleScan.Of`, `SampleDep.RefIndexes`
- Produces:
  - `sealed class NewRef` с полями `RelativePath` (`string`), `AbsolutePath` (`string`), `RelativePathType` (`int`, по умолчанию 3), `ClearPack` (`bool`, по умолчанию `true`)
  - `static int AlsSamplePatch.Rewrite(string src, string dst, Dictionary<int, NewRef> byIndex, int expectedRefCount)`

- [ ] **Step 1: Написать падающую проверку**

В `tools/SampleTest.cs` добавить ветку в `Main`:

```csharp
            else if (cmd == "patch") Patch(arg);
```

и строку в usage:

```csharp
                Console.WriteLine("       SampleTest.exe patch <set.als>");
```

и сам метод:

```csharp
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
```

- [ ] **Step 2: Собрать и убедиться, что не собирается**

```bash
tools/build-sample-test.cmd
```

Ожидается: `BUILD FAILED`, `error CS0246: … 'NewRef' …` и `… 'AlsSamplePatch' …`.

- [ ] **Step 3: Открыть `LineReader` для второго патчера**

В `src/AlsPatch.cs` заменить одну строку:

```csharp
        sealed class LineReader
```

на

```csharp
        /// <summary>Внутренний, а не приватный: тем же чтением пользуется AlsSamplePatch.</summary>
        internal sealed class LineReader
```

- [ ] **Step 4: Написать `src/AlsSamplePatch.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace AbletonManager
{
    /// <summary>Куда должна начать указывать одна ссылка на файл.</summary>
    public sealed class NewRef
    {
        /// <summary>«Samples/Imported/kick.wav» — прямыми слэшами, как пишет сама Live.</summary>
        public string RelativePath = "";

        /// <summary>Полный путь в новом месте, тоже прямыми слэшами.</summary>
        public string AbsolutePath = "";

        /// <summary>3 — «внутри папки проекта», см. RefResolver.</summary>
        public int RelativePathType = 3;

        /// <summary>Обнулить LivePackName и LivePackId: файл больше не из пака.</summary>
        public bool ClearPack = true;
    }

    /// <summary>
    /// Копия сета, в которой у выбранных ссылок заменены пути.
    ///
    /// Узлы адресуются НОМЕРОМ в порядке документа, а не содержимым. В тексте
    /// &lt;FileRef&gt; не отличить от соседнего: путь у сэмпла клипа и у «памяти о
    /// происхождении» бывает буквально один и тот же, а переписать надо только первый.
    /// AlsFile уже обходит файл и складывает info.Files по порядку, значит i-я запись
    /// в списке — это i-й &lt;FileRef&gt; в тексте. Так логика «какой это контейнер»
    /// живёт в одном месте: разъехавшись, два разбора переписали бы не ту ссылку,
    /// которую показали.
    ///
    /// Механика — та же, что у AlsPatch: потоком, строка за строкой, в памяти только
    /// текущий узел. Оригинал не открывается на запись.
    /// </summary>
    public static class AlsSamplePatch
    {
        /// <summary>
        /// Пишет в dst копию src с заменёнными путями. Возвращает число тронутых узлов.
        ///
        /// expectedRefCount — сколько FileRef насчитал AlsFile в этом же файле. Не сошлось
        /// значит нумерация разъехалась и правка попала бы не в ту ссылку: dst удаляется,
        /// бросается InvalidDataException. Записать неверный путь в ссылку на сэмпл хуже,
        /// чем не записать ничего — сет откроется, но зазвучит не тем.
        /// </summary>
        public static int Rewrite(string src, string dst, Dictionary<int, NewRef> byIndex,
                                  int expectedRefCount)
        {
            if (byIndex == null) throw new ArgumentNullException("byIndex");

            int index = -1, patched = 0;
            bool ok = false;
            try
            {
                using (FileStream fin = new FileStream(src, FileMode.Open, FileAccess.Read,
                                                       FileShare.ReadWrite, 64 * 1024))
                using (GZipStream gin = new GZipStream(fin, CompressionMode.Decompress))
                // detectEncodingFromByteOrderMarks: false — иначе BOM был бы съеден на
                // чтении и не записан обратно, а копия обязана отличаться от оригинала
                // ровно теми значениями, которые мы меняем, и ничем больше.
                using (StreamReader rin = new StreamReader(gin, new UTF8Encoding(false), false, 64 * 1024))

                using (FileStream fout = new FileStream(dst, FileMode.Create, FileAccess.Write,
                                                        FileShare.None, 64 * 1024))
                using (GZipStream gout = new GZipStream(fout, CompressionMode.Compress))
                using (StreamWriter wout = new StreamWriter(gout, new UTF8Encoding(false), 64 * 1024))
                {
                    AlsPatch.LineReader lines = new AlsPatch.LineReader(rin);
                    StringBuilder node = null;

                    string line;
                    while ((line = lines.Next()) != null)
                    {
                        if (node == null)
                        {
                            int at = line.IndexOf("<FileRef", StringComparison.Ordinal);
                            // Самозакрывающийся <FileRef /> пропускают оба разбора: AlsFile
                            // заводит запись только при !IsEmptyElement.
                            if (at < 0 || SelfClosing(line, at)) { wout.Write(line); continue; }
                            index++;
                            node = new StringBuilder(line);
                        }
                        else node.Append(line);

                        // Закрывающий тег ищем в текущей строке, а не во всём накопленном:
                        // иначе на каждую строку узла пересобиралась бы вся его строка целиком.
                        if (line.IndexOf("</FileRef>", StringComparison.Ordinal) < 0) continue;

                        string text = node.ToString();
                        node = null;

                        NewRef nr;
                        if (byIndex.TryGetValue(index, out nr)) { wout.Write(Apply(text, nr)); patched++; }
                        else wout.Write(text);
                    }

                    // Файл оборвался посреди узла — пишем как есть, чтобы не потерять хвост.
                    if (node != null) wout.Write(node.ToString());
                }

                int count = index + 1;
                if (count != expectedRefCount)
                    throw new InvalidDataException(string.Format(
                        "FileRef count mismatch: found {0}, expected {1}", count, expectedRefCount));

                ok = true;
            }
            finally
            {
                if (!ok) { try { File.Delete(dst); } catch { } }
            }

            Diag.Line("collect: rewrote " + patched + " FileRef in " + Path.GetFileName(dst));
            return patched;
        }

        /// <summary>Стоит ли «/» перед закрывающей скобкой тега — то есть узел пустой.</summary>
        static bool SelfClosing(string line, int at)
        {
            int close = line.IndexOf('>', at);
            return close > at && line[close - 1] == '/';
        }

        static string Apply(string node, NewRef nr)
        {
            string s = node;
            // Ведущий «<» тут не украшение: без него «<RelativePath Value="» нашлось бы
            // по запросу «Path Value="» и путь уехал бы не в тот тег.
            s = SetValue(s, "<RelativePathType Value=\"",
                         nr.RelativePathType.ToString(CultureInfo.InvariantCulture));
            s = SetValue(s, "<RelativePath Value=\"", Escape(nr.RelativePath));
            s = SetValue(s, "<Path Value=\"", Escape(nr.AbsolutePath));
            if (nr.ClearPack)
            {
                s = SetValue(s, "<LivePackName Value=\"", "");
                s = SetValue(s, "<LivePackId Value=\"", "");
            }
            // Type, OriginalFileSize и OriginalCrc не трогаем: они про тот же самый файл,
            // он просто переехал.
            return s;
        }

        /// <summary>Заменить значение первого такого тега в узле. Нет тега — узел как был.</summary>
        static string SetValue(string node, string tag, string value)
        {
            int at = node.IndexOf(tag, StringComparison.Ordinal);
            if (at < 0) return node;
            int from = at + tag.Length;
            int close = node.IndexOf('"', from);
            if (close < 0) return node;

            StringBuilder sb = new StringBuilder(node.Length + value.Length + 8);
            sb.Append(node, 0, from).Append(value).Append(node, close, node.Length - close);
            return sb.ToString();
        }

        /// <summary>
        /// XML-экранирование значения атрибута. Не формальность: папка «Drum &amp; Bass»
        /// встречается сплошь и рядом, и записанная как есть она рвёт документ.
        /// </summary>
        static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder(s.Length + 16);
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '&') sb.Append("&amp;");
                else if (c == '<') sb.Append("&lt;");
                else if (c == '>') sb.Append("&gt;");
                else if (c == '"') sb.Append("&quot;");
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
```

- [ ] **Step 5: Собрать и прогнать круг**

```bash
tools/build-sample-test.cmd && "$TEMP/alive-sample-test/SampleTest.exe" patch "E:\Music\Ableton Projects\Series 2\somnitelno\flush Project\flush.als"
```

Ожидается `OK: N checks passed`. Затем прогнать `scan`, чтобы убедиться, что правка видимости `LineReader` ничего не сломала:

```bash
"$TEMP/alive-sample-test/SampleTest.exe" scan "E:\Music\Ableton Projects\Series 2"
```

- [ ] **Step 6: Проверить, что приложение по-прежнему собирается**

```bash
build.cmd
```

Ожидается `OK: …\bin\Alive.exe` и `OK: …\bin\AliveReel.exe`.

- [ ] **Step 7: Коммит**

```bash
git add src/AlsSamplePatch.cs src/AlsPatch.cs tools/SampleTest.cs
git commit -m "Сэмплы: патчер путей в копии сета

AlsSamplePatch пишет копию .als с заменёнными путями у выбранных FileRef.
Узлы адресуются номером в порядке документа: в тексте FileRef сэмпла не
отличить от FileRef «памяти о происхождении», путь у них бывает один и
тот же, а переписать надо только первый. Нумерацию задаёт AlsFile, и она
же сверяется в конце — не сошлось, файл удаляется и правка не
применяется.

Пути пишутся прямыми слэшами и экранируются по XML: папка «Drum & Bass»
без этого рвёт документ.

LineReader у AlsPatch стал internal — второе такое чтение заводить незачем.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: `CollectAll` — план сборки и копирование

**Files:**
- Create: `src/CollectAll.cs`
- Modify: `tools/SampleTest.cs` — команда `collect`

**Interfaces:**
- Consumes: `SampleScan.Of`, `SampleDep`, `SampleOrigin`, `AlsSamplePatch.Rewrite`, `NewRef`, `SetEntry.ProjectDir` / `.Path` / `.Name`
- Produces:
  - `sealed class CollectOptions` с `FromElsewhere`, `FromOtherProjects`, `FromUserLibrary` (`bool`, по умолчанию `true`) и `FromFactoryPacks` (`bool`, по умолчанию `false`)
  - `sealed class CollectPlan` с `TargetDir` (`string`), `Copy` / `Skipped` / `NotFound` (`List<SampleDep>`), `TotalBytes` / `FreeBytes` (`long`), `Rewrites` (`Dictionary<int, NewRef>`), `Dest` (`Dictionary<SampleDep, string>` — относительный путь внутри `TargetDir`, прямыми слэшами)
  - `static CollectPlan CollectAll.Plan(SetEntry set, AlsInfo info, List<SampleDep> deps, CollectOptions opt)`
  - `static void CollectAll.Run(CollectPlan plan, SetEntry set, AlsInfo info, Action<int,int,string> progress, System.Threading.CancellationToken cancel)`
  - `static bool CollectAll.Wanted(SampleOrigin o, CollectOptions opt)`

- [ ] **Step 1: Написать падающую проверку**

В `tools/SampleTest.cs` добавить ветку в `Main`:

```csharp
            else if (cmd == "collect") Collect(arg);
```

строку в usage:

```csharp
                Console.WriteLine("       SampleTest.exe collect <set.als>");
```

и метод:

```csharp
        /// <summary>
        /// Собирает сет во временную папку и проверяет главное обещание сборки: каждая
        /// ссылка собранной копии разрешается в существующий файл ВНУТРИ этой папки.
        /// Ради этого сборка и делается, и проверять это надо тем же RefResolver,
        /// которым потом будет пользоваться каталог.
        /// </summary>
        static void Collect(string file)
        {
            if (!File.Exists(file)) { Check(false, "no such set: " + file); return; }

            LiveEnvironment env = LiveEnvironment.Detect();
            AlsInfo info = AlsFile.Read(file);
            Check(info.Error == null, "cannot read " + file);
            if (info.Error != null) return;

            SetEntry set = new SetEntry();
            set.Path = file;
            set.Name = Path.GetFileNameWithoutExtension(file);

            List<SampleDep> deps = SampleScan.Of(info, Path.GetDirectoryName(file), env);

            CollectOptions opt = new CollectOptions();
            opt.FromFactoryPacks = true;    // в стенде собираем всё, чтобы проверить все ветки

            CollectPlan plan = CollectAll.Plan(set, info, deps, opt);

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

            int done = 0;
            CollectAll.Run(plan, set, info,
                delegate (int n, int of, string what) { done = n; },
                System.Threading.CancellationToken.None);

            Check(done == plan.Copy.Count, string.Format("copied {0} of {1}", done, plan.Copy.Count));

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
            try { Directory.Delete(temp, true); } catch { }
        }
```

- [ ] **Step 2: Собрать и убедиться, что не собирается**

```bash
tools/build-sample-test.cmd
```

Ожидается: `BUILD FAILED`, `error CS0246: … 'CollectOptions' …`, `… 'CollectPlan' …`, `… 'CollectAll' …`.

- [ ] **Step 3: Написать `src/CollectAll.cs`**

```csharp
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
                        File.Copy(d.Path, dst, true);
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
```

- [ ] **Step 4: Собрать и прогнать сборку**

```bash
tools/build-sample-test.cmd && "$TEMP/alive-sample-test/SampleTest.exe" collect "E:\Music\Ableton Projects\Series 2\somnitelno\flush Project\flush.als"
```

Ожидается `OK: N checks passed` и строка `collected N files, X MB -> …`. Главная проверка внутри — что после сборки ни одна ссылка не указывает наружу собранной папки.

- [ ] **Step 5: Коммит**

```bash
git add src/CollectAll.cs tools/SampleTest.cs
git commit -m "Сэмплы: сборка проекта в переносимую папку

CollectAll раскладывает зависимости на «копируем», «пропущено галочкой» и
«не нашли», считает вес и свободное место, и копирует.

Внутрипроектные ссылки не переписываются вообще: структуру папки сета
копия повторяет, и путь Samples/Processed/x.wav в ней верен как есть.
Переписываются только внешние — в Samples/Imported с типом 3.

.als пишется последним, чтобы прерванная сборка не оставила папку,
которая выглядит готовой; при обрыве папка удаляется целиком — её создал
этот запуск, чужого в ней нет.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: `CollectDialog` — окно с галочками и прогрессом

**Files:**
- Create: `src/CollectDialog.cs`
- Modify: `src/Settings.cs` — четыре флага
- Modify: `tools/SampleTest.cs` — команда `show`

**Interfaces:**
- Consumes: `GlassDialog`, `GlassButton`, `PillToggle` (`src/Controls.cs`), `Chrome.DrawText`, `Theme`, `CollectOptions`, `CollectPlan`, `CollectAll`, `SampleScan`, `AlsFile`
- Produces:
  - `sealed class CollectDialog : GlassDialog` с конструктором `CollectDialog(SetEntry set, LiveEnvironment env)` и полем `public string Produced` (папка собранного проекта; пусто, если отменили)
  - `Settings.CollectElsewhere`, `Settings.CollectOtherProjects`, `Settings.CollectUserLibrary`, `Settings.CollectFactoryPacks` (`bool`)

- [ ] **Step 1: Добавить флаги в `Settings`**

В `src/Settings.cs`, после блока плагинов, добавить:

```csharp
        // ------------------------------------------------------------- сборка проекта

        /// <summary>
        /// Галочки диалога Collect All — те же четыре, что у «Collect All and Save» в Live.
        /// Паки выключены по умолчанию: они весят на порядок больше всего остального, а
        /// есть у любого, кто их купил.
        /// </summary>
        public bool CollectElsewhere = true;
        public bool CollectOtherProjects = true;
        public bool CollectUserLibrary = true;
        public bool CollectFactoryPacks;
```

В разборе (рядом с `else if (key == "groupbyfolder") …`):

```csharp
                    else if (key == "collectelsewhere") s.CollectElsewhere = val == "1";
                    else if (key == "collectotherprojects") s.CollectOtherProjects = val == "1";
                    else if (key == "collectuserlibrary") s.CollectUserLibrary = val == "1";
                    else if (key == "collectfactorypacks") s.CollectFactoryPacks = val == "1";
```

В записи (рядом с `sb.Append("groupbyfolder=")…`):

```csharp
                sb.Append("collectelsewhere=").AppendLine(CollectElsewhere ? "1" : "0");
                sb.Append("collectotherprojects=").AppendLine(CollectOtherProjects ? "1" : "0");
                sb.Append("collectuserlibrary=").AppendLine(CollectUserLibrary ? "1" : "0");
                sb.Append("collectfactorypacks=").AppendLine(CollectFactoryPacks ? "1" : "0");
```

- [ ] **Step 2: Написать стенд показа окна**

В `tools/SampleTest.cs` добавить ветку в `Main`:

```csharp
            else if (cmd == "show") Show(arg);
```

строку в usage:

```csharp
                Console.WriteLine("       SampleTest.exe show <set.als>");
```

и метод:

```csharp
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
            using (CollectDialog d = new CollectDialog(set, LiveEnvironment.Detect()))
                System.Windows.Forms.Application.Run(d);
        }
```

Так как `Show` запускает цикл сообщений, `Main` для этой команды должен быть помечен `[STAThread]` — атрибут ставится на `Main` целиком:

```csharp
        [STAThread]
        static int Main(string[] args)
```

- [ ] **Step 3: Собрать и убедиться, что не собирается**

```bash
tools/build-sample-test.cmd
```

Ожидается: `BUILD FAILED`, `error CS0246: … 'CollectDialog' …`.

- [ ] **Step 4: Написать `src/CollectDialog.cs`**

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Сборка проекта в переносимую папку. Одно окно, два состояния: выбор и копирование, —
    /// тем же приёмом, что RescueDialog, и по той же причине: это один поступок, а не два
    /// разных дела, и второе окно поверх первого только мешало бы.
    ///
    /// Пункты — ровно те четыре, что задаёт «Collect All and Save» самой Live. Сверх неё
    /// показаны число файлов и вес: 5.8 ГБ у паков надо видеть ДО нажатия OK, а не после.
    /// </summary>
    public sealed class CollectDialog : GlassDialog
    {
        sealed class Row
        {
            public string Label = "";
            public SampleOrigin Origin;
            public PillToggle Toggle;
            public int Files;
            public long Bytes;
            public Rectangle Rect;
        }

        readonly SetEntry _set;
        readonly LiveEnvironment _env;
        readonly List<Row> _rows = new List<Row>();
        readonly GlassButton _ok = new GlassButton();
        readonly GlassButton _cancel = new GlassButton();
        readonly Timer _tick = new Timer();

        AlsInfo _info;
        List<SampleDep> _deps;
        CollectPlan _plan;
        CollectOptions _opt = new CollectOptions();

        int _inProjectFiles; long _inProjectBytes;
        int _notFound;

        bool _counting = true;
        bool _running;
        string _error = "";

        // Прогресс пишется рабочим потоком, читается таймером окна. Простые поля:
        // int и long читаются и пишутся атомарно, а точность до одного файла тут
        // никому не нужна — это полоса, а не отчёт.
        volatile int _done, _total;
        volatile string _current = "";
        CancellationTokenSource _cancel;

        /// <summary>Папка собранного проекта. Пусто, если отменили или не дошло до сборки.</summary>
        public string Produced = "";

        /// <summary>Сколько файлов скопировать не вышло — занято, слишком длинный путь.</summary>
        public int Failed;

        public CollectDialog(SetEntry set, LiveEnvironment env)
        {
            _set = set;
            _env = env;

            Caption = "Collect All: " + set.Name;
            ClientSize = new Size(Sc(640), Sc(400));

            Settings st = Settings.Load();
            _opt.FromElsewhere = st.CollectElsewhere;
            _opt.FromOtherProjects = st.CollectOtherProjects;
            _opt.FromUserLibrary = st.CollectUserLibrary;
            _opt.FromFactoryPacks = st.CollectFactoryPacks;

            AddRow("Files from elsewhere", SampleOrigin.Elsewhere, _opt.FromElsewhere);
            AddRow("Files from other Projects", SampleOrigin.OtherProject, _opt.FromOtherProjects);
            AddRow("Files from User Library", SampleOrigin.UserLibrary, _opt.FromUserLibrary);
            AddRow("Files from Factory Packs", SampleOrigin.FactoryPack, _opt.FromFactoryPacks);

            _ok.Text = "Collect";
            _ok.Primary = true;
            _ok.FitToText(20);
            _ok.Enabled = false;
            _ok.Click += delegate { Start(); };
            Controls.Add(_ok);

            _cancel.Text = "Cancel";
            _cancel.FitToText(20);
            _cancel.Click += delegate { OnCancel(); };
            Controls.Add(_cancel);

            _tick.Interval = 100;
            _tick.Tick += delegate { Invalidate(); };
            _tick.Start();
        }

        bool _started;

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // Считать начинаем, только когда окно показано. BeginInvoke до создания
            // хендла бросает InvalidOperationException, а подсчёт вполне успевает
            // кончиться раньше, чем ShowDialog доберётся до показа окна.
            if (_started) return;
            _started = true;
            ThreadPool.QueueUserWorkItem(delegate { CountInBackground(); });
        }

        void AddRow(string label, SampleOrigin origin, bool on)
        {
            Row r = new Row();
            r.Label = label;
            r.Origin = origin;
            r.Toggle = new PillToggle();
            r.Toggle.IsSwitch = true;
            r.Toggle.Size = new Size(Sc(42), Sc(24));
            r.Toggle.Checked = on;
            r.Toggle.Enabled = false;           // до конца подсчёта трогать нечего
            r.Toggle.CheckedChanged += delegate { Recount(); };
            Controls.Add(r.Toggle);
            _rows.Add(r);
        }

        // ------------------------------------------------------------------ подсчёт

        /// <summary>
        /// Вернуться в поток окна из фонового. Окно могут закрыть, пока фоновая работа
        /// идёт: тогда хендла уже нет и BeginInvoke бросает — ловим здесь, в одном месте,
        /// а не проверкой IsDisposed в каждом обработчике (она всё равно гонка).
        /// </summary>
        void Post(MethodInvoker action)
        {
            try
            {
                if (IsDisposed || !IsHandleCreated) return;
                BeginInvoke(action);
            }
            catch (Exception) { }
        }

        void CountInBackground()
        {
            AlsInfo info = AlsFile.Read(_set.Path);
            List<SampleDep> deps = info.Error == null
                ? SampleScan.Of(info, Path.GetDirectoryName(_set.Path), _env)
                : new List<SampleDep>();

            Post(delegate
            {
                _info = info;
                _deps = deps;
                _counting = false;
                if (info.Error != null) { _error = info.Error; Invalidate(); return; }

                foreach (Row r in _rows)
                {
                    r.Files = 0; r.Bytes = 0;
                    r.Toggle.Enabled = true;
                }
                _inProjectFiles = 0; _inProjectBytes = 0; _notFound = 0;

                foreach (SampleDep d in deps)
                {
                    if (d.Origin == SampleOrigin.Missing) { _notFound++; continue; }
                    if (d.Origin == SampleOrigin.InProject)
                    { _inProjectFiles++; _inProjectBytes += d.Size; continue; }
                    foreach (Row r in _rows)
                        if (r.Origin == d.Origin) { r.Files++; r.Bytes += d.Size; break; }
                }

                Recount();
                LayoutRows();
                Invalidate();
            });
        }

        void Recount()
        {
            if (_deps == null || _info == null) return;
            _opt.FromElsewhere = _rows[0].Toggle.Checked;
            _opt.FromOtherProjects = _rows[1].Toggle.Checked;
            _opt.FromUserLibrary = _rows[2].Toggle.Checked;
            _opt.FromFactoryPacks = _rows[3].Toggle.Checked;

            _plan = CollectAll.Plan(_set, _info, _deps, _opt);
            _ok.Enabled = !_running && _plan.TotalBytes < _plan.FreeBytes;
            Invalidate();
        }

        // ------------------------------------------------------------------ сборка

        void Start()
        {
            if (_plan == null || _running) return;

            Settings st = Settings.Load();
            st.CollectElsewhere = _opt.FromElsewhere;
            st.CollectOtherProjects = _opt.FromOtherProjects;
            st.CollectUserLibrary = _opt.FromUserLibrary;
            st.CollectFactoryPacks = _opt.FromFactoryPacks;
            st.Save();

            _running = true;
            _ok.Enabled = false;
            foreach (Row r in _rows) r.Toggle.Enabled = false;
            _total = _plan.Copy.Count;
            _done = 0;
            _cancel = new CancellationTokenSource();

            CollectPlan plan = _plan;
            AlsInfo info = _info;
            CancellationToken token = _cancel.Token;

            ThreadPool.QueueUserWorkItem(delegate
            {
                string error = "";
                bool done = false;
                try
                {
                    CollectAll.Run(plan, _set, info,
                        delegate (int n, int of, string what) { _done = n; _total = of; _current = what; },
                        token);
                    done = true;
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { error = ex.Message; }

                Post(delegate
                {
                    _running = false;
                    if (done)
                    {
                        Produced = plan.TargetDir;
                        Failed = plan.Failed.Count;
                        DialogResult = DialogResult.OK;
                        Close();
                    }
                    else
                    {
                        _error = error;
                        if (error.Length == 0) Close();     // отменили — просто закрываемся
                        else { _ok.Enabled = true; Invalidate(); }
                    }
                });
            });

            Invalidate();
        }

        void OnCancel()
        {
            if (_running && _cancel != null) { _cancel.Cancel(); return; }
            DialogResult = DialogResult.Cancel;
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _tick.Stop();
            if (_cancel != null) { try { _cancel.Cancel(); } catch { } }
            base.OnFormClosed(e);
        }

        // ------------------------------------------------------------------ раскладка

        int RowTop { get { return Card.Top + Sc(80); } }
        int RowStep { get { return Sc(34); } }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutRows();
        }

        void LayoutRows()
        {
            int h = Sc(Theme.ControlH);
            int pad = Sc(24);
            _cancel.SetBounds(Card.Right - pad - _cancel.Width, Card.Bottom - pad - h, _cancel.Width, h);
            _ok.SetBounds(_cancel.Left - Sc(10) - _ok.Width, Card.Bottom - pad - h, _ok.Width, h);

            int y = RowTop;
            foreach (Row r in _rows)
            {
                r.Rect = new Rectangle(Card.Left + pad, y, Card.Width - pad * 2, RowStep);
                r.Toggle.Location = new Point(r.Rect.X, y + (RowStep - r.Toggle.Height) / 2);
                r.Toggle.Visible = !_running && !_counting;
                y += RowStep;
            }
        }

        // ------------------------------------------------------------------ рисование

        static string Mb(long bytes)
        {
            if (bytes >= 1073741824L)
                return (bytes / 1073741824.0).ToString("0.0", CultureInfo.InvariantCulture) + " GB";
            return (bytes / 1048576.0).ToString("0", CultureInfo.InvariantCulture) + " MB";
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            Theme.Smooth(g);

            int pad = Sc(24);
            int left = Card.Left + pad, right = Card.Right - pad;
            Rectangle head = new Rectangle(left, Card.Top + Sc(46), right - left, Sc(24));

            if (_error.Length > 0)
            {
                Chrome.DrawText(g, "Cannot read this set: " + _error, Theme.FBody, head, Theme.Red, Chrome.Left);
                return;
            }

            if (_counting)
            {
                Chrome.DrawText(g, "Counting…", Theme.FBody, head, Theme.TextDim, Chrome.Left);
                return;
            }

            if (_running)
            {
                int total = _total, done = _done;
                Chrome.DrawText(g, string.Format("Copying {0} of {1}", done, total),
                                Theme.FBody, head, Theme.Text, Chrome.Left);

                RectangleF bar = new RectangleF(left, head.Bottom + Sc(16), right - left, Sc(6));
                Theme.FillRound(g, bar, bar.Height / 2f, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
                if (total > 0 && done > 0)
                {
                    RectangleF fill = new RectangleF(bar.X, bar.Y, bar.Width * done / (float)total, bar.Height);
                    Theme.FillRound(g, fill, bar.Height / 2f, Theme.Text);
                }

                Rectangle now = new Rectangle(left, (int)bar.Bottom + Sc(10), right - left, Sc(20));
                Chrome.DrawText(g, _current ?? "", Theme.FLabel, now, Theme.TextDim, Chrome.Left);
                return;
            }

            Chrome.DrawText(g, "Specify which used media files are to be copied into the project.",
                            Theme.FBody, head, Theme.Text, Chrome.Left);

            foreach (Row r in _rows)
            {
                int textX = r.Toggle.Right + Sc(14);
                Rectangle label = new Rectangle(textX, r.Rect.Y, Sc(260), r.Rect.Height);
                Chrome.DrawText(g, r.Label, Theme.FBody, label, Theme.Text, Chrome.Left);

                Rectangle nums = new Rectangle(right - Sc(240), r.Rect.Y, Sc(240), r.Rect.Height);
                string s = r.Files == 0 ? "—"
                    : string.Format("{0} files    {1}", r.Files, Mb(r.Bytes));
                Chrome.DrawText(g, s, Theme.FLabel, nums,
                                r.Toggle.Checked ? Theme.Text : Theme.TextDim, Chrome.Right);
            }

            int y = RowTop + _rows.Count * RowStep + Sc(14);
            Extra(g, left, right, ref y, "In project (always copied)",
                  string.Format("{0} files    {1}", _inProjectFiles, Mb(_inProjectBytes)));
            if (_notFound > 0)
                Extra(g, left, right, ref y, "Not found — left as they are",
                      string.Format("{0} files", _notFound));

            y += Sc(8);
            using (Pen p = new Pen(Theme.Hairline)) g.DrawLine(p, left, y, right, y);
            y += Sc(10);

            if (_plan != null)
            {
                bool fits = _plan.TotalBytes < _plan.FreeBytes;
                Extra(g, left, right, ref y,
                      string.Format("Will copy {0} files, {1}", _plan.Copy.Count, Mb(_plan.TotalBytes)),
                      fits ? "Free: " + Mb(_plan.FreeBytes) : "Not enough space",
                      fits ? Theme.Text : Theme.Red);
            }
        }

        void Extra(Graphics g, int left, int right, ref int y, string label, string value)
        {
            Extra(g, left, right, ref y, label, value, Theme.TextDim);
        }

        void Extra(Graphics g, int left, int right, ref int y, string label, string value, Color color)
        {
            Rectangle l = new Rectangle(left, y, right - left - Sc(240), Sc(22));
            Rectangle v = new Rectangle(right - Sc(240), y, Sc(240), Sc(22));
            Chrome.DrawText(g, label, Theme.FLabel, l, color, Chrome.Left);
            Chrome.DrawText(g, value, Theme.FLabel, v, color, Chrome.Right);
            y += Sc(22);
        }
    }
}
```

- [ ] **Step 5: Собрать и снять окно**

```bash
tools/build-sample-test.cmd
```

Ожидается `OK: …\SampleTest.exe`. Затем собрать `Shot.exe` и снять окно:

```bash
"$WINDIR/Microsoft.NET/Framework64/v4.0.30319/csc.exe" /nologo /target:exe /main:Reel.Shot /out:"$TEMP/alive-sample-test/Shot.exe" /reference:System.dll /reference:System.Drawing.dll proto/test/Shot.cs
```

```bash
"$TEMP/alive-sample-test/Shot.exe" "$TEMP/alive-sample-test/SampleTest.exe" "$TEMP/alive-sample-test/collect.png" show "E:\Music\Ableton Projects\Series 2\somnitelno\flush Project\flush.als"
```

Посмотреть `collect.png`. Проверить глазами: четыре строки с переключателями, у каждой число файлов и вес; строки «In project» и «Will copy …»; кнопки `Collect` и `Cancel` не нулевой ширины и не наезжают друг на друга.

- [ ] **Step 6: Проверить, что приложение собирается**

```bash
build.cmd
```

- [ ] **Step 7: Коммит**

```bash
git add src/CollectDialog.cs src/Settings.cs tools/SampleTest.cs
git commit -m "Сэмплы: окно сборки проекта

Четыре пункта «Collect All and Save» самой Live, по одному переключателю
на категорию, выбор запоминается в settings.cfg. Сверх Live — число
файлов и вес у каждого пункта и итог внизу: гигабайты у паков надо
видеть до нажатия, а не после. Не влезает на диск — Collect заблокирован.

Одно окно, два состояния, как у RescueDialog: подсчёт и выбор, потом
полоса с текущим файлом и отменой.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Кнопка в инспекторе

**Files:**
- Modify: `src/DetailPanel.cs`
- Modify: `src/MainForm.cs`

**Interfaces:**
- Consumes: `CollectDialog`, `DetailPanel.RescueRequested` (как образец события)
- Produces: `DetailPanel.CollectRequested` (`event Action`)

- [ ] **Step 1: Добавить кнопку в `DetailPanel`**

Рядом с объявлением `_rescue` (`src/DetailPanel.cs:16`):

```csharp
        readonly GlassButton _collect = new GlassButton();
```

Рядом с `public event Action RescueRequested;`:

```csharp
        /// <summary>Собрать проект в переносимую папку — см. CollectDialog.</summary>
        public event Action CollectRequested;
```

В конструкторе, сразу после блока `_rescue`:

```csharp
            _collect.Text = "Collect All";
            _collect.Surface = Theme.Backdrop;
            _collect.Click += delegate { if (CollectRequested != null) CollectRequested(); };
            Controls.Add(_collect);
```

В `ApplyAction`, в ветке `_pluginMode`:

```csharp
                _collect.Visible = false;
```

и в ветке сета, рядом с `_rescue.Visible = …`:

```csharp
                _collect.Visible = !_pluginMode && _set != null && !inList;
```

В `OnResize` заменить строку с `_forks`

```csharp
            _forks.SetBounds(Pad, _rescue.Top - Sc(8) - h, w, h);
```

на две — `Collect All` встаёт между `Rescue Project` и `Forks`:

```csharp
            _collect.SetBounds(Pad, _rescue.Top - Sc(8) - h, w, h);
            _forks.SetBounds(Pad, _collect.Top - Sc(8) - h, w, h);
```

Положение считается безусловно, как и у соседей: видимостью заведует `ApplyAction`, а не раскладка.

- [ ] **Step 2: Увеличить резерв под кнопки**

В `src/DetailPanel.cs`, в свойстве `BodyBottom`, заменить строку

```csharp
                int rows = _pluginMode ? 0 : (_showInListRequested != null ? 2 : (ForksRequested != null ? 3 : 2));
```

на

```csharp
                int rows = _pluginMode ? 0 : (_showInListRequested != null ? 2 : (ForksRequested != null ? 4 : 3));
```

В режиме списка по-прежнему две кнопки (`Open in Live` и `Show in List`) — `Collect All` там не показывается, как и `Rescue Project`. В режиме плиток их стало на одну больше.

В комментарии над `BodyBottom` заменить «у сета их до трёх сразу (Open in Live, Rescue Project и Forks…)» на «у сета их до четырёх сразу (Open in Live, Rescue Project, Collect All и Forks…)».

- [ ] **Step 3: Подключить в `MainForm`**

Рядом с `_detail.RescueRequested += RescueSelected;` (`src/MainForm.cs:763`):

```csharp
            _detail.CollectRequested += CollectSelected;
```

Рядом с `RescueSet` (`src/MainForm.cs:2503`) добавить:

```csharp
        void CollectSelected()
        {
            SetEntry s = SelectedSet();
            CollectSet(s);
        }

        /// <summary>
        /// Собрать проект: все нужные ему медиафайлы в одну папку рядом с ним, плюс копия
        /// сета с переписанными путями. Оригинал не трогается — см. CollectAll.
        /// </summary>
        void CollectSet(SetEntry s)
        {
            if (s == null) return;
            if (!File.Exists(s.Path))
            {
                Status("File is gone: " + s.Path);
                return;
            }

            using (CollectDialog d = new CollectDialog(s, _index.Env))
            {
                d.ShowDialog(this);
                if (d.Produced.Length > 0)
                {
                    Notify(d.Failed > 0
                        ? string.Format("Collected to {0} — {1} file(s) could not be copied, see the log",
                                        Path.GetFileName(d.Produced), d.Failed)
                        : "Collected to " + Path.GetFileName(d.Produced));
                    try { Process.Start("explorer.exe", "\"" + d.Produced + "\""); }
                    catch (Exception ex) { Diag.Line("collect: explorer: " + ex.Message); }
                }
            }
        }
```

`SelectedSet()` уже есть (`src/MainForm.cs:2391`) — `CollectSelected` устроен ровно как соседний `RescueSelected`.

- [ ] **Step 4: Собрать и посмотреть в живом приложении**

```bash
build.cmd
```

Затем запустить `bin/Alive.exe`, выбрать сет на главной (плитками — кнопка появляется там же, где `Rescue Project`), убедиться что:
- кнопка `Collect All` видна и не наезжает на соседей;
- по клику открывается окно, считает и показывает четыре строки с числами;
- `Collect` собирает папку, открывается проводник, в каталоге появляется собранная копия отдельной строкой с местом `Collected`;
- собранный `.als` открывается в Live и звучит так же.

- [ ] **Step 5: Коммит**

```bash
git add src/DetailPanel.cs src/MainForm.cs
git commit -m "Сэмплы: кнопка Collect All в инспекторе

Кнопка в ту же нижнюю стопку, где Rescue Project, и при тех же условиях.
По завершении — тост и проводник на собранной папке.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## Проверка целиком

После Задачи 5 прогнать всё разом:

```bash
tools/build-sample-test.cmd && "$TEMP/alive-sample-test/SampleTest.exe" scan "E:\Music\Ableton Projects\Series 2"
```

```bash
"$TEMP/alive-sample-test/SampleTest.exe" patch "E:\Music\Ableton Projects\Series 2\somnitelno\flush Project\flush.als" && "$TEMP/alive-sample-test/SampleTest.exe" collect "E:\Music\Ableton Projects\Series 2\somnitelno\flush Project\flush.als"
```

```bash
build.cmd
```

Все три — без `FAIL` и без `BUILD FAILED`.

Последняя проверка только руками и обязательна: **открыть собранный `.als` в самой Live** и убедиться, что ни один клип не показан потерянным. Ни один автоматический тест этого не заменяет — RefResolver и Live читают одни и те же поля, но окончательный судья тут Live.
