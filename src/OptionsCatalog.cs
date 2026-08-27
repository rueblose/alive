using System;
using System.Collections.Generic;

namespace AbletonOptions
{
    public enum Kind { Flag, Num, Text, Choice }

    // Doc    - officially documented by Ableton
    // New12  - introduced / confirmed in Live 12
    // Ok     - working option (Live 11/12, community-verified)
    // Dev    - internal, for Ableton's own debugging and tests
    // Legacy - removed in Live 11, does nothing in Live 12
    public enum Stat { Doc, New12, Ok, Dev, Legacy }

    public sealed class Opt
    {
        public string Name;
        public Kind Kind;
        public Stat Stat;
        public string Cat;          // английский ключ категории
        public string TitleEn, TitleRu;
        public string DescEn, DescRu;
        public string NoteEn, NoteRu;
        public string Def, Min, Max;
        public string[] Choices;
        public string Plat;         // null / "Windows" / "macOS"

        public string Title { get { return L.S(TitleEn, TitleRu); } }
        public string Desc { get { return L.S(DescEn, DescRu); } }
        public string Note { get { return L.S(NoteEn, NoteRu); } }
    }

    public static class Catalog
    {
        public const string CatWorkflow = "Workflow";
        public const string CatUi       = "Interface";
        public const string CatEditor   = "MIDI editor";
        public const string CatAudio    = "Audio & CPU";
        public const string CatMidi     = "MIDI";
        public const string CatPlugins  = "Plug-ins & devices";
        public const string CatBrowser  = "Browser & files";
        public const string CatCtrl     = "Controllers & Push";
        public const string CatRewire   = "ReWire";
        public const string CatDiag     = "Diagnostics & logs";
        public const string CatDev      = "Internal / tests";
        public const string CatLegacy   = "Deprecated";

        public static readonly string[] Categories = new string[]
        {
            CatWorkflow, CatUi, CatEditor, CatAudio, CatMidi, CatPlugins,
            CatBrowser, CatCtrl, CatRewire, CatDiag, CatDev, CatLegacy
        };

        public static string CatName(string key)
        {
            switch (key)
            {
                case CatWorkflow: return L.S("Workflow", "Рабочий процесс");
                case CatUi:       return L.S("Interface", "Интерфейс");
                case CatEditor:   return L.S("MIDI editor", "Редактор нот");
                case CatAudio:    return L.S("Audio & CPU", "Аудио и CPU");
                case CatMidi:     return L.S("MIDI", "MIDI");
                case CatPlugins:  return L.S("Plug-ins & devices", "Плагины и устройства");
                case CatBrowser:  return L.S("Browser & files", "Браузер и файлы");
                case CatCtrl:     return L.S("Controllers & Push", "Контроллеры и Push");
                case CatRewire:   return L.S("ReWire", "ReWire");
                case CatDiag:     return L.S("Diagnostics & logs", "Диагностика и логи");
                case CatDev:      return L.S("Internal / tests", "Внутреннее и тесты");
                case CatLegacy:   return L.S("Deprecated", "Устаревшие");
            }
            return key;
        }

        public static string StatName(Stat s)
        {
            switch (s)
            {
                case Stat.Doc:    return L.S("official", "официально");
                case Stat.New12:  return L.S("Live 12", "Live 12");
                case Stat.Dev:    return L.S("internal", "служебная");
                case Stat.Legacy: return L.S("removed", "не работает");
            }
            return "";
        }

        static readonly List<Opt> _all = new List<Opt>();
        public static List<Opt> All { get { return _all; } }

        static void A(string name, Kind kind, Stat stat, string cat,
                      string title, string titleRu,
                      string desc, string descRu,
                      string def = null, string min = null, string max = null,
                      string choices = null, string plat = null,
                      string note = null, string noteRu = null)
        {
            Opt o = new Opt();
            o.Name = name; o.Kind = kind; o.Stat = stat; o.Cat = cat;
            o.TitleEn = title; o.TitleRu = titleRu;
            o.DescEn = desc; o.DescRu = descRu;
            o.NoteEn = note; o.NoteRu = noteRu;
            o.Def = def; o.Min = min; o.Max = max;
            o.Choices = string.IsNullOrEmpty(choices) ? null : choices.Split('|');
            o.Plat = plat;
            _all.Add(o);
        }

        public static Opt Find(string name)
        {
            for (int i = 0; i < _all.Count; i++)
                if (string.Equals(_all[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return _all[i];
            return null;
        }

        /// <summary>
        /// Раньше числились рабочими опциями Live 11/12, но прямая проверка на Live 12.4.3
        /// показала диалог «unknown option» для каждой — Live их не понимает. Раз мы знаем
        /// это точно, при чтении файла они отбрасываются, а не сохраняются как «свои
        /// неизвестные» — иначе предупреждение возвращалось бы при каждом перезапуске Live.
        /// </summary>
        public static readonly HashSet<string> KnownBad = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "VstNoLocalDir", "VstNoScanSkip", "NoVstStartupScan",
            "NoMidiMonitorLatencyCompensation", "MidiClockSlave",
            "ShowBackToArrangementOnMasterTrack", "ThinningAggressiveness", "EnableMapToSiblings",
        };

        static Catalog()
        {
            // ---------------------------------------------------------------
            // WORKFLOW
            // ---------------------------------------------------------------
            A("EnableArmOnSelection", Kind.Flag, Stat.Ok, CatWorkflow,
              "Arm track on selection", "Авто-арм выбранного трека",
              "Selecting a track automatically arms it for recording, so you never have to click the record button on the track. A classic for fast MIDI sketching.",
              "При выделении трека он автоматически становится записываемым (Arm) — не нужно каждый раз жать кнопку записи на треке. Классика для быстрой набивки MIDI.",
              note: "Ableton advises against this when using Push 2 / Push 3 — it clashes with the controller's logic.",
              noteRu: "Ableton не рекомендует включать при работе с Push 2 / Push 3 — конфликтует с логикой контроллера.");

            A("NoAutoArming", Kind.Flag, Stat.New12, CatWorkflow,
              "Disable auto-arming entirely", "Запретить авто-арм полностью",
              "The opposite option: switches off automatic record-arming on track selection completely, including Live's own default behaviour. Handy if you keep recording over things by accident.",
              "Обратная по смыслу опция: полностью отключает автоматическое включение записи на треке при его выборе, включая штатное поведение Live. Полезно, если постоянно случайно записываешь поверх.");

            A("EnableHotSwapOnSelection", Kind.Flag, Stat.Ok, CatWorkflow,
              "Hot-Swap follows selection", "Hot-Swap следует за выделением",
              "Hot-Swap mode automatically retargets to whichever device or sample you select, without pressing the Hot-Swap button again.",
              "Режим горячей замены (Hot-Swap) автоматически переключается на то устройство или сэмпл, который ты выделил, без повторного нажатия кнопки.");

            A("AutoAdjustMacroMappingRange", Kind.Flag, Stat.Ok, CatWorkflow,
              "Auto-fit macro mapping range", "Авто-диапазон при маппинге макросов",
              "When you map a parameter to a macro knob, Live fits the macro's Min/Max range around the parameter's current value instead of defaulting to 0–100%.",
              "При назначении параметра на макро-ручку Live автоматически подстраивает Min/Max диапазон макроса под текущее значение параметра, а не ставит 0–100%.");

            A("DontRetriggerSessionClips", Kind.Flag, Stat.Ok, CatWorkflow,
              "Don't retrigger Session clips", "Не перезапускать клипы Session",
              "A Session clip that is stopped and launched again resumes from where it stopped instead of starting over.",
              "Остановленный и снова запущенный клип в Session View продолжает с той позиции, где остановился, а не с начала.");

            A("ClipFireWillContinueSong", Kind.Flag, Stat.Ok, CatWorkflow,
              "Clip launch continues the song", "Запуск клипа продолжает песню",
              "Launching a clip continues playback that is already running instead of restarting the transport from zero.",
              "Запуск клипа продолжает уже идущее воспроизведение вместо перезапуска транспорта с нуля.");

            A("IgnoreTemplateSet", Kind.Flag, Stat.Ok, CatWorkflow,
              "Ignore the template set", "Игнорировать шаблон проекта",
              "New Live Sets open empty — your Template Set is not loaded. Useful when the template is heavy but you want a clean project fast.",
              "Новый Live Set создаётся пустым, шаблонный сет (Template Set) не загружается. Удобно, если шаблон тяжёлый, а нужен быстрый чистый проект.");

            A("SelectNoAudioAsDefault", Kind.Flag, Stat.Ok, CatWorkflow,
              "New audio tracks default to no input", "Новый аудиотрек без входа",
              "New audio tracks default their input to \"No Audio\" instead of the first physical input. Saves you from stray bleed and feedback.",
              "У новых аудиотреков вход по умолчанию выставляется в «No Audio» вместо первого физического входа. Спасает от случайных наводок и фидбэка.");

            A("AudioQuantizeFixedWarpMarker", Kind.Flag, Stat.Ok, CatWorkflow,
              "Quantize to a fixed warp marker", "Квантайз к фиксированному warp-маркеру",
              "Changes how audio-clip quantization works: it snaps relative to a pinned warp marker.",
              "Меняет логику квантизации аудиоклипа: привязка выполняется к зафиксированному warp-маркеру.");

            A("HotKeyForCaptureAndInsertScene", Kind.Text, Stat.Ok, CatWorkflow,
              "Hotkey for Capture and Insert Scene", "Хоткей для Capture and Insert Scene",
              "Assigns your own keyboard shortcut to Capture and Insert Scene. The value is a key combination, e.g. Shift+Ctrl+I.",
              "Назначает свою горячую клавишу для команды Capture and Insert Scene. Значение пишется как комбинация клавиш, например Shift+Ctrl+I.",
              def: "Shift+Ctrl+I");

            A("HotKeyForKitPad", Kind.Text, Stat.Ok, CatWorkflow,
              "Hotkey for Kit Pad", "Хоткей для Kit Pad",
              "Assigns your own keyboard shortcut for opening the Kit Pad. The value is a key combination, e.g. Shift+Ctrl+K.",
              "Назначает свою горячую клавишу для открытия Kit Pad. Значение — комбинация клавиш, например Shift+Ctrl+K.",
              def: "Shift+Ctrl+K");

            A("DisableHotKeyLatching", Kind.Flag, Stat.New12, CatWorkflow,
              "Disable hotkey latching", "Отключить залипание хоткеев",
              "Removes the \"latching\" behaviour of modal hotkeys: a modifier only applies while the key is physically held down.",
              "Убирает «залипание» модальных горячих клавиш: модификатор действует только пока клавиша реально удерживается.");

            A("WipeoutProtectionActivationWindowSizeMs", Kind.Num, Stat.Ok, CatWorkflow,
              "Wipeout protection: activation window", "Защита от затирания: окно активации",
              "Wipeout Protection guards against accidentally overwriting an Arrangement recording. This sets the activation window, in milliseconds.",
              "Wipeout Protection страхует от случайного затирания записи в Arrangement. Этот параметр задаёт окно активации защиты в миллисекундах.",
              min: "0");

            A("WipeoutProtectionAccidentWindowSizeMs", Kind.Num, Stat.Ok, CatWorkflow,
              "Wipeout protection: accident window", "Защита от затирания: окно «случайности»",
              "How long, in milliseconds, an action is still treated as an accidental press rather than a deliberate overwrite.",
              "Окно в миллисекундах, в течение которого действие трактуется как случайное нажатие, а не осознанная перезапись.",
              min: "0");

            A("WipeoutProtectionDecisionWindowSizeMs", Kind.Num, Stat.Ok, CatWorkflow,
              "Wipeout protection: decision window", "Защита от затирания: окно решения",
              "The window, in milliseconds, Live has to decide whether an action counts as a wipeout.",
              "Окно в миллисекундах, за которое Live принимает решение, считать ли действие затиранием.",
              min: "0");

            A("LoopJumpContinuationWindowSizeMs", Kind.Num, Stat.Ok, CatWorkflow,
              "Loop-jump continuation window", "Окно продолжения при прыжке лупа",
              "Window size, in milliseconds, for continuous playback across a loop jump — smooths out recording behaviour at the loop boundary.",
              "Размер окна (мс) для механизма непрерывного воспроизведения при прыжке по лупу — сглаживает поведение записи на границе цикла.",
              min: "0");

            A("LoopJumpContinuationActivationWindowSizeMs", Kind.Num, Stat.Ok, CatWorkflow,
              "Loop-jump continuation: activation window", "Окно активации продолжения лупа",
              "Activation window, in milliseconds, for the loop-jump continuation mechanism.",
              "Окно активации (мс) механизма непрерывного воспроизведения при прыжке по лупу.",
              min: "0");

            A("KeepUnresolvedRelativeRoutings", Kind.Flag, Stat.Ok, CatWorkflow,
              "Keep unresolved routings", "Сохранять неразрешённые роутинги",
              "Live keeps relative routings it could not resolve when opening a project instead of clearing them. Saves your routing when moving projects between systems.",
              "Live не сбрасывает относительные маршруты (routing), которые не удалось разрешить при открытии проекта, а сохраняет их как есть. Спасает роутинг при переносе проектов между системами.");

            A("DontStoreCompoundPaths", Kind.Flag, Stat.Ok, CatWorkflow,
              "Don't store compound paths", "Не сохранять составные пути",
              "Live does not write compound file paths into the project.",
              "Live не записывает в проект составные (compound) пути к файлам.");

            A("DisableFileRefMapping", Kind.Flag, Stat.Ok, CatWorkflow,
              "Disable file reference mapping", "Отключить маппинг файловых ссылок",
              "Turns off the file-reference remapping mechanism. Affects how Live hunts for missing samples.",
              "Отключает механизм переназначения ссылок на файлы. Влияет на то, как Live ищет пропавшие сэмплы.");

            A("TempoFineControlRange", Kind.Num, Stat.Ok, CatWorkflow,
              "Tempo fine-control step", "Шаг точной подстройки темпа",
              "Range of the fine tempo adjustment (dragging with Ctrl held). Smaller value means finer control.",
              "Диапазон/шаг точной регулировки темпа (при перетаскивании с зажатым Ctrl). Меньше значение — тоньше подстройка.",
              def: "0.02", min: "0");

            A("SafetyAreaAroundMarkers", Kind.Num, Stat.Ok, CatWorkflow,
              "Safety area around markers", "Защитная зона вокруг маркеров",
              "Size, in pixels, of the dead zone around markers so you don't grab them by accident.",
              "Размер «мёртвой зоны» в пикселях вокруг маркеров, чтобы не хватать их мышью случайно.",
              def: "10", min: "0");

            A("DontAskForAdminRights", Kind.Flag, Stat.Ok, CatWorkflow,
              "Never ask for admin rights", "Не запрашивать права администратора",
              "Live stops prompting for elevation. Useful on restricted accounts where UAC would refuse anyway.",
              "Live не показывает запрос на повышение прав. Полезно на машинах с ограниченной учёткой, где UAC всё равно откажет.");

            A("BuiltinLessons", Kind.Num, Stat.Ok, CatWorkflow,
              "Built-in Lessons", "Встроенные уроки (Lessons)",
              "Controls whether Live's built-in lesson set appears in the browser. 0 hides it, 1 shows it.",
              "Управляет наличием встроенного набора уроков в браузере Live. Значение 0 отключает, 1 включает.",
              def: "1", min: "0", max: "1");

            A("DecodeDirectShowMediaFiles", Kind.Flag, Stat.Ok, CatWorkflow,
              "Decode media through DirectShow", "Декодировать медиа через DirectShow",
              "Allows importing audio from containers Live cannot read on its own (AVI, WMV and friends) via the system's DirectShow.",
              "Разрешает импорт звука из контейнеров, которые Live сам не читает (AVI, WMV и т.п.), через системный DirectShow.",
              plat: "Windows");

            A("DevicesLegacyLive5", Kind.Flag, Stat.Ok, CatWorkflow,
              "Live 5 device compatibility", "Режим совместимости устройств Live 5",
              "Makes built-in devices behave as they did in Live 5. Only needed to reproduce very old projects faithfully.",
              "Включает поведение встроенных устройств как в Live 5. Нужно только для точного воспроизведения очень старых проектов.");

            // ---------------------------------------------------------------
            // INTERFACE
            // ---------------------------------------------------------------
            A("ShowDeviceSlots", Kind.Flag, Stat.Ok, CatUi,
              "Device slots in the mixer", "Слоты устройств в микшере",
              "Shows each track's device chain right in the Session mixer — you can see the whole plug-in chain and toggle devices from there. The most popular Options.txt trick, and it still works in Live 12.",
              "Показывает в микшере Session View список устройств каждого трека — видно всю цепочку плагинов, можно включать/выключать их прямо оттуда. Самая популярная опция Options.txt, работает и в Live 12.",
              note: "Known quirk: device names may not draw at first — briefly resizing the track width fixes it.",
              noteRu: "Известный баг: имена устройств могут не отрисоваться сразу — помогает временное изменение ширины трека.");

            A("ShowFullVersionInTitle", Kind.Flag, Stat.Ok, CatUi,
              "Full version in the title bar", "Полная версия в заголовке окна",
              "Live's window title shows the full version number including the build. Handy when testing betas.",
              "В заголовке окна Live выводится полный номер версии, включая номер сборки. Удобно при тестировании бет.");

            A("ShowChunkLoadTime", Kind.Flag, Stat.Ok, CatUi,
              "Set load time in the Info View", "Время загрузки сета в Info View",
              "Reports in the Info View how long the Live Set took to load. Useful for hunting down slow projects.",
              "Показывает в Info View, сколько времени заняла загрузка Live Set. Помогает искать тормозящие проекты.");

            A("DisableGraphicsHardwareAcceleration", Kind.Flag, Stat.Doc, CatUi,
              "Disable graphics acceleration", "Отключить аппаратное ускорение графики",
              "Turns off the hardware-accelerated UI rendering enabled by default since Live 11.2 (Metal on macOS). Cures drawing artefacts, flicker and sluggish UI on problematic GPUs.",
              "Отключает аппаратный рендеринг интерфейса, включённый по умолчанию с Live 11.2 (Metal на macOS). Лечит артефакты отрисовки, мерцание и тормоза UI на проблемных видеокартах.");

            A("AnimatedScrollBarVisibleTime", Kind.Num, Stat.Ok, CatUi,
              "Scrollbar visible time", "Время видимости скроллбаров",
              "How many seconds animated scrollbars stay visible before fading away.",
              "Сколько секунд анимированные полосы прокрутки остаются видимыми, прежде чем растворятся.",
              min: "0.0", max: "10.0");

            A("AnimatedScrollBarAlphaDelta", Kind.Num, Stat.Ok, CatUi,
              "Scrollbar fade speed", "Скорость растворения скроллбаров",
              "How fast animated scrollbars change opacity: 0.0 is very slow, 1.0 is instant.",
              "Скорость изменения прозрачности анимированных полос прокрутки: 0.0 — очень медленно, 1.0 — мгновенно.",
              min: "0.0", max: "1.0");

            A("LayoutCheckFrequency", Kind.Num, Stat.Ok, CatUi,
              "Screen layout check frequency", "Частота проверки раскладки экрана",
              "How often, in milliseconds, Live checks for monitor layout or resolution changes. Less often means less overhead but slower reaction to display changes.",
              "Как часто (мс) Live проверяет изменение раскладки/разрешения мониторов. Реже — меньше нагрузка, но медленнее реакция на смену конфигурации экранов.",
              min: "100", max: "10000");

            A("MaxChainViewsToCache", Kind.Num, Stat.Ok, CatUi,
              "Rack chain view cache", "Кэш отрисовки цепочек рэков",
              "How many rack chain views Live keeps cached. Higher means smoother navigation in big racks at the cost of memory.",
              "Сколько представлений цепочек рэка Live держит в кэше. Больше — плавнее навигация по большим рэкам, но больше памяти.",
              def: "16", min: "1");

            A("RemoveFolderWithSingleClick", Kind.Flag, Stat.Ok, CatUi,
              "Remove folder with one click", "Удаление папки одним кликом",
              "Cuts a step out of removing a user folder from the browser — one click is enough.",
              "Убирает лишний шаг при удалении пользовательской папки из браузера — достаточно одного клика.");

            A("DrawRandomBackground", Kind.Flag, Stat.Dev, CatUi,
              "Random background (render debug)", "Случайный фон (отладка отрисовки)",
              "Fills repainted regions with random colours. A rendering debug tool, useless for normal work.",
              "Заливает области перерисовки случайным цветом. Инструмент отладки рендеринга, для обычной работы бесполезен.");

            // ---------------------------------------------------------------
            // MIDI EDITOR
            // ---------------------------------------------------------------
            A("EditorDontSnapOnMarkers", Kind.Flag, Stat.Ok, CatEditor,
              "Don't snap to markers", "Не липнуть к маркерам",
              "Notes and clips stop magnetising to locators and markers in the Arrangement — only the grid remains.",
              "Ноты и клипы перестают магнититься к локаторам и маркерам в Arrangement View — остаётся только сетка.");

            A("EditorNoteResizeSnapsOnGridOnly", Kind.Flag, Stat.Ok, CatEditor,
              "Resize notes to the grid only", "Растягивание нот только по сетке",
              "When changing a note's length, snapping considers only grid lines and ignores neighbouring note edges.",
              "При изменении длины ноты привязка идёт исключительно к линиям сетки, игнорируя края соседних нот.");

            A("EditorMagneticWidth", Kind.Num, Stat.Ok, CatEditor,
              "Magnet zone width", "Ширина зоны магнита",
              "Width, in pixels, of the attraction zone around notes while dragging. Higher means a stronger magnet.",
              "Ширина зоны притяжения вокруг нот при перетаскивании, в пикселях. Больше — сильнее магнит.",
              min: "0", max: "100");

            A("EditorSnapTimeout", Kind.Num, Stat.Ok, CatEditor,
              "Pause between snaps", "Пауза между привязками",
              "Minimum interval, in milliseconds, between two snap events. Higher makes snapping calmer.",
              "Минимальный интервал (мс) между двумя срабатываниями привязки. Больше — привязка «спокойнее».",
              min: "0", max: "500");

            A("EditorResnapTimeoutOnMouseUp", Kind.Num, Stat.Ok, CatEditor,
              "Delay before re-snapping", "Пауза перед повторной привязкой",
              "How long, in milliseconds, the editor waits after you release the mouse before re-snapping.",
              "Сколько мс редактор ждёт после отпускания кнопки мыши перед повторной привязкой.",
              min: "0", max: "1000");

            A("EditorResnapRangeFactor", Kind.Num, Stat.Ok, CatEditor,
              "Re-snap radius", "Радиус повторной привязки",
              "Multiplier deciding which neighbouring notes get pulled into the re-snap.",
              "Множитель, определяющий, какие соседние ноты попадают под повторную привязку.",
              min: "1.0", max: "2.0");

            // ---------------------------------------------------------------
            // AUDIO & CPU
            // ---------------------------------------------------------------
            A("MaxAudioThreads", Kind.Num, Stat.Ok, CatAudio,
              "Maximum audio threads", "Максимум аудиопотоков",
              "Hard-caps the number of audio engine threads. -1 means auto-detect from the core count. Lowering it sometimes clears dropouts on CPUs with efficiency cores.",
              "Жёстко ограничивает число потоков аудиодвижка. -1 — определять автоматически по числу ядер. Иногда снижение числа потоков убирает дропауты на процессорах с E-ядрами.",
              def: "-1", min: "-1");

            A("RestrictAudioCalculationToPerformanceCores", Kind.Flag, Stat.New12, CatAudio,
              "Audio on performance cores only", "Считать звук только на P-ядрах",
              "Stops the audio engine from using efficiency (E) cores, keeping it on performance cores. On hybrid CPUs (Intel 12th gen and newer, Apple Silicon) this often removes instability and dropouts.",
              "Запрещает аудиодвижку использовать энергоэффективные (E) ядра, оставляя только производительные. На гибридных CPU (Intel 12-го поколения и новее, Apple Silicon) это часто убирает нестабильность и дропауты.");

            A("AdditionalLatencyForPlaythrough", Kind.Num, Stat.Ok, CatAudio,
              "Extra playthrough latency", "Доп. задержка сквозного канала",
              "Adds (or subtracts) latency in samples for input playthrough, so you can hand-compensate an external signal path.",
              "Добавляет (или вычитает) задержку в сэмплах для сквозного прослушивания входа. Позволяет вручную скомпенсировать задержку внешнего тракта.",
              min: "-1000", max: "1000");

            A("DriveEngineTiming", Kind.Flag, Stat.Ok, CatAudio,
              "Take engine timing from the driver", "Тайминг движка от драйвера",
              "The audio engine takes its timing from the audio interface driver instead of its own clock.",
              "Аудиодвижок берёт тайминг из информации драйвера аудиоинтерфейса, а не из собственных часов.");

            A("NoSCurves", Kind.Flag, Stat.Ok, CatAudio,
              "Linear fades instead of S-curves", "Линейные фейды вместо S-кривых",
              "Disables S-shaped curves in fades and crossfades — they become linear.",
              "Отключает S-образные кривые в фейдах/кроссфейдах — они становятся линейными.");

            A("SampleTimeOverflowScale", Kind.Num, Stat.Ok, CatAudio,
              "Sample-time overflow scale", "Масштаб переполнения sample time",
              "Scaling factor used when computing sample counter overflow. An internal parameter for extremely long sessions.",
              "Коэффициент масштабирования при расчёте переполнения счётчика сэмплов. Внутренний параметр для очень длинных сессий.",
              min: "0");

            A("UseFileSystemCacheForReading", Kind.Flag, Stat.Ok, CatAudio,
              "File system cache for reads", "Кэш ФС при чтении",
              "Allows the OS file cache to be used when reading samples. Can speed things up on spinning disks — or eat RAM with huge libraries.",
              "Разрешает использовать системный кэш файловой системы при чтении сэмплов. Может ускорить работу на HDD и, наоборот, съесть RAM на больших библиотеках.");

            A("UseFileSystemCacheForWriting", Kind.Flag, Stat.Ok, CatAudio,
              "File system cache for writes", "Кэш ФС при записи",
              "Allows the OS file cache to be used when writing audio to disk.",
              "Разрешает использовать системный кэш файловой системы при записи аудио на диск.");

            A("SharedMemStoragePath", Kind.Text, Stat.Dev, CatAudio,
              "Shared memory file path", "Путь к файлу разделяемой памяти",
              "Sets the path of the shared-memory file Live uses to exchange data between processes.",
              "Задаёт путь к файлу разделяемой памяти для обмена данными между процессами Live.");

            A("DisableAppleSiliconBurstWorkaround", Kind.Flag, Stat.Doc, CatAudio,
              "Revert Apple Silicon burst workaround", "Откат burst-обхода на Apple Silicon",
              "On Apple Silicon Macs, restores the energy-saving behaviour from before Live 11.3.25. May reduce reliability at higher buffer sizes.",
              "На Mac с Apple Silicon возвращает энергосберегающее поведение, которое было до Live 11.3.25. Может ухудшить стабильность на больших буферах.",
              plat: "macOS");

            A("AsioDisableMultiClient", Kind.Flag, Stat.Ok, CatAudio,
              "Disable multi-client ASIO", "Запретить мультиклиентский ASIO",
              "Stops several applications from holding the same ASIO driver at once. Cures conflicts and crashes with fussy drivers.",
              "Запрещает нескольким приложениям одновременно держать один ASIO-драйвер. Лечит конфликты и вылеты с капризными драйверами.",
              plat: "Windows");

            A("AsioNoClockSource", Kind.Flag, Stat.Ok, CatAudio,
              "Leave the ASIO clock source alone", "Не трогать источник клока ASIO",
              "Live does not try to pick a sync source in the ASIO driver — whatever the driver panel says is used.",
              "Live не пытается выбирать источник синхронизации в ASIO-драйвере — используется то, что выставлено в панели драйвера.",
              plat: "Windows");

            A("AsioNoSetSampleRate", Kind.Flag, Stat.Ok, CatAudio,
              "Don't set the sample rate", "Не задавать частоту дискретизации",
              "Live never switches the sample rate in the ASIO driver. Needed when an external device or another app owns the clock.",
              "Live не пытается переключать sample rate в ASIO-драйвере. Нужно, когда частоту задаёт внешнее устройство или другой софт.",
              plat: "Windows");

            A("AsioNoSampleRateCheck", Kind.Flag, Stat.Ok, CatAudio,
              "Don't verify the sample rate", "Не проверять частоту дискретизации",
              "Skips the sample-rate consistency check, working around false errors from some drivers.",
              "Отключает проверку соответствия частоты дискретизации. Обходит ложные ошибки у некоторых драйверов.",
              plat: "Windows");

            A("AsioSupportProcessNow", Kind.Flag, Stat.Ok, CatAudio,
              "ASIO Process Now support", "Поддержка ASIO Process Now",
              "Enables ASIO Process Now mode, which can lower latency on drivers that support it.",
              "Включает режим ASIO Process Now — на совместимых драйверах может снизить задержку.",
              plat: "Windows");

            // ---------------------------------------------------------------
            // MIDI
            // ---------------------------------------------------------------
            A("MidiEventThinning", Kind.Num, Stat.Ok, CatMidi,
              "Incoming MIDI event thinning", "Прореживание входящих MIDI-событий",
              "How hard incoming MIDI is thinned out (dense CC streams from controllers). 0 means no thinning, 4 is maximum.",
              "Насколько сильно прореживается поток входящих MIDI-сообщений (плотные CC от контроллеров). 0 — не прореживать, 4 — максимально.",
              min: "0", max: "4");

            A("NoMidiEventFiltering", Kind.Flag, Stat.Ok, CatMidi,
              "No MIDI event filtering", "Не фильтровать MIDI-события",
              "Disables filtering of incoming MIDI events entirely — Live takes the stream as it comes.",
              "Полностью отключает фильтрацию входящих MIDI-событий — Live принимает поток как есть.");

            A("NoMidiServer", Kind.Flag, Stat.Ok, CatMidi,
              "Disable the MIDI server", "Отключить MIDI-сервер",
              "Shuts Live's MIDI subsystem down completely. A diagnostic move: it tells you whether MIDI is behind a crash or a hang on startup.",
              "Полностью отключает MIDI-подсистему Live. Диагностическая мера: помогает понять, виноват ли MIDI в вылетах или зависании на старте.");

            A("SendSPPInArrangerLoops", Kind.Flag, Stat.Ok, CatMidi,
              "Send SPP on Arrangement loops", "SPP при зацикливании Arrangement",
              "Live sends a Song Position Pointer when jumping across an Arrangement loop, so external gear doesn't drift.",
              "Live шлёт Song Position Pointer при прыжке по лупу в Arrangement, чтобы внешние устройства не уезжали по позиции.");

            A("UseOwnGeneratedMidiTimeStamps", Kind.Flag, Stat.Ok, CatMidi,
              "Use Live's own MIDI timestamps", "Свои временные метки MIDI",
              "Live uses timestamps it generates itself instead of the driver's. Cures jitter with badly behaved MIDI interfaces.",
              "Live использует собственные временные метки вместо тех, что приходят от драйвера. Лечит джиттер на кривых MIDI-интерфейсах.");

            A("NoMidiFromReWire", Kind.Flag, Stat.Ok, CatMidi,
              "Ignore MIDI from ReWire", "Игнорировать MIDI из ReWire",
              "Live refuses MIDI coming from ReWire clients.",
              "Live не принимает MIDI от ReWire-клиентов.");

            A("ImpulseIgnoreOmega", Kind.Flag, Stat.Ok, CatMidi,
              "Impulse ignores note-off", "Impulse игнорирует note-off",
              "Impulse stops reacting to note-off for the matching preset, so the sample always plays out in full.",
              "Impulse перестаёт реагировать на note-off для соответствующего пресета — сэмпл всегда играет целиком.");

            // ---------------------------------------------------------------
            // PLUG-INS & DEVICES
            // ---------------------------------------------------------------
            A("_PluginAutoPopulateThreshold", Kind.Num, Stat.Doc, CatPlugins,
              "Plug-in parameter auto-populate threshold", "Порог авто-заполнения параметров плагина",
              "If a plug-in has no more parameters than this number, Live lists them all in the device's parameter list automatically (ready for automation and mapping). 128 always fills the list with up to 128 parameters.",
              "Если у плагина параметров не больше указанного числа, Live автоматически выводит их все в список параметров устройства (для автоматизации и маппинга). 128 — всегда заполнять список максимумом в 128 параметров.",
              def: "64", min: "1", max: "128");

            A("_EnsureKeyMessagesForPlugins", Kind.Flag, Stat.Doc, CatPlugins,
              "Forward keystrokes to VST plug-ins", "Пробрасывать клавиатуру в VST",
              "Fixes the case where keystrokes never reach a VST plug-in's window — the classic example being NI Reaktor.",
              "Решает проблему, когда нажатия клавиш не доходят до окна VST-плагина (классический пример — NI Reaktor).",
              plat: "Windows");

            A("NoVstGesturesRequired", Kind.Flag, Stat.Ok, CatPlugins,
              "Don't require VST automation gestures", "Не требовать VST-жестов автоматизации",
              "Live stops waiting for proper begin/end edit gestures from a plug-in when recording automation. Fixes plug-ins that never write automation.",
              "Live не ждёт от плагина корректных begin/end edit gesture при записи автоматизации. Лечит плагины, которые не пишут автоматизацию.");

            A("Halion3BugWorkaround", Kind.Flag, Stat.Ok, CatPlugins,
              "Halion 3 bug workaround", "Обход бага Halion 3",
              "Enables a workaround for a known Steinberg Halion 3 defect.",
              "Включает обходной путь для известной ошибки Steinberg Halion 3.");

            // ---------------------------------------------------------------
            // BROWSER & FILES
            // ---------------------------------------------------------------
            A("_Feature.Browser.AsyncLoading", Kind.Flag, Stat.New12, CatBrowser,
              "Asynchronous browser loading", "Асинхронная загрузка браузера",
              "The browser loads its contents in the background instead of blocking the interface. A real help with large libraries and network drives.",
              "Браузер подгружает содержимое в фоне, не подвешивая интерфейс. Ощутимо помогает при больших библиотеках и сетевых дисках.");

            A("LogFolderConfigErrors", Kind.Flag, Stat.Ok, CatBrowser,
              "Log folder configuration errors", "Логировать ошибки конфигурации папок",
              "Writes errors about library and project folder configuration into Log.txt.",
              "Пишет в Log.txt ошибки, связанные с настройкой расположения папок библиотеки и проектов.");

            A("EventRecorderTempDir", Kind.Text, Stat.Dev, CatBrowser,
              "Event Recorder temp folder", "Временная папка Event Recorder",
              "Sets the folder for the internal Event Recorder's temporary files.",
              "Задаёт папку для временных файлов внутреннего Event Recorder.");

            A("MemoryLeaksFile", Kind.Text, Stat.Dev, CatBrowser,
              "Memory leak report file", "Файл отчёта об утечках памяти",
              "Path of the file Live writes memory-leak information to.",
              "Путь к файлу, куда Live запишет информацию об утечках памяти.");

            // ---------------------------------------------------------------
            // CONTROLLERS & PUSH
            // ---------------------------------------------------------------
            A("DontCombineAPCs", Kind.Flag, Stat.Doc, CatCtrl,
              "Don't combine multiple APCs", "Не объединять несколько APC",
              "Turns off APC combination mode: the session rings of several controllers stop aligning and syncing, so each can be moved independently.",
              "Отключает режим объединения APC: session-рамки нескольких контроллеров перестают выравниваться и синхронизироваться, каждую можно двигать независимо.");

            A("Push2UseLegacyScript", Kind.Flag, Stat.New12, CatCtrl,
              "Push 2: legacy script", "Push 2: старый скрипт",
              "Puts Push 2 back on its pre-Live-12 control script. Needed if the new logic breaks a familiar workflow or third-party tweaks.",
              "Возвращает Push 2 к прежнему (до Live 12) управляющему скрипту. Нужно, если новая логика ломает привычный воркфлоу или сторонние доработки.");

            A("ControlSurfaceDisplayUpdateRate", Kind.Num, Stat.Ok, CatCtrl,
              "Control surface display refresh rate", "Частота обновления дисплеев контроллеров",
              "Refresh interval, in milliseconds, for control surface displays. Higher means less load on the CPU and the MIDI port.",
              "Интервал обновления экранов управляющих поверхностей в миллисекундах. Больше значение — меньше нагрузка на CPU и MIDI-порт.",
              min: "1", max: "100");

            A("DrumPadSelectionDelay", Kind.Num, Stat.Ok, CatCtrl,
              "Drum pad selection delay", "Задержка выбора drum-пэда",
              "Delay, in milliseconds, before hitting a pad moves the selection to it. Stops the selection from jumping around while you play.",
              "Задержка (мс) перед тем, как удар по пэду переключит выделение на соответствующий пэд. Спасает от того, что выделение прыгает при игре.",
              min: "0", max: "1000");

            A("LogRemoteScriptCapabilityInfo", Kind.Flag, Stat.Ok, CatCtrl,
              "Log remote script capabilities", "Логировать возможности remote-скриптов",
              "Writes information about loaded MIDI Remote Scripts' capabilities into Log.txt. Useful when writing your own script.",
              "Пишет в Log.txt информацию о возможностях загруженных MIDI Remote Scripts. Полезно при разработке своего скрипта.");

            A("AutoShowPythonShellOnError", Kind.Flag, Stat.Dev, CatCtrl,
              "Open the Python shell on error", "Открывать Python-консоль при ошибке",
              "Pops open the Python console whenever a MIDI Remote Script throws. A script developer's tool.",
              "При ошибке в MIDI Remote Script автоматически открывается Python-консоль. Инструмент разработчика скриптов.");

            A("LogPseudoMidiOutDataSizeSent", Kind.Flag, Stat.Dev, CatCtrl,
              "Log MIDI out data size", "Логировать объём MIDI-out",
              "Logs the size of data sent through the MIDI output.",
              "Пишет в лог размер данных, отправленных через MIDI-выход.");

            // ---------------------------------------------------------------
            // ReWire
            // ---------------------------------------------------------------
            A("ReWireChannels", Kind.Num, Stat.Ok, CatRewire,
              "ReWire channel count", "Число каналов ReWire",
              "How many ReWire channels Live offers.",
              "Сколько каналов ReWire предоставляет Live.",
              def: "64", min: "2");

            A("ReWireLogic", Kind.Num, Stat.Ok, CatRewire,
              "ReWire compatibility mode", "Режим совместимости ReWire",
              "Switches the ReWire logic variant for compatibility with a particular host.",
              "Переключает вариант логики ReWire для совместимости с конкретным хостом.",
              min: "0", max: "3");

            A("ReWireSonar", Kind.Flag, Stat.Ok, CatRewire,
              "Sonar compatibility", "Совместимость с Sonar",
              "Enables the ReWire mode compatible with Cakewalk Sonar.",
              "Включает режим ReWire, совместимый с Cakewalk Sonar.");

            // ---------------------------------------------------------------
            // DIAGNOSTICS & LOGS
            // ---------------------------------------------------------------
            A("DisableAutoBugReporting", Kind.Flag, Stat.Ok, CatDiag,
              "Disable automatic bug reports", "Отключить авто-отправку отчётов",
              "Live stops sending crash reports to Ableton automatically.",
              "Live перестаёт автоматически отправлять отчёты о сбоях в Ableton.");

            A("DebugKeys", Kind.Flag, Stat.Dev, CatDiag,
              "Debug hotkeys", "Отладочные горячие клавиши",
              "Enables internal debug key combinations, including a display of incoming MIDI messages.",
              "Включает служебные отладочные комбинации клавиш, в том числе показ входящих MIDI-сообщений.");

            A("StackTraces", Kind.Flag, Stat.Dev, CatDiag,
              "Stack traces to the console", "Стек вызовов в консоль",
              "Prints a call stack whenever an error occurs.",
              "При ошибках печатает стек вызовов.");

            A("Trace", Kind.Flag, Stat.Dev, CatDiag,
              "Event tracing", "Трассировка событий",
              "Enables verbose application event tracing. Bloats Log.txt considerably.",
              "Включает подробную трассировку событий приложения. Сильно раздувает Log.txt.");

            A("LogTimeConversionVariation", Kind.Flag, Stat.Dev, CatDiag,
              "Log time conversion drift", "Логировать расхождения преобразования времени",
              "Logs discrepancies when converting between musical and absolute time.",
              "Пишет в лог расхождения при преобразованиях музыкального и абсолютного времени.");

            A("MemoryBasedUndo", Kind.Flag, Stat.Ok, CatDiag,
              "In-memory undo", "Undo в памяти",
              "Keeps undo history in RAM rather than on disk. Faster, but it costs memory.",
              "История отмены хранится в оперативной памяти, а не на диске. Быстрее, но ест RAM.");

            A("SuppressCheckSynchronousInvariant", Kind.Flag, Stat.Dev, CatDiag,
              "Suppress synchronous invariant checks", "Отключить синхронные проверки инвариантов",
              "Removes part of the internal state validation. Debugging only.",
              "Убирает часть внутренних проверок состояния. Только для отладки.");

            A("SetAssertMode", Kind.Choice, Stat.Dev, CatDiag,
              "Assertion handling mode", "Режим обработки assert'ов",
              "What Live does when an internal assertion fires: default behaves as usual, ignore continues silently, debug breaks into the debugger, release uses release-build behaviour.",
              "Что делает Live при срабатывании внутренней проверки: default — как обычно, ignore — молча продолжить, debug — уйти в отладчик, release — поведение релизной сборки.",
              choices: "default|ignore|debug|release");

            A("UseDebugPrefs", Kind.Flag, Stat.Dev, CatDiag,
              "Debug preferences", "Отладочные настройки",
              "Live uses a separate debug preferences file instead of the normal one.",
              "Live использует отдельный отладочный файл настроек вместо обычного.");

            A("CheckAllLanguageFiles", Kind.Flag, Stat.Dev, CatDiag,
              "Check all language files", "Проверить все языковые файлы",
              "Live validates every localisation file for errors at startup.",
              "Live проверяет все файлы локализации на ошибки при запуске.");

            A("WriteLanguageCommentFile", Kind.Flag, Stat.Dev, CatDiag,
              "Write localisation comment file", "Записать файл комментариев локализации",
              "Produces a file of comments for localisation strings. A translator's tool.",
              "Создаёт файл с комментариями к строкам локализации. Инструмент переводчиков.");

            A("DumpUsfLibOutput", Kind.Flag, Stat.Dev, CatDiag,
              "Dump UsfLib output", "Дамп вывода UsfLib",
              "Saves the output of the internal UsfLib library.",
              "Сохраняет вывод внутренней библиотеки UsfLib.");

            A("ChallengeMethod", Kind.Num, Stat.Dev, CatDiag,
              "Authorisation challenge method", "Метод авторизационного запроса",
              "Picks how the user authorisation challenge is performed.",
              "Выбирает способ проверки авторизации пользователя.",
              min: "0", max: "2");

            A("UpdateTest", Kind.Flag, Stat.Dev, CatDiag,
              "Update test mode", "Тестовый режим обновления",
              "An internal option for testing the update mechanism.",
              "Служебная опция проверки механизма обновлений.");

            // ---------------------------------------------------------------
            // INTERNAL / TESTS
            // ---------------------------------------------------------------
            A("AcceptanceTestsIgnoreAssertions", Kind.Flag, Stat.Dev, CatDev,
              "Acceptance tests: ignore assertions", "Приёмочные тесты: игнорировать assert'ы",
              "Disables part of the internal checks during Ableton's acceptance tests.",
              "Отключает часть внутренних проверок в приёмочных тестах Ableton.");

            A("AcceptanceTestsPort", Kind.Num, Stat.Dev, CatDev,
              "Acceptance tests port", "Порт приёмочных тестов",
              "The TCP port Live listens on for acceptance test commands.",
              "TCP-порт, на котором Live слушает команды приёмочных тестов.",
              def: "16720", min: "1", max: "65535");

            A("UnitTestsAssertOnFailure", Kind.Flag, Stat.Dev, CatDev,
              "Unit tests: assert on failure", "Unit-тесты: assert при падении",
              "Breaks execution when a unit test fails.",
              "Прерывает выполнение при провале юнит-теста.");

            A("UnitTestsDontCleanup", Kind.Flag, Stat.Dev, CatDev,
              "Unit tests: skip cleanup", "Unit-тесты: не убирать за собой",
              "Leaves temporary files in place after a test run.",
              "Не удаляет временные файлы после прогона тестов.");

            A("UnitTestsLetDummyTestPass", Kind.Flag, Stat.Dev, CatDev,
              "Unit tests: let dummies pass", "Unit-тесты: пропускать заглушки",
              "Allows placeholder tests to count as passing.",
              "Позволяет фиктивным тестам считаться пройденными.");

            A("UnitTestsQuitAfter", Kind.Flag, Stat.Dev, CatDev,
              "Unit tests: quit when done", "Unit-тесты: выйти после прогона",
              "Live closes as soon as the tests finish.",
              "Live закрывается сразу после завершения тестов.");

            A("UnitTestsReportFormat", Kind.Choice, Stat.Dev, CatDev,
              "Unit tests: report format", "Unit-тесты: формат отчёта",
              "Format of the unit test run report.",
              "Формат отчёта о прогоне юнит-тестов.",
              choices: "plain|xml|json");

            A("UnitTestsRunDefaultOff", Kind.Flag, Stat.Dev, CatDev,
              "Unit tests: run disabled ones", "Unit-тесты: гонять отключённые",
              "Also runs tests that are switched off by default.",
              "Запускает и те тесты, что по умолчанию выключены.");

            A("UnitTestsTraces", Kind.Flag, Stat.Dev, CatDev,
              "Unit tests: traces", "Unit-тесты: трассировка",
              "Prints a verbose trace of the test run.",
              "Печатает подробную трассировку прогона тестов.");

            // ---------------------------------------------------------------
            // DEPRECATED — removed in Live 11, inert in Live 12
            // ---------------------------------------------------------------
            A("ShowToolsMenu", Kind.Flag, Stat.Legacy, CatLegacy,
              "Tools menu", "Меню Tools",
              "Used to reveal Ableton's hidden Tools menu of internal commands. Removed in Live 11.",
              "Открывало скрытое служебное меню Tools с внутренними командами Ableton. Удалено в Live 11.",
              note: "Verified on Live 12.4.3: rejected with an \"unknown option\" dialog.",
              noteRu: "Проверено на Live 12.4.3: отвергается с окном «unknown option».");

            A("ReWireMasterOff", Kind.Flag, Stat.Legacy, CatLegacy,
              "Disable ReWire master", "Отключить ReWire-мастер",
              "Used to stop Live acting as a ReWire master. Removed in Live 11.",
              "Запрещало Live выступать в роли ReWire-мастера. Удалено в Live 11.");

            A("ShowPeakCpuLoad", Kind.Flag, Stat.Legacy, CatLegacy,
              "Peak CPU load", "Пиковая загрузка CPU",
              "Used to show peak rather than average CPU load. Removed in Live 11 — since Live 11 it's a right-click option on the CPU meter.",
              "Показывало пиковое, а не среднее значение загрузки CPU. Удалено в Live 11 (в Live 11+ переключается в контекстном меню индикатора CPU).");

            A("AbsoluteMouseMode", Kind.Flag, Stat.Legacy, CatLegacy,
              "Absolute mouse mode", "Абсолютный режим мыши",
              "Absolute cursor positioning when dragging knobs. Removed in Live 11.",
              "Абсолютное позиционирование курсора при работе с ручками. Удалено в Live 11.");

            A("AudioDropOutDisplay", Kind.Flag, Stat.Legacy, CatLegacy,
              "Dropout indicator", "Индикатор дропаутов",
              "Used to display an audio dropout indicator. Removed in Live 11.",
              "Показывало индикатор аудио-дропаутов. Удалено в Live 11.");

            A("AudioNoThreadReNew", Kind.Flag, Stat.Legacy, CatLegacy,
              "Don't recreate audio threads", "Не пересоздавать аудиопотоки",
              "Prevented audio threads from being recreated. Removed in Live 11.",
              "Запрещало пересоздание аудиопотоков. Удалено в Live 11.");

            A("BrowserPageSize", Kind.Num, Stat.Legacy, CatLegacy,
              "Browser page size", "Размер страницы браузера",
              "Number of items per browser page. Removed in Live 11.",
              "Число элементов на странице браузера. Удалено в Live 11.");

            A("CopySampleFiles", Kind.Flag, Stat.Legacy, CatLegacy,
              "Copy sample files", "Копировать сэмплы",
              "Controlled copying of samples into the project. Removed in Live 11.",
              "Управляло копированием сэмплов в проект. Удалено в Live 11.");

            A("DisableSchedulerPerDevice", Kind.Flag, Stat.Legacy, CatLegacy,
              "Disable per-device scheduler", "Отключить планировщик по устройствам",
              "Disabled separate processing scheduling per device. Removed in Live 11.",
              "Отключало отдельное планирование обработки для каждого устройства. Удалено в Live 11.");

            A("DisableUpdateOverviews", Kind.Flag, Stat.Legacy, CatLegacy,
              "Don't redraw waveform overviews", "Не обновлять обзорные волны",
              "Disabled repainting of waveform overviews. Removed in Live 11.",
              "Отключало перерисовку обзорных представлений формы волны. Удалено в Live 11.");

            A("DontUseHardcodedLibraryPath", Kind.Flag, Stat.Legacy, CatLegacy,
              "Don't use the hardcoded library path", "Не использовать фиксированный путь библиотеки",
              "Disabled the hardcoded library path. Removed in Live 11.",
              "Отключало жёстко заданный путь к библиотеке. Удалено в Live 11.");

            A("DrawDirectlyToScreen", Kind.Flag, Stat.Legacy, CatLegacy,
              "Draw straight to the screen", "Рисовать напрямую на экран",
              "Drawing that bypassed the offscreen buffer. Removed in Live 11.",
              "Отрисовка минуя внеэкранный буфер. Удалено в Live 11.");

            A("EditorFoldMore", Kind.Flag, Stat.Legacy, CatLegacy,
              "Extended fold in the editor", "Расширенный fold в редакторе",
              "Extra row folding in the MIDI editor. Removed in Live 11.",
              "Дополнительное сворачивание строк в MIDI-редакторе. Удалено в Live 11.");

            A("EnableGMIForVideo", Kind.Flag, Stat.Legacy, CatLegacy,
              "GMI for video", "GMI для видео",
              "Enabled an alternative video engine. Removed in Live 11.",
              "Включало альтернативный движок для видео. Удалено в Live 11.");

            A("EnablePseudoDevice", Kind.Flag, Stat.Legacy, CatLegacy,
              "Pseudo device", "Псевдоустройство",
              "Enabled an internal test device. Removed in Live 11.",
              "Включало внутреннее тестовое устройство. Удалено в Live 11.");

            A("ExtendedDeviceOptions", Kind.Flag, Stat.Legacy, CatLegacy,
              "Extended device options", "Расширенные опции устройств",
              "Exposed extra parameters on built-in devices. Removed in Live 11.",
              "Показывало дополнительные параметры встроенных устройств. Удалено в Live 11.");

            A("ForceDirectDraw", Kind.Flag, Stat.Legacy, CatLegacy,
              "Force DirectDraw", "Принудительный DirectDraw",
              "Forced rendering through DirectDraw. Removed in Live 11.",
              "Форсировало отрисовку через DirectDraw. Удалено в Live 11.", plat: "Windows");

            A("ForceGDI", Kind.Flag, Stat.Legacy, CatLegacy,
              "Force GDI", "Принудительный GDI",
              "Forced rendering through GDI. Removed in Live 11.",
              "Форсировало отрисовку через GDI. Удалено в Live 11.", plat: "Windows");

            A("HotKeyForSet", Kind.Text, Stat.Legacy, CatLegacy,
              "Hotkey for loading a set", "Хоткей для загрузки сета",
              "Assigned a shortcut for loading a Live Set. Removed in Live 11.",
              "Назначало горячую клавишу для загрузки Live Set. Удалено в Live 11.");

            A("IgnorePrefs", Kind.Flag, Stat.Legacy, CatLegacy,
              "Ignore preferences", "Игнорировать настройки",
              "Started Live with default preferences. Removed in Live 11.",
              "Запуск с настройками по умолчанию. Удалено в Live 11.");

            A("InitialDocument", Kind.Text, Stat.Legacy, CatLegacy,
              "Startup document", "Стартовый документ",
              "Path of the set opened at launch. Removed in Live 11.",
              "Путь к сету, открываемому при запуске. Удалено в Live 11.");

            A("LocalFilesDir", Kind.Text, Stat.Legacy, CatLegacy,
              "Local files folder", "Папка локальных файлов",
              "Path to Live's local files folder. Removed in Live 11.",
              "Путь к папке локальных файлов Live. Удалено в Live 11.");

            A("LogPluginPerformance", Kind.Flag, Stat.Legacy, CatLegacy,
              "Log plug-in performance", "Логировать производительность плагинов",
              "Logged per-plug-in load statistics. Removed in Live 11.",
              "Писало в лог статистику по нагрузке от плагинов. Удалено в Live 11.");

            A("NoMidiJitterCorrection", Kind.Flag, Stat.Legacy, CatLegacy,
              "No MIDI jitter correction", "Без коррекции MIDI-джиттера",
              "Disabled smoothing of incoming MIDI jitter. Removed in Live 11.",
              "Отключало сглаживание джиттера входящего MIDI. Удалено в Live 11.");

            A("Patch", Kind.Text, Stat.Legacy, CatLegacy,
              "Patch", "Патч",
              "An internal patch-application option. Removed in Live 11.",
              "Служебная опция применения патча. Удалено в Live 11.");

            A("ReportZeroLengthClips", Kind.Flag, Stat.Legacy, CatLegacy,
              "Report zero-length clips", "Сообщать о клипах нулевой длины",
              "Diagnostics for zero-length clips. Removed in Live 11.",
              "Диагностика клипов нулевой длины. Удалено в Live 11.");

            A("Sandbox", Kind.Flag, Stat.Legacy, CatLegacy,
              "Sandbox", "Песочница",
              "Ran components in an isolated mode. Removed in Live 11.",
              "Запуск компонентов в изолированном режиме. Удалено в Live 11.");

            A("SetSatisfactionSurveyActive", Kind.Flag, Stat.Legacy, CatLegacy,
              "Satisfaction survey", "Опрос удовлетворённости",
              "Controlled the internal survey prompt. Removed in Live 11.",
              "Управляло показом внутреннего опроса. Удалено в Live 11.");

            A("SoundManagerBufferSize", Kind.Num, Stat.Legacy, CatLegacy,
              "Sound Manager buffer size", "Размер буфера Sound Manager",
              "Buffer size of the legacy sound subsystem. Removed in Live 11.",
              "Размер буфера устаревшей звуковой подсистемы. Удалено в Live 11.");

            A("TestDir", Kind.Text, Stat.Legacy, CatLegacy,
              "Test folder", "Папка тестов",
              "Path to the test data folder. Removed in Live 11.",
              "Путь к папке тестовых данных. Удалено в Live 11.");

            A("TestLibraryRootDir", Kind.Text, Stat.Legacy, CatLegacy,
              "Test library root", "Корень тестовой библиотеки",
              "Path to the test library root. Removed in Live 11.",
              "Путь к корню тестовой библиотеки. Удалено в Live 11.");

            A("UndoSplashScreenFixOsX", Kind.Flag, Stat.Legacy, CatLegacy,
              "Undo the splash screen fix (OS X)", "Откат фикса сплэш-экрана (OS X)",
              "Reverted a macOS splash screen fix. Removed in Live 11.",
              "Отменяло исправление сплэш-экрана на macOS. Удалено в Live 11.", plat: "macOS");

            A("UndoStepsAtMouseUp", Kind.Flag, Stat.Legacy, CatLegacy,
              "Undo step on mouse up", "Шаг undo по отпусканию мыши",
              "Committed an undo step when the mouse button was released. Removed in Live 11.",
              "Фиксировало шаг отмены в момент отпускания кнопки мыши. Удалено в Live 11.");

            A("ViewIdCheckFrequency", Kind.Num, Stat.Legacy, CatLegacy,
              "View ID check frequency", "Частота проверки ID представлений",
              "An internal view identifier check. Removed in Live 11.",
              "Служебная проверка идентификаторов вью. Удалено в Live 11.");

            A("VirtualAudioIn", Kind.Flag, Stat.Legacy, CatLegacy,
              "Virtual audio input", "Виртуальный аудиовход",
              "Enabled a virtual audio input for tests. Removed in Live 11.",
              "Включало виртуальный аудиовход для тестов. Удалено в Live 11.");

            A("CheckCast", Kind.Flag, Stat.Legacy, CatLegacy,
              "Type cast checking", "Проверка приведения типов",
              "An internal type cast check. Removed in Live 11.",
              "Внутренняя проверка приведения типов. Удалено в Live 11.");

            A("CheckForUnlinkedComponents", Kind.Flag, Stat.Legacy, CatLegacy,
              "Find unlinked components", "Поиск несвязанных компонентов",
              "Internal diagnostics of the component graph. Removed in Live 11.",
              "Внутренняя диагностика графа компонентов. Удалено в Live 11.");

            A("DirectAssert", Kind.Flag, Stat.Legacy, CatLegacy,
              "Direct assertions", "Прямые assert'ы",
              "Internal assertion handling. Removed in Live 11.",
              "Внутренняя обработка assert'ов. Удалено в Live 11.");

            A("DontLogExceptions", Kind.Flag, Stat.Legacy, CatLegacy,
              "Don't log exceptions", "Не логировать исключения",
              "Stopped exceptions being written to the log. Removed in Live 11.",
              "Отключало запись исключений в лог. Удалено в Live 11.");

            A("DumpAddControlled", Kind.Flag, Stat.Legacy, CatLegacy,
              "Dump added controls", "Дамп добавленных контролов",
              "An internal dump. Removed in Live 11.",
              "Внутренний дамп. Удалено в Live 11.");

            A("DumpAppViewOnQuit", Kind.Flag, Stat.Legacy, CatLegacy,
              "Dump the view on quit", "Дамп вью при выходе",
              "An internal dump when Live closes. Removed in Live 11.",
              "Внутренний дамп при закрытии Live. Удалено в Live 11.");

            A("DumpDocumentOnQuit", Kind.Flag, Stat.Legacy, CatLegacy,
              "Dump the document on quit", "Дамп документа при выходе",
              "An internal dump of the set when Live closes. Removed in Live 11.",
              "Внутренний дамп сета при закрытии Live. Удалено в Live 11.");

            A("AcceptanceTestsRecording", Kind.Flag, Stat.Legacy, CatLegacy,
              "Acceptance test recording", "Запись приёмочных тестов",
              "Internal recording of test scenarios. Removed in Live 11.",
              "Служебная запись сценариев тестов. Удалено в Live 11.");

            A("EventRecorderIgnoreCache", Kind.Flag, Stat.Legacy, CatLegacy,
              "Event Recorder: ignore cache", "Event Recorder: игнорировать кэш",
              "An internal Event Recorder option. Removed in Live 11.",
              "Служебная опция Event Recorder. Удалено в Live 11.");

            A("EventRecorderPauseMs", Kind.Num, Stat.Legacy, CatLegacy,
              "Event Recorder: pause, ms", "Event Recorder: пауза, мс",
              "An internal Event Recorder option. Removed in Live 11.",
              "Служебная опция Event Recorder. Удалено в Live 11.");

            A("EventRecorderPlaybackDir", Kind.Text, Stat.Legacy, CatLegacy,
              "Event Recorder: playback folder", "Event Recorder: папка воспроизведения",
              "An internal Event Recorder option. Removed in Live 11.",
              "Служебная опция Event Recorder. Удалено в Live 11.");

            A("EventRecorderRecordingDir", Kind.Text, Stat.Legacy, CatLegacy,
              "Event Recorder: recording folder", "Event Recorder: папка записи",
              "An internal Event Recorder option. Removed in Live 11.",
              "Служебная опция Event Recorder. Удалено в Live 11.");

            A("UnitTestsRunStreamServiceTests", Kind.Flag, Stat.Legacy, CatLegacy,
              "Unit tests: stream service tests", "Unit-тесты: тесты стриминга",
              "An internal test option. Removed in Live 11.",
              "Служебная опция тестов. Удалено в Live 11.");
        }
    }
}
