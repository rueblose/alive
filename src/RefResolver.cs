using System;
using System.Collections.Generic;
using System.IO;

namespace AbletonManager
{
    public enum RefStatus
    {
        Empty,        // ссылки нет: пустой FileRef-заглушка, таких в сете большинство
        Found,        // файл на месте
        Missing,      // не нашли — вот это настоящая потеря
        MissingPack   // не найден Live Pack, на который ссылается сет
    }

    public sealed class ResolvedRef
    {
        public FileRefInfo Ref;
        public RefStatus Status;
        public string ResolvedPath = "";
        public string Note = "";
    }

    /// <summary>
    /// RelativePathType в .als задаёт КОРЕНЬ, от которого считается RelativePath.
    /// Значения выяснены на реальной библиотеке (см. README):
    ///   0 - ссылки нет
    ///   1 - от папки проекта, может уходить вверх (../../Samples/...)
    ///   3 - внутри папки проекта
    ///   5 - от корня Live Pack, имя пака в LivePackName
    ///   6 - от User Library
    ///   7 - от Resources\Builtin установленного Live
    /// Абсолютный путь используется только как последняя подсказка: в чужих и старых
    /// проектах он ведёт на другую машину, другой диск или прошлую версию Live.
    /// </summary>
    public static class RefResolver
    {
        // ------------------------------------------------- кеш файловых проб
        //
        // Замерено на реальной библиотеке: в двадцати сетах 29 643 ссылки на сэмплы, а
        // разных путей среди них всего 1 112 — то есть 96% проверок «есть ли такой файл»
        // спрашивают ровно то же самое, что уже спрашивали. Один сет ссылается на свою
        // папку Samples сотнями клипов, а общие библиотеки и паки повторяются во всех
        // сетах сразу.
        //
        // Кешируем именно результат ПРОБЫ, а не саму ссылку: разрешённые пути нужны
        // целиком — по ним счётчики отбрасывают повторы одного и того же файла.
        //
        // Живёт только на время одного сканирования: набор файлов на диске меняется без
        // спроса, и запомненный между сканированиями ответ был бы враньём — «пересканить»
        // должно означать «проверить заново».

        static volatile Dictionary<string, bool> _probe;

        public static void BeginScan()
        {
            _probe = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        }

        public static void EndScan()
        {
            _probe = null;
        }

        static bool PathExists(string full)
        {
            Dictionary<string, bool> cache = _probe;
            if (cache == null) return Probe(full);

            bool ok;
            lock (cache) { if (cache.TryGetValue(full, out ok)) return ok; }
            ok = Probe(full);
            lock (cache) cache[full] = ok;
            return ok;
        }

        /// <summary>Directory.Exists тут не лишний: .adg и .amxd бывают папками.</summary>
        static bool Probe(string full)
        {
            try { return File.Exists(full) || Directory.Exists(full); }
            catch { return false; }
        }

        public static ResolvedRef Resolve(FileRefInfo fr, string projectDir, LiveEnvironment env)
        {
            ResolvedRef res = new ResolvedRef();
            res.Ref = fr;

            bool noRel = string.IsNullOrEmpty(fr.RelativePath);
            bool noAbs = string.IsNullOrEmpty(fr.AbsolutePath);
            if (noRel && noAbs) { res.Status = RefStatus.Empty; return res; }

            string rel = noRel ? null : fr.RelativePath.Replace('/', Path.DirectorySeparatorChar);

            switch (fr.RelativePathType)
            {
                case 1:
                case 3:
                    if (Try(projectDir, rel, res)) return res;
                    break;

                case 5:
                    string packRoot = null;
                    if (!string.IsNullOrEmpty(fr.LivePackName))
                        env.Packs.TryGetValue(fr.LivePackName, out packRoot);
                    if (Try(packRoot, rel, res)) return res;
                    if (packRoot == null && !string.IsNullOrEmpty(fr.LivePackName))
                    {
                        // пак вообще не установлен — это другая беда, чем потерянный сэмпл
                        if (!Exists(fr.AbsolutePath))
                        {
                            res.Status = RefStatus.MissingPack;
                            res.Note = fr.LivePackName;
                            res.ResolvedPath = fr.RelativePath;
                            return res;
                        }
                    }
                    break;

                case 6:
                    if (Try(env.UserLibrary, rel, res)) return res;
                    break;

                case 7:
                    if (Try(env.Builtin, rel, res)) return res;
                    if (Try(env.CoreLibrary, rel, res)) return res;
                    break;
            }

            // запасные варианты: абсолютный путь, затем прочие корни
            if (Exists(fr.AbsolutePath))
            {
                res.Status = RefStatus.Found;
                res.ResolvedPath = fr.AbsolutePath.Replace('/', Path.DirectorySeparatorChar);
                return res;
            }
            if (Try(projectDir, rel, res)) return res;
            if (Try(env.UserLibrary, rel, res)) return res;
            if (Try(env.Builtin, rel, res)) return res;
            if (Try(env.CoreLibrary, rel, res)) return res;

            res.Status = RefStatus.Missing;
            res.ResolvedPath = noAbs ? fr.RelativePath : fr.AbsolutePath;
            return res;
        }

        static bool Try(string root, string relative, ResolvedRef res)
        {
            if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(relative)) return false;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, relative)); }
            catch { return false; }

            if (PathExists(full))
            {
                res.Status = RefStatus.Found;
                res.ResolvedPath = full;
                return true;
            }
            return false;
        }

        static bool Exists(string p)
        {
            if (string.IsNullOrEmpty(p)) return false;
            return PathExists(p.Replace('/', Path.DirectorySeparatorChar));
        }
    }
}
