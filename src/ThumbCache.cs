using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Threading;

namespace AbletonManager
{
    /// <summary>
    /// Finished arrangement preview pictures on disk.
    ///
    /// Why: a preview means fully parsing the .als, and the sets here run to 20 MB of XML once
    /// decompressed, so one tile costs around 120 ms. The home page shows two dozen at once,
    /// which is over three seconds on EVERY start — all for a picture that does not change
    /// until the set itself does. We keep it next to index.cache and read it back in a couple
    /// of milliseconds.
    ///
    /// The key is the path, the modification time and the size of the set: re-save the set and
    /// the key differs, the old entry simply stops being found and goes at the next sweep. No
    /// separate "has it gone stale" check is needed.
    ///
    /// An empty file means "the arrangement ruler is empty": that answer also costs a full
    /// parse, and remembering it is just as useful as the picture itself. Read errors, on the
    /// other hand, are not written to disk — they can be temporary (the file sits on a drive
    /// that is switched off), and remembering one would declare the set empty until its next
    /// edit.
    /// </summary>
    public static class ThumbCache
    {
        /// <summary>How many pictures we keep. More is simply occupied space: nobody looks at
        /// that many tiles at once, and re-reading one that fell out costs
        /// milliseconds.</summary>
        const int MaxFiles = 400;

        static int _swept;   // the sweep runs once per start, not on every save

        static string Dir { get { return Path.Combine(Settings.Dir, "thumbs"); } }

        // -------------------------------------------------------------------- key

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

        /// <summary>The cache file name for a set, or null if the set is not there right
        /// now.</summary>
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

        // ------------------------------------------------------------------ reading

        /// <summary>Is there a ready answer. Cheap — one File.Exists, called from
        /// drawing.</summary>
        public static bool Has(string setPath)
        {
            string f = KeyFile(setPath);
            if (f == null) return false;
            try { return File.Exists(f); }
            catch { return false; }
        }

        /// <summary>
        /// The finished picture. known=false — there is no entry (or it is corrupt) and the set
        /// has to be parsed as usual; known=true with null means "the arrangement is empty",
        /// nothing to parse.
        /// </summary>
        public static Bitmap Load(string setPath, out bool known)
        {
            known = false;
            string f = KeyFile(setPath);
            if (f == null) return null;
            try
            {
                if (!File.Exists(f)) return null;

                // We read it fully into memory: Image.FromFile keeps the file open for as long
                // as the picture lives, and the cache sweep would then be unable to delete it.
                byte[] bytes = File.ReadAllBytes(f);
                known = true;
                if (bytes.Length == 0) return null;          // the "arrangement is empty" mark

                using (MemoryStream ms = new MemoryStream(bytes, false))
                using (Image img = Image.FromStream(ms, false, false))
                {
                    // PArgb is the same format the scaled copies are kept in: DrawImage of such
                    // a picture does not recompute alpha on every frame.
                    Bitmap copy = new Bitmap(img.Width, img.Height, PixelFormat.Format32bppPArgb);
                    using (Graphics g = Graphics.FromImage(copy))
                        g.DrawImageUnscaled(img, 0, 0);
                    return copy;
                }
            }
            catch
            {
                known = false;
                try { File.Delete(f); } catch { }            // a corrupt entry — let it be redrawn
                return null;
            }
        }

        // ------------------------------------------------------------------ writing

        /// <summary>Save a picture (null saves the "arrangement is empty" mark). Call from the
        /// background.</summary>
        public static void Save(string setPath, Bitmap bmp)
        {
            string f = KeyFile(setPath);
            if (f == null) return;
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);

                // We write through a temporary file with a name of its own: several threads may
                // be saving at once, and they would fight over a shared ".tmp".
                string tmp = f + "." + Guid.NewGuid().ToString("N") + ".tmp";
                if (bmp == null) File.WriteAllBytes(tmp, new byte[0]);
                else bmp.Save(tmp, ImageFormat.Png);

                try
                {
                    if (File.Exists(f)) File.Delete(f);
                    File.Move(tmp, f);
                }
                catch { try { File.Delete(tmp); } catch { } }   // somebody got there first — all the better
            }
            catch { /* the cache is not critical - worst case we redraw */ }

            Sweep();
        }

        /// <summary>Save the "arrangement is empty" mark without occupying the UI
        /// thread.</summary>
        public static void SaveEmptyAsync(string setPath)
        {
            string p = setPath;
            ThreadPool.QueueUserWorkItem(delegate { Save(p, null); });
        }

        // ------------------------------------------------------------------ sweeping

        /// <summary>
        /// Keep the folder within reason: the excess goes, oldest first. Once per start and in
        /// the background — this is plain tidying, there is nowhere to hurry.
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
