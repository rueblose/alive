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
