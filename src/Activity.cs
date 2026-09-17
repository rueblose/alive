using System;
using System.Collections.Generic;
using System.IO;

namespace AbletonManager
{
    /// <summary>
    /// История работы: в какие дни и часы сохраняли проекты.
    ///
    /// Источник — папки Backup. Live кладёт туда копию при каждом сохранении и пишет в
    /// её имя момент сохранения; ничего другого о прошлом на диске не сохраняется, сам
    /// .als помнит только последний раз. Разбором занимается FolderScan прямо в том
    /// обходе, который и так считает вес папки проекта, — лишних обращений к диску нет.
    ///
    /// Но одного диска мало: Live держит только ДЕСЯТЬ последних копий на имя сета и
    /// затирает остальные (замерено: 144 папки Backup ровно по 10 файлов). То есть
    /// история сама себя стирает — и как раз у тех проектов, над которыми работают
    /// плотнее всего. Поэтому свой кеш не перезаписывается сканированием, а копится:
    /// день, который однажды попал в историю, из неё больше не уходит, даже если Live
    /// давно выкинула ту копию, а сам проект удалён.
    ///
    /// Снимки нашего же SnapshotStore тоже лежат в Backup (в подпапке Alive), но в
    /// историю не идут: они сняты поверх того же сохранения Live и удвоили бы день.
    /// Отсекаются сами собой — и по имени папки, и по форме имени файла.
    ///
    /// Объект неизменяемый: сканирование собирает новый и подменяет ссылку целиком, как
    /// и список сетов. Читают его из потока интерфейса, пишет фоновый.
    /// </summary>
    public sealed class Activity
    {
        public static readonly Activity Empty = new Activity(new List<DateTime>());

        readonly List<DateTime> _stamps;                 // по возрастанию
        readonly Dictionary<DateTime, int> _byDay = new Dictionary<DateTime, int>();
        readonly int[] _byHour = new int[24];

        public int Total { get { return _stamps.Count; } }
        public int ActiveDays { get { return _byDay.Count; } }

        public DateTime First, Last;
        public int CurrentStreak, LongestStreak;
        public DateTime BusiestDay;
        public int BusiestSaves;
        public int PeakHour = -1;

        /// <summary>Сколько сохранений пришлось на этот день.</summary>
        public int SavesOn(DateTime day)
        {
            int n;
            return _byDay.TryGetValue(day.Date, out n) ? n : 0;
        }

        public int[] HourHistogram { get { return _byHour; } }

        // ------------------------------------------------------------------- сборка

        Activity(List<DateTime> stamps)
        {
            stamps.Sort();
            _stamps = stamps;
            if (stamps.Count == 0) return;

            First = stamps[0];
            Last = stamps[stamps.Count - 1];

            foreach (DateTime t in stamps)
            {
                DateTime day = t.Date;
                int n;
                _byDay.TryGetValue(day, out n);
                _byDay[day] = n + 1;
                _byHour[t.Hour]++;
            }

            foreach (KeyValuePair<DateTime, int> kv in _byDay)
                if (kv.Value > BusiestSaves) { BusiestSaves = kv.Value; BusiestDay = kv.Key; }

            for (int h = 0; h < 24; h++)
                if (PeakHour < 0 || _byHour[h] > _byHour[PeakHour]) PeakHour = h;

            List<DateTime> days = new List<DateTime>(_byDay.Keys);
            days.Sort();
            Streaks(days);
        }

        void Streaks(List<DateTime> days)
        {
            int run = 0;
            for (int i = 0; i < days.Count; i++)
            {
                run = i > 0 && days[i] == days[i - 1].AddDays(1) ? run + 1 : 1;
                if (run > LongestStreak) LongestStreak = run;
            }

            // Текущая серия считается от сегодня, но день ещё не кончился: если сегодня
            // ещё не садился — серию обрывать рано, смотрим со вчера. Так же считает
            // GitHub, и так же это ощущается изнутри.
            DateTime cursor = DateTime.Today;
            if (!_byDay.ContainsKey(cursor)) cursor = cursor.AddDays(-1);
            while (_byDay.ContainsKey(cursor)) { CurrentStreak++; cursor = cursor.AddDays(-1); }
        }

        /// <summary>
        /// Собрать историю из того, что принесло сканирование, добавив её к уже
        /// известной. previous — история прошлого запуска (обычно из кеша); null, если
        /// собираем с чистого листа.
        ///
        /// Время самого .als идёт в зачёт, ТОЛЬКО если у сета нет ни одной копии.
        ///
        /// Свежая копия — это и есть последнее сохранение: Live выбрасывает из Backup
        /// самые старые, а не самые новые, так что верхняя отметка всегда совпадает с
        /// текущим файлом. Значит время .als при живых копиях либо дубль (и сет
        /// считался бы дважды), либо след правки МИМО Live — Collect All и помощник по
        /// восстановлению переписывают .als сами, и сохранением это не является.
        /// Раньше здесь стояло окно в 90 секунд, и сквозь него пролезали оба случая:
        /// правка Collect All за 72 дня до последней копии и файл, приехавший из
        /// другого часового пояса, — у него время расходилось ровно на 4 часа.
        ///
        /// А брать одни копии тоже нельзя: у сетов, сохранённых единожды, копий нет
        /// вовсе (первое сохранение копировать нечего), и они пропали бы целиком.
        /// </summary>
        internal static Activity Build(IList<string> dirs, FolderScan.Weight[] weights,
                                       IEnumerable<SetEntry> sets, Activity previous)
        {
            // Секунда — достаточный ключ: дважды сохранить в одну и ту же секунду можно
            // только из двух копий Live разом, и цена такого совпадения — один
            // несосчитанный save. Зато без ключа кеш удваивался бы на каждом скане.
            //
            // Отсев нужен и внутри одного обхода: если сет лежит не в «* Project», его
            // папкой проекта считается собственная, а она бывает родителем чужих
            // проектов — и их копии обходятся дважды. Замерено: 1143 файла копий дают
            // 1135 отметок, восемь пришли по второму разу.
            HashSet<long> seen = new HashSet<long>();
            List<DateTime> stamps = new List<DateTime>();

            if (previous != null)
                foreach (DateTime t in previous._stamps)
                    if (Sane(t) && seen.Add(t.Ticks)) stamps.Add(t);

            // Ключ — папка и имя сета: в одной папке проекта лежат разные версии, и
            // копии у каждой свои, с её собственным именем в начале.
            HashSet<string> newest = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (dirs != null && weights != null)
                for (int i = 0; i < dirs.Count && i < weights.Length; i++)
                {
                    List<FolderScan.Save> saves = weights[i].Saves;
                    if (saves == null) continue;
                    foreach (FolderScan.Save sv in saves)
                    {
                        if (Sane(sv.When) && seen.Add(sv.When.Ticks)) stamps.Add(sv.When);

                        // Отмечаем, что у этого сета копии есть, — по этому ниже
                        // отсеивается время самого .als.
                        newest.Add(dirs[i] + "|" + sv.Set);
                    }
                }

            if (sets != null)
                foreach (SetEntry s in sets)
                {
                    if (s.IsBackup || s.Modified == default(DateTime)) continue;
                    if (newest.Contains(s.ProjectDir + "|" + s.Name)) continue;

                    DateTime when = s.Modified.ToLocalTime();
                    if (Sane(when) && seen.Add(when.Ticks)) stamps.Add(when);
                }

            return new Activity(stamps);
        }

        /// <summary>
        /// Отметка похожа на правду? Кеш копится и никогда не чистится, поэтому одна
        /// запись со сбитыми часами осталась бы в истории навсегда и растянула бы
        /// календарь на пустые десятилетия. Фильтр стоит и на входящем из кеша: если
        /// часы поправили, мусор уйдёт сам на ближайшем сканировании.
        /// </summary>
        static bool Sane(DateTime t)
        {
            return t.Year >= 2000 && t <= DateTime.Today.AddDays(2);
        }

        // -------------------------------------------------------------------- кеш

        /// <summary>
        /// Файл рядом с index.cache. Это НЕ ускоритель, а единственная долговечная
        /// копия истории: на диске уцелевшие копии Live держат от одного до шести дней
        /// работы над проектом (замерено по папкам, упёршимся в предел из десяти), всё
        /// остальное прошлое есть только здесь. Поэтому и пишется он через временный
        /// файл, и читается до последней целой записи, а не «всё или ничего».
        /// </summary>
        const int CacheVersion = 1;

        static string CachePath { get { return Path.Combine(Settings.Dir, "activity.cache"); } }

        public static Activity LoadCache()
        {
            List<DateTime> stamps = new List<DateTime>();
            try
            {
                if (!File.Exists(CachePath)) return Empty;
                using (BinaryReader r = new BinaryReader(File.OpenRead(CachePath)))
                {
                    if (r.ReadInt32() != CacheVersion) return Empty;
                    int n = r.ReadInt32();
                    if (n < 0 || n > 5000000) return Empty;

                    // Читаем сколько прочтётся. Обрыв записи — это потеря хвоста, а не
                    // повод выбросить годы: вернув Empty, мы бы ещё и перезаписали
                    // остаток пустышкой на ближайшем сканировании.
                    for (int i = 0; i < n; i++)
                        stamps.Add(new DateTime(r.ReadInt64(), DateTimeKind.Local));
                }
            }
            catch { }
            return stamps.Count == 0 ? Empty : new Activity(stamps);
        }

        public void SaveCache()
        {
            try
            {
                if (!Directory.Exists(Settings.Dir)) Directory.CreateDirectory(Settings.Dir);

                // Пишем рядом и подменяем готовым. Прямая запись поверх означала бы, что
                // выключенный посреди неё компьютер оставляет обрубок вместо всей
                // истории, а восстановить её с диска уже неоткуда.
                string tmp = CachePath + ".tmp";
                using (BinaryWriter w = new BinaryWriter(File.Create(tmp)))
                {
                    w.Write(CacheVersion);
                    w.Write(_stamps.Count);
                    foreach (DateTime t in _stamps) w.Write(t.Ticks);
                }
                if (File.Exists(CachePath)) File.Replace(tmp, CachePath, null);
                else File.Move(tmp, CachePath);
            }
            catch { }
        }
    }
}
