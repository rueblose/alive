using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AbletonManager
{
    /// <summary>
    /// Готовые картинки превью аранжировки на диске.
    ///
    /// Зачем: превью — это полный разбор .als, а сеты тут по 20 МБ XML после распаковки,
    /// и одна плитка стоит около 120 мс. Главная показывает их два десятка разом, то есть
    /// больше трёх секунд на КАЖДОМ запуске — и всё ради картинки, которая не меняется,
    /// пока не изменится сам сет. Держим её рядом с index.cache и перечитываем за пару
    /// миллисекунд.
    ///
    /// Ключ — путь, время правки и размер сета: сет пересохранили — ключ другой, старая
    /// запись просто перестаёт находиться и уходит при ближайшей чистке. Отдельной
    /// проверки «не протухло ли» поэтому не нужно.
    ///
    /// Пустой файл — это «на линейке аранжировки пусто»: такой ответ тоже стоит полного
    /// разбора, и запоминать его так же полезно, как и саму картинку. А вот ошибки чтения
    /// на диск не пишем — они бывают временными (файл лежит на отключённом диске), и
    /// запомнить их значило бы объявить сет пустым до следующей его правки.
    /// </summary>
    public static class ThumbCache
    {
        /// <summary>Сколько картинок держим. Больше — просто занятое место: столько плиток
        /// разом всё равно не смотрят, а перечитать выпавшую стоит миллисекунды.</summary>
        const int MaxFiles = 400;

        static int _swept;   // чистку делаем один раз за запуск, не на каждое сохранение

        static string Dir { get { return Path.Combine(Settings.Dir, "thumbs"); } }

        // ------------------------------------------------------------------- ключ

        const ulong FnvOffset = 14695981039346656037;
        const ulong FnvPrime = 1099511628211;

        static ulong Fnv(string s, ulong h)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                h = (h ^ (byte)(c & 0xFF)) * FnvPrime;
                h = (h ^ (byte)(c >> 8)) * FnvPrime;
            }
            return h;
        }

        /// <summary>Имя файла кеша для сета или null, если сета сейчас нет на месте.</summary>
        static string KeyFile(string setPath)
        {
            if (string.IsNullOrEmpty(setPath)) return null;
            try
            {
                FileInfo fi = new FileInfo(setPath);
                if (!fi.Exists) return null;
                ulong h = Fnv(setPath.ToLowerInvariant(), FnvOffset);
                h = Fnv(fi.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture), h);
                h = Fnv(fi.Length.ToString(CultureInfo.InvariantCulture), h);
                return Path.Combine(Dir, h.ToString("x16") + ".png");
            }
            catch { return null; }
        }

        // ---------------------------------------------------------------- чтение

        /// <summary>Есть ли готовый ответ. Дёшево — один File.Exists, зовётся из отрисовки.</summary>
        public static bool Has(string setPath)
        {
            string f = KeyFile(setPath);
            if (f == null) return false;
            try { return File.Exists(f); }
            catch { return false; }
        }

        /// <summary>
        /// Готовая картинка. known=false — записи нет (или она испортилась), надо разбирать
        /// сет как обычно; known=true и null — «аранжировка пуста», разбирать незачем.
        /// </summary>
        public static Bitmap Load(string setPath, out bool known)
        {
            known = false;
            string f = KeyFile(setPath);
            if (f == null) return null;
            try
            {
                if (!File.Exists(f)) return null;

                // Читаем в память целиком: Image.FromFile держит файл открытым, пока жива
                // картинка, и чистка кеша потом не смогла бы его удалить.
                byte[] bytes = File.ReadAllBytes(f);
                known = true;
                if (bytes.Length == 0) return null;          // отметка «аранжировка пуста»

                using (MemoryStream ms = new MemoryStream(bytes, false))
                using (Image img = Image.FromStream(ms, false, false))
                {
                    // PArgb — тот же формат, в котором лежат масштабированные копии:
                    // DrawImage такой картинки не пересчитывает альфу на каждый кадр.
                    Bitmap copy = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                        g.DrawImageUnscaled(img, 0, 0);
                    return copy;
                }
            }
            catch
            {
                known = false;
                try { File.Delete(f); } catch { }            // битая запись — пусть перерисуют
                return null;
            }
        }

        // ---------------------------------------------------------------- запись

        /// <summary>Сохранить картинку (null — отметку «аранжировка пуста»). Звать из фона.</summary>
        public static void Save(string setPath, Bitmap bmp)
        {
            string f = KeyFile(setPath);
            if (f == null) return;
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

                // Пишем через временный файл со своим именем: сохранять могут сразу
                // несколько потоков, и общий «.tmp» они бы отбирали друг у друга.
                string tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
                if (bmp == null) File.WriteAllBytes(tmp, new byte[0]);
                else bmp.Save(tmp, ImageFormat.Png);

                try
                {
                    if (File.Exists(f)) File.Delete(f);
                    File.Move(tmp, f);
                }
                catch { try { File.Delete(tmp); } catch { } }   // кто-то успел раньше — и хорошо
            }
            catch { /* кеш — не критично, в худшем случае перерисуем */ }

            Sweep();
        }

        /// <summary>Сохранить отметку «аранжировка пуста», не занимая поток интерфейса.</summary>
        public static void SaveEmptyAsync(string setPath)
        {
            string p = setPath;
            ThreadPool.QueueUserWorkItem(delegate { Save(p, null); });
        }

        // ---------------------------------------------------------------- чистка

        /// <summary>
        /// Держим папку в разумных пределах: лишнее сносим, начиная с самого давнего.
        /// Один раз за запуск и в фоне — это чистая уборка, торопиться с ней некуда.
        /// </summary>
        static void Sweep()
        {
            if (Interlocked.Exchange(ref _swept, 1) != 0) return;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    DirectoryInfo d = new DirectoryInfo(Dir);
                    if (!d.Exists) return;
                    FileInfo[] all = d.GetFiles("*.png");
                    if (all.Length <= MaxFiles) return;
                    Array.Sort(all, delegate (FileInfo a, FileInfo b)
                    { return a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc); });
                    for (int i = 0; i < all.Length - MaxFiles; i++)
                        try { all[i].Delete(); } catch { }
                }
                catch { }
            });
        }
    }
}
