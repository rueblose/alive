using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AbletonManager
{
    /// <summary>
    /// Обход дерева папок в поисках сетов.
    ///
    /// Почему не Directory.GetFiles, как было раньше: в .NET Framework путь длиннее 260
    /// символов — это исключение, и падает на нём вся папка целиком вместе со всем, что
    /// под ней. В библиотеке из паков и пресетов таких мест хватает (замерено на D:\Music:
    /// 14 папок), и раньше они молча выпадали из сканирования — вместе со всеми сетами
    /// внутри. Снаружи это выглядело как «часть проектов программа не видит», причём без
    /// единого следа в интерфейсе. FindFirstFile с префиксом \\?\ ограничение снимает.
    ///
    /// Заодно это один проход вместо двух (GetFiles + GetDirectories) и без сборки
    /// массива строк на каждую папку — а папок в корне вроде D:\Music двадцать тысяч.
    /// </summary>
    internal static class FolderScan
    {
        public sealed class Result
        {
            public int Dirs;         // просмотрено папок
            public int Files;        // найдено файлов
            public int Unreadable;   // папок, которые не открылись
            public bool RootFailed;  // не открылась сама папка, с которой начинали
        }

        /// <summary>Вернуть true, чтобы прекратить обход.</summary>
        public delegate bool CancelCheck();

        /// <summary>
        /// Все файлы с указанным расширением в дереве. Папки Backup пропускаются по
        /// флагу — их сеты Live плодит сама, и в каталоге они не нужны.
        /// </summary>
        public static Result Find(string root, string extension, bool includeBackups,
                                  Action<string> onFile, CancelCheck cancel)
        {
            Result res = new Result();
            if (string.IsNullOrEmpty(root)) return res;
            try { root = System.IO.Path.GetFullPath(root); }
            catch { res.RootFailed = true; return res; }

            // Стек, а не рекурсия: у дерева бывает неожиданная глубина, а падать с
            // переполнением стека посреди сканирования нечем оправдать.
            Stack<string> todo = new Stack<string>();
            todo.Push(root);
            bool atRoot = true;

            while (todo.Count > 0)
            {
                if (cancel != null && cancel()) break;

                string dir = todo.Pop();
                res.Dirs++;

                WIN32_FIND_DATA fd;
                IntPtr h = FindFirstFileW(SearchPattern(dir), out fd);
                if (h == InvalidHandle)
                {
                    res.Unreadable++;
                    if (atRoot) res.RootFailed = true;
                    atRoot = false;
                    continue;
                }
                atRoot = false;

                try
                {
                    do
                    {
                        string name = fd.cFileName;
                        if (name == "." || name == "..") continue;

                        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            // Симлинки и junction'ы не разворачиваем: по ним обход уходит
                            // либо в петлю, либо второй раз в те же самые папки.
                            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                            if (!includeBackups &&
                                string.Equals(name, "Backup", StringComparison.OrdinalIgnoreCase)) continue;
                            todo.Push(Combine(dir, name));
                        }
                        else if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                        {
                            // Пробная копия помощника по восстановлению — это .als в папке
                            // проекта, и без этой строчки он приехал бы в каталог рядом с
                            // настоящим сетом. Живёт такой файл секунды, но попасть на
                            // сканирование успевает.
                            if (RescueProbe.IsProbe(name)) continue;

                            res.Files++;
                            if (onFile != null) onFile(Combine(dir, name));
                        }
                    }
                    while (FindNextFileW(h, out fd));
                }
                finally { FindClose(h); }
            }
            return res;
        }

        /// <summary>Сколько весит папка со всем, что в ней лежит.</summary>
        public struct Weight
        {
            public long Bytes;
            public int Files;

            /// <summary>
            /// Когда проект сохраняли. Live при каждом сохранении кладёт копию старого
            /// файла в Backup и пишет в её имя момент сохранения:
            /// «angelcore [2026-05-22 012035].als». Другой истории работы на диске нет —
            /// сам .als помнит только последний раз.
            ///
            /// Берём именно скобку, а не время файла: время у копии — это момент
            /// ПРЕДЫДУЩЕГО сохранения, копируется-то содержимое, которое было до.
            /// Проверено: «try1 riddik [2026-08-28 140615].als» лежит с временем 14:00.
            ///
            /// null, а не пустой список: папок без Backup большинство, и заводить на
            /// каждую по объекту незачем.
            /// </summary>
            public List<Save> Saves;
        }

        /// <summary>Одно сохранение: когда и какого сета — имя копия несёт в себе же.</summary>
        public struct Save
        {
            public DateTime When;
            public string Set;      // «angelcore» из «angelcore [2026-05-22 012035].als»
        }

        /// <summary>
        /// Вес папки целиком. Считаем всё подряд, включая Samples и Backup: вопрос
        /// «сколько занимает проект» — это про место на диске, а оно занято и ими.
        ///
        /// Размер берём прямо из записи каталога, которую и так вернул FindNextFile, —
        /// открывать каждый файл ради длины не нужно.
        /// </summary>
        public static Weight Weigh(string root, CancelCheck cancel)
        {
            Weight w = new Weight();
            if (string.IsNullOrEmpty(root)) return w;
            try { root = System.IO.Path.GetFullPath(root); }
            catch { return w; }

            Stack<string> todo = new Stack<string>();
            todo.Push(root);

            while (todo.Count > 0)
            {
                if (cancel != null && cancel()) break;
                string dir = todo.Pop();

                // Раз на папку, а не раз на файл: внутри Backup их бывают сотни.
                bool backups = dir.EndsWith(@"\Backup", StringComparison.OrdinalIgnoreCase);

                WIN32_FIND_DATA fd;
                IntPtr h = FindFirstFileW(SearchPattern(dir), out fd);
                if (h == InvalidHandle) continue;
                try
                {
                    do
                    {
                        string name = fd.cFileName;
                        if (name == "." || name == "..") continue;

                        if ((fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0)
                        {
                            if ((fd.dwFileAttributes & FILE_ATTRIBUTE_REPARSE_POINT) != 0) continue;
                            todo.Push(Combine(dir, name));
                        }
                        else
                        {
                            w.Bytes += ((long)fd.nFileSizeHigh << 32) | fd.nFileSizeLow;
                            w.Files++;

                            Save saved;
                            if (backups && TryBackupStamp(name, out saved))
                            {
                                if (w.Saves == null) w.Saves = new List<Save>();
                                w.Saves.Add(saved);
                            }
                        }
                    }
                    while (FindNextFileW(h, out fd));
                }
                finally { FindClose(h); }
            }
            return w;
        }

        /// <summary>
        /// Момент сохранения из имени копии: «что угодно [2026-05-22 012035].als».
        /// Разбираем по позициям, а не ParseExact, — вызовов тысячи, а формат Live
        /// пишет сама, и он не зависит ни от языка системы, ни от её настроек даты.
        /// </summary>
        internal static bool TryBackupStamp(string name, out Save save)
        {
            save = default(Save);
            if (name == null || !name.EndsWith("].als", StringComparison.OrdinalIgnoreCase)) return false;

            int i = name.LastIndexOf('[');
            // «[» + «2026-05-22 012035» (17) + «]» + «.als» — ровно 23 символа с конца.
            if (i < 0 || name.Length - i != 23) return false;
            if (name[i + 5] != '-' || name[i + 8] != '-' || name[i + 11] != ' ') return false;

            int y, mo, d, h, mi, se;
            if (!Num(name, i + 1, 4, out y) || !Num(name, i + 6, 2, out mo) || !Num(name, i + 9, 2, out d) ||
                !Num(name, i + 12, 2, out h) || !Num(name, i + 14, 2, out mi) || !Num(name, i + 16, 2, out se))
                return false;

            if (mo < 1 || mo > 12 || d < 1 || d > 31 || h > 23 || mi > 59 || se > 59) return false;
            try { save.When = new DateTime(y, mo, d, h, mi, se, DateTimeKind.Local); }
            catch { return false; }        // 31 февраля из чужого имени файла

            save.Set = name.Substring(0, i).TrimEnd();
            return true;
        }

        static bool Num(string s, int at, int len, out int value)
        {
            value = 0;
            for (int i = at; i < at + len; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9') return false;
                value = value * 10 + (c - '0');
            }
            return true;
        }

        static string Combine(string dir, string name)
        {
            return dir.Length > 0 && dir[dir.Length - 1] == '\\' ? dir + name : dir + "\\" + name;
        }

        /// <summary>
        /// Путь в той форме, в которой его понимает FindFirstFile без ограничения на
        /// длину. Префикс \\?\ отключает нормализацию пути, поэтому годится только
        /// полный путь с обычными слешами — GetFullPath выше именно такой и делает.
        /// </summary>
        static string SearchPattern(string dir)
        {
            string p = dir;
            if (!p.StartsWith(@"\\?\", StringComparison.Ordinal))
                p = p.StartsWith(@"\\", StringComparison.Ordinal)
                  ? @"\\?\UNC\" + p.Substring(2)      // сетевая папка \\server\share
                  : @"\\?\" + p;
            if (p.Length > 0 && p[p.Length - 1] != '\\') p += "\\";
            return p + "*";
        }

        // ------------------------------------------------------------------ win32

        const int FILE_ATTRIBUTE_DIRECTORY = 0x10;
        const int FILE_ATTRIBUTE_REPARSE_POINT = 0x400;
        static readonly IntPtr InvalidHandle = new IntPtr(-1);

        // Все поля по 4 байта: с FILETIME как long структура на x64 разъезжается по
        // выравниванию, и имя файла читается из мусора.
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct WIN32_FIND_DATA
        {
            public int dwFileAttributes;
            public uint ftCreationLow, ftCreationHigh;
            public uint ftAccessLow, ftAccessHigh;
            public uint ftWriteLow, ftWriteHigh;
            public uint nFileSizeHigh, nFileSizeLow;
            public uint dwReserved0, dwReserved1;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string cFileName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string cAlternateFileName;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr FindFirstFileW(string pattern, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindNextFileW(IntPtr handle, out WIN32_FIND_DATA data);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool FindClose(IntPtr handle);
    }
}
