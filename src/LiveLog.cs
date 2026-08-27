using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>Чем кончилась одна попытка открыть сет.</summary>
    public enum LoadResult
    {
        Running,    // блок начался и ещё пишется — Live прямо сейчас грузит
        Loaded,     // «Loaded document was created by …» — документ дочитан целиком
        Broke       // блок оборвался: дальше в журнале другая попытка или конец файла
    }

    /// <summary>Один плагин, который Live поднимала при открытии сета.</summary>
    public sealed class PluginLoad
    {
        public string Name = "";
        public PluginKind Kind;
        public bool Restored;      // видели парное «Restored: имя»
        public bool Failed;        // видели «Restore N failed: имя» — плагин отказался, но Live выжила
        public DateTime At;

        public string Format { get { return Kind == PluginKind.Vst3 ? "VST3" : "VST2"; } }

        /// <summary>Ни ответа, ни ошибки: Live вошла в плагин и оттуда не вернулась.</summary>
        public bool Hung { get { return !Restored && !Failed; } }
    }

    /// <summary>
    /// Блок журнала от «Loading document …» до следующего такого же — то есть ровно
    /// одна попытка открыть один сет, со списком поднятых по дороге плагинов.
    /// </summary>
    public sealed class LoadAttempt
    {
        public string LogPath = "";
        public string LiveVersion = "";      // имя папки настроек: «Live 12.4.3»
        public string Document = "";         // путь так, как его записала Live
        public DateTime Started;
        public DateTime LastEvent;
        public string CreatedBy = "";        // «Ableton Live 11.2.6» из строки Loaded document
        public LoadResult Result = LoadResult.Running;

        public readonly List<PluginLoad> Plugins = new List<PluginLoad>();

        /// <summary>
        /// Плагин, на котором всё оборвалось. Это обязательно ПОСЛЕДНЯЯ запись блока и
        /// обязательно без пары: Live умерла внутри его кода, не успев дописать журнал,
        /// поэтому после него в блоке физически ничего нет.
        ///
        /// Именно «последняя», а не «любая без пары». Без пары запись остаётся и когда
        /// плагин представился одним именем, а отчитался другим:
        ///
        ///     info: VST3: Going to restore: SpaceCarver
        ///     info: VST3: plugin processor successfully loaded: Blindspot Audio 'Oppressor'
        ///     info: VST3: Restored: Oppressor
        ///
        /// Так ведут себя плагины одной сборки с общим префиксом класса (в журнале с этой
        /// машины таких шесть штук, и все — в сетах, которые прекрасно открылись). Считать
        /// их зависшими значит обвинить исправный плагин, а расследование начинается
        /// именно с этого имени.
        /// </summary>
        public PluginLoad Hung
        {
            get
            {
                if (Result == LoadResult.Loaded || Plugins.Count == 0) return null;
                PluginLoad last = Plugins[Plugins.Count - 1];
                return last.Hung ? last : null;
            }
        }

        public int RestoredCount
        {
            get { int n = 0; foreach (PluginLoad p in Plugins) if (p.Restored) n++; return n; }
        }

        public List<PluginLoad> Failures
        {
            get
            {
                List<PluginLoad> r = new List<PluginLoad>();
                foreach (PluginLoad p in Plugins) if (p.Failed) r.Add(p);
                return r;
            }
        }
    }

    /// <summary>
    /// Журнал самой Live: %APPDATA%\Ableton\Live &lt;версия&gt;\Preferences\Log.txt.
    ///
    /// Это тот редкий случай, когда разбираться не нужно вовсе — Live сама пишет всё,
    /// что нам требуется, и пишет до того, как упасть:
    ///
    ///     info: Loading document "E:\...\big black.als"
    ///     info: VST3: Going to restore: soothe2
    ///     info: VST3: Restored: soothe2
    ///     info: Loaded document was created by Ableton Live 11.2.6
    ///
    /// Если Live умирает внутри плагина, последняя строка блока — «Going to restore»
    /// без пары. Это и есть имя виновника, без единого запуска и без бинарного поиска.
    /// В журнале с этой машины 663 «Going to restore» против 563 «Restored»: сотня
    /// оборванных загрузок, каждая — готовый диагноз.
    ///
    /// Журнал накопительный и не переписывается между запусками Live, поэтому попытку
    /// недельной давности видно так же, как сегодняшнюю.
    /// </summary>
    public static class LiveLog
    {
        const string InfoMark = ": info: ";
        const string ErrorMark = ": error: ";

        const string LoadingMark = "Loading document \"";
        const string LoadedMark = "Loaded document was created by ";

        /// <summary>Журналы всех установок Live — от той, что писалась последней, к старым.</summary>
        public static List<LiveLogFile> Files()
        {
            List<LiveLogFile> found = new List<LiveLogFile>();
            try
            {
                string root = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Ableton");
                if (!Directory.Exists(root)) return found;

                foreach (string dir in Directory.GetDirectories(root))
                {
                    string log = Path.Combine(dir, Path.Combine("Preferences", "Log.txt"));
                    if (File.Exists(log)) found.Add(new LiveLogFile(log, Path.GetFileName(dir)));
                }
                found.Sort(delegate (LiveLogFile a, LiveLogFile b)
                { return b.Written.CompareTo(a.Written); });
            }
            catch (Exception ex) { Diag.Fail("livelog: enumerate", ex); }
            return found;
        }

        /// <summary>
        /// Самая свежая попытка открыть именно этот сет — по всем установленным версиям
        /// Live сразу. Версий на машине обычно несколько, а какая из них ломалась на
        /// проекте, пользователь не помнит и знать не обязан.
        /// </summary>
        public static LoadAttempt LastAttempt(string alsPath)
        {
            LoadAttempt best = null;
            foreach (LiveLogFile f in Files())
            {
                LoadAttempt a = f.LastAttempt(alsPath);
                if (a == null) continue;
                if (best == null || a.Started > best.Started) best = a;
            }
            return best;
        }

        // ------------------------------------------------------------------- разбор

        /// <summary>
        /// Разбирает уже прочитанный кусок журнала в попытки открытия. Отдельный метод,
        /// а не приватная кишка LiveLogFile: так его можно позвать и на хвосте файла при
        /// слежении, и на файле целиком при разовой диагностике.
        /// </summary>
        internal static List<LoadAttempt> Parse(IEnumerable<string> lines, string logPath, string version)
        {
            List<LoadAttempt> attempts = new List<LoadAttempt>();
            LoadAttempt cur = null;

            foreach (string raw in lines)
            {
                if (raw == null || raw.Length == 0) continue;

                DateTime at;
                string text = Payload(raw, out at);
                if (text == null) continue;   // продолжение многострочной записи — там маркеров нет

                if (text.StartsWith(LoadingMark, StringComparison.Ordinal))
                {
                    // Новый блок закрывает предыдущий: если тот не успел сказать
                    // «Loaded document», значит он и не загрузился.
                    Close(cur);
                    cur = new LoadAttempt();
                    cur.LogPath = logPath;
                    cur.LiveVersion = version;
                    cur.Document = Quoted(text, LoadingMark.Length);
                    cur.Started = cur.LastEvent = at;
                    attempts.Add(cur);
                    continue;
                }

                if (cur == null) continue;
                cur.LastEvent = at;

                if (text.StartsWith(LoadedMark, StringComparison.Ordinal))
                {
                    cur.CreatedBy = text.Substring(LoadedMark.Length).Trim();
                    cur.Result = LoadResult.Loaded;
                    continue;
                }

                PluginKind kind;
                string rest = AfterVstTag(text, out kind);
                if (rest == null) continue;

                if (rest.StartsWith("Going to restore: ", StringComparison.Ordinal))
                {
                    PluginLoad p = new PluginLoad();
                    p.Kind = kind;
                    p.Name = rest.Substring("Going to restore: ".Length).Trim();
                    p.At = at;
                    cur.Plugins.Add(p);
                }
                else if (rest.StartsWith("Restored: ", StringComparison.Ordinal))
                {
                    Pending(cur, rest.Substring("Restored: ".Length).Trim(), true, false);
                }
                else if (rest.StartsWith("Restore ", StringComparison.Ordinal))
                {
                    // «Restore 1 failed: Dist COLDFIRE» — номер попытки нам не нужен.
                    int failed = rest.IndexOf(" failed: ", StringComparison.Ordinal);
                    if (failed > 0)
                        Pending(cur, rest.Substring(failed + " failed: ".Length).Trim(), false, true);
                }
            }

            return attempts;
        }

        /// <summary>
        /// Блок кончился, а «Loaded document» так и не было — значит оборвался. Пока блок
        /// последний в файле, это ещё может быть просто «Live грузит прямо сейчас», и
        /// такой вердикт выносит уже вызывающий (см. LiveLogFile.Finish).
        /// </summary>
        static void Close(LoadAttempt a)
        {
            if (a != null && a.Result == LoadResult.Running) a.Result = LoadResult.Broke;
        }

        /// <summary>
        /// Закрыть последнюю незакрытую запись с этим именем. По имени, а не «последнюю
        /// вообще»: у Live бывают вложенные восстановления (плагин внутри стойки), и
        /// тогда порядок закрытия не совпадает с порядком открытия.
        /// </summary>
        static void Pending(LoadAttempt a, string name, bool restored, bool failed)
        {
            for (int i = a.Plugins.Count - 1; i >= 0; i--)
            {
                PluginLoad p = a.Plugins[i];
                if (!p.Hung) continue;
                if (!string.Equals(p.Name, name, StringComparison.Ordinal)) continue;
                p.Restored = restored;
                p.Failed = failed;
                return;
            }

            // Пары не нашлось: журнал начали читать с середины блока. Запись всё равно
            // ценна — она говорит, что этот плагин через загрузку прошёл.
            PluginLoad orphan = new PluginLoad();
            orphan.Name = name;
            orphan.Restored = restored;
            orphan.Failed = failed;
            a.Plugins.Add(orphan);
        }

        /// <summary>«VST3: Going to restore: X» → «Going to restore: X», иначе null.</summary>
        static string AfterVstTag(string text, out PluginKind kind)
        {
            kind = PluginKind.Vst3;
            if (text.StartsWith("VST3: ", StringComparison.Ordinal)) return text.Substring(6);
            if (text.StartsWith("VST2: ", StringComparison.Ordinal))
            {
                kind = PluginKind.Vst2;
                return text.Substring(6);
            }
            return null;
        }

        /// <summary>
        /// «2026-08-24T15:15:42.939602: info: Loading document "…"» → сам текст записи.
        /// Строки без такой шапки — это продолжение предыдущей записи (Live переносит
        /// списки MIDI-устройств на несколько строк), маркеров в них не бывает.
        /// </summary>
        static string Payload(string line, out DateTime at)
        {
            at = DateTime.MinValue;

            // «info» или «error» — сам уровень нам без надобности: всё, что нужно
            // различать, различается по тексту записи («Restore 1 failed» приходит
            // ошибкой, «Restored» — сообщением, и ловим мы их по словам, не по уровню).
            int mark = line.IndexOf(InfoMark, StringComparison.Ordinal);
            int len = InfoMark.Length;
            if (mark < 0)
            {
                mark = line.IndexOf(ErrorMark, StringComparison.Ordinal);
                len = ErrorMark.Length;
            }
            if (mark <= 0) return null;

            at = Stamp(line.Substring(0, mark));
            return line.Substring(mark + len);
        }

        static DateTime Stamp(string s)
        {
            DateTime v;
            if (DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss.ffffff",
                                       CultureInfo.InvariantCulture, DateTimeStyles.None, out v))
                return v;
            if (DateTime.TryParseExact(s, "yyyy-MM-ddTHH:mm:ss",
                                       CultureInfo.InvariantCulture, DateTimeStyles.None, out v))
                return v;
            return DateTime.MinValue;
        }

        static string Quoted(string text, int from)
        {
            int close = text.IndexOf('"', from);
            return close < 0 ? text.Substring(from) : text.Substring(from, close - from);
        }

        // -------------------------------------------------------------- сравнение путей

        /// <summary>
        /// Тот ли это файл. Live пишет путь то с обратными слэшами (командная строка),
        /// то с прямыми (шаблон из библиотеки), поэтому сравнивать строки как есть нельзя.
        /// </summary>
        public static bool SamePath(string a, string b)
        {
            return string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);
        }

        internal static string Normalize(string p)
        {
            if (string.IsNullOrEmpty(p)) return "";
            string s = p.Replace('/', '\\').TrimEnd('\\');
            try { s = Path.GetFullPath(s); }
            catch { }
            return s;
        }
    }

    /// <summary>
    /// Один Log.txt, который можно и прочитать целиком, и дочитывать по мере того, как
    /// Live в него пишет. Дочитывание нужно окну восстановления: оно ждёт, чем кончится
    /// проба, и должно показать ответ сразу, а не гонять по девять мегабайт на каждый
    /// тик таймера.
    /// </summary>
    public sealed class LiveLogFile
    {
        public readonly string Path;
        public readonly string Version;

        /// <summary>
        /// С какого байта дочитывать. Стоит не на конце файла, а на начале последнего
        /// незакрытого блока «Loading document»: блок разбирается целиком каждый раз,
        /// иначе попытка, начавшаяся между тиками, приезжала бы без первых плагинов.
        /// </summary>
        long _offset;

        internal LiveLogFile(string path, string version)
        {
            Path = path;
            Version = version;
        }

        public DateTime Written
        {
            get { try { return File.GetLastWriteTimeUtc(Path); } catch { return DateTime.MinValue; } }
        }

        public long Length
        {
            get { try { return new FileInfo(Path).Length; } catch { return 0; } }
        }

        /// <summary>Начать слежение с текущего конца — прошлое нас в пробе не интересует.</summary>
        public void SkipToEnd() { _offset = Length; }

        /// <summary>Самая свежая попытка открыть этот сет во всём файле.</summary>
        public LoadAttempt LastAttempt(string alsPath)
        {
            LoadAttempt best = null;
            foreach (LoadAttempt a in All())
            {
                if (!LiveLog.SamePath(a.Document, alsPath)) continue;
                if (best == null || a.Started >= best.Started) best = a;
            }
            return best;
        }

        /// <summary>Все попытки из файла целиком.</summary>
        public List<LoadAttempt> All()
        {
            long start = 0;
            return Read(0, false, out start);
        }

        /// <summary>
        /// Что дописалось с прошлого раза. Последний блок может быть ещё не дописан —
        /// он вернётся с Result = Running, и в следующий раз приедет снова, уже целиком.
        /// </summary>
        public List<LoadAttempt> ReadNew()
        {
            long next;
            List<LoadAttempt> a = Read(_offset, true, out next);
            _offset = next;
            return a;
        }

        List<LoadAttempt> Read(long from, bool advance, out long next)
        {
            long fileEnd = from;
            next = from;

            byte[] buf;
            try
            {
                // Файл укоротился — Live переустановили или журнал подрезали. Начинаем заново.
                if (Length < from) from = 0;

                // FileShare.ReadWrite обязателен: журнал открыт самой Live на запись, и
                // без него чтение падало бы ровно в тот момент, ради которого затевалось.
                using (FileStream fs = new FileStream(Path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete, 64 * 1024))
                {
                    fs.Position = from;
                    long size = fs.Length - from;
                    if (size < 0) size = 0;
                    if (size > MaxRead) { from = fs.Length - MaxRead; fs.Position = from; size = MaxRead; }

                    buf = new byte[size];
                    int got = 0;
                    while (got < buf.Length)
                    {
                        int n = fs.Read(buf, got, buf.Length - got);
                        if (n <= 0) break;
                        got += n;
                    }
                    if (got != buf.Length) Array.Resize(ref buf, got);
                    fileEnd = from + got;
                }
            }
            catch (Exception ex)
            {
                Diag.Fail("livelog: read " + Path, ex);
                return new List<LoadAttempt>();
            }

            // Разбиваем сами, по байтам. StreamReader.ReadLine тут не годится: нужно
            // точное смещение начала строки в файле, чтобы вернуться к незакрытому блоку,
            // а пересчитать его из длины декодированной строки нельзя — журнал Live в
            // UTF-8 и полон кириллицы из имён проектов, символ там не равен байту.
            // Переводы строк у Live одиночные LF, но \r на конце снимаем на всякий случай.
            long lastOpenBlock = -1;
            List<string> lines = new List<string>();
            int start = 0;
            for (int i = 0; i <= buf.Length; i++)
            {
                if (i < buf.Length && buf[i] != (byte)'\n') continue;

                int end = i;
                if (end > start && buf[end - 1] == (byte)'\r') end--;
                if (end > start)
                {
                    string line = Encoding.UTF8.GetString(buf, start, end - start);
                    if (line.IndexOf(OpenBlock, StringComparison.Ordinal) >= 0)
                        lastOpenBlock = from + start;
                    lines.Add(line);
                }
                start = i + 1;
            }

            List<LoadAttempt> attempts = LiveLog.Parse(lines, Path, Version);
            Settle(attempts);

            // Последний блок мог быть ещё не дописан — Live грузит прямо сейчас. Тогда
            // смещение отступает к его началу, чтобы в следующий раз перечитать блок
            // целиком и увидеть развязку. Дочитанный блок так возвращать незачем.
            bool tailOpen = attempts.Count > 0
                         && attempts[attempts.Count - 1].Result == LoadResult.Running
                         && lastOpenBlock >= 0;
            if (advance) next = tailOpen ? lastOpenBlock : fileEnd;
            else next = fileEnd;

            return attempts;
        }

        /// <summary>
        /// Последний блок файла разбор всегда оставляет «Running»: закрыть его нечем —
        /// следующего «Loading document» ещё нет. Но если в журнал давно никто не писал,
        /// значит Live не грузит, а не пишет вовсе — и блок оборвался.
        /// </summary>
        void Settle(List<LoadAttempt> attempts)
        {
            if (attempts.Count == 0) return;
            LoadAttempt last = attempts[attempts.Count - 1];
            if (last.Result != LoadResult.Running) return;

            DateTime written = Written;
            if (written == DateTime.MinValue) return;
            if (DateTime.UtcNow - written > Silence) last.Result = LoadResult.Broke;
        }

        const string OpenBlock = ": info: Loading document \"";

        /// <summary>
        /// Сколько журнал должен молчать, чтобы считать загрузку оборванной. Порог
        /// щедрый нарочно: между строками «Going to restore» и «Restored» у тяжёлого
        /// плагина проходит до десятка секунд тишины (замерено на Addictive Drums 2 —
        /// семь секунд), и торопливый порог назвал бы виновным исправный плагин.
        /// </summary>
        static readonly TimeSpan Silence = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Сколько байт журнала читать за раз. Log.txt накопительный и на этой машине
        /// доходит до девяти мегабайт; тридцать два — потолок и для разовой диагностики,
        /// и от разросшегося до неприличия файла.
        /// </summary>
        const long MaxRead = 32L * 1024 * 1024;
    }
}
