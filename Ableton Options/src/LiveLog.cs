using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AbletonOptions
{
    /// <summary>
    /// Live при запуске разбирает Options.txt и на каждое незнакомое имя пишет в Log.txt
    /// строку вида:
    ///
    ///   info: Message Box: The options file [Options.txt] contains an unknown option
    ///   ‘-MidiClockSlave’ which will be ignored.
    ///
    /// Это точный ответ самой программы: какие опции твоя версия Live не понимает.
    /// Никакой публичный список такого не даёт — там всё ещё висят опции, выброшенные
    /// в Live 12. Поэтому каталог сверяется с логом и помечает отвергнутое.
    ///
    /// Обрати внимание: строки «Options: -X» в логе — это просто эхо файла, они пишутся
    /// и для принятых, и для отвергнутых опций. Доверять можно только сообщениям об ошибке.
    /// </summary>
    public static class LiveLog
    {
        const long MaxRead = 12 * 1024 * 1024;

        static readonly Regex Unknown =
            new Regex(@"unknown option[^-\r\n]*-([A-Za-z0-9_.]+)", RegexOptions.Compiled);

        /// <summary>Имена опций, которые этот Live отверг. Пустой набор — если лога нет.</summary>
        public static HashSet<string> RejectedBy(string preferencesDir)
        {
            HashSet<string> result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(preferencesDir)) return result;

            string path = Path.Combine(preferencesDir, "Log.txt");
            string text;
            try
            {
                // Live держит лог открытым, поэтому только совместное чтение.
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                                                      FileShare.ReadWrite | FileShare.Delete))
                {
                    if (fs.Length > MaxRead) fs.Position = fs.Length - MaxRead;
                    using (StreamReader r = new StreamReader(fs, Encoding.UTF8))
                        text = r.ReadToEnd();
                }
            }
            catch
            {
                return result;   // лога нет или занят — просто нечего сверять
            }

            foreach (Match m in Unknown.Matches(text))
                result.Add(m.Groups[1].Value);
            return result;
        }
    }
}
