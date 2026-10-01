using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using AbletonManager;

namespace AliveTools
{
    /// <summary>
    /// A test bench for the sample library. Not part of the distribution — it checks the thing
    /// rather than being it.
    ///
    ///     LibraryTest.exe all        every check below
    ///     LibraryTest.exe index      sample paths in the sets catalog and its cache
    ///     LibraryTest.exe walk       the library walk and its cache
    ///     LibraryTest.exe usage      which samples the sets use, copies included
    ///     LibraryTest.exe aiff       the AIFF reader
    ///     LibraryTest.exe sources    Live's Places and the From Live suggestions
    ///     LibraryTest.exe fit        names and paths cut to their cell
    ///     LibraryTest.exe copies     the same sample in several places
    ///     LibraryTest.exe catalog    the scan, the search and the selection of the main window
    ///     LibraryTest.exe data       the data folder's files survive a failed read and a crash mid-write
    ///     LibraryTest.exe visual     what the interface draws: numbers, fades, counts
    ///     LibraryTest.exe texts      what the words and small reactions say: pins, counts, dates
    ///     LibraryTest.exe real &lt;projects&gt; &lt;samples...&gt;   timings on a real library, no checks
    ///
    /// Runs with its own ALIVE_HOME under %TEMP% — the owner's settings and caches are never
    /// touched. Build: tools\build-library-test.cmd
    /// </summary>
    internal static class LibraryTest
    {
        static int _checks, _failed;

        static void Check(bool ok, string what)
        {
            _checks++;
            if (ok) return;
            _failed++;
            Console.WriteLine("FAIL: " + what);
        }

        [STAThread]
        static int Main(string[] args)
        {
            Environment.SetEnvironmentVariable("ALIVE_HOME", Fresh("home"));

            string cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (cmd == "real") { Real(args); return 0; }
            if (cmd == "all" || cmd == "index") Index();
            if (cmd == "all" || cmd == "walk") Walk();
            if (cmd == "all" || cmd == "usage") Usage();
            if (cmd == "all" || cmd == "aiff") Aiff();
            if (cmd == "all" || cmd == "sources") Sources();
            if (cmd == "all" || cmd == "fit") Fit();
            if (cmd == "all" || cmd == "copies") CopiesCheck();
            if (cmd == "all" || cmd == "catalog") Catalog();
            if (cmd == "all" || cmd == "data") Data();
            if (cmd == "all" || cmd == "visual") Visual();
            if (cmd == "all" || cmd == "texts") Texts();

            if (_checks == 0)
            {
                Console.WriteLine("usage: LibraryTest.exe all | index | walk | usage | aiff | sources | fit | copies | catalog | data | visual | texts | real <projects> <samples...>");
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

        // ------------------------------------------------------------ scaffolding

        static string DataRoot
        {
            get { return Path.Combine(Path.GetTempPath(), "alive-library-data"); }
        }

        /// <summary>An empty folder of our own under %TEMP%\alive-library-data.</summary>
        static string Fresh(string name)
        {
            string dir = Path.Combine(DataRoot, name);
            Nuke(dir);
            Directory.CreateDirectory(dir);
            return dir;
        }

        /// <summary>rmdir through \\?\ — Directory.Delete gives up on paths past 260 characters,
        /// and the walk test makes one. rmdir removes a junction without following it.</summary>
        static void Nuke(string dir)
        {
            if (!Directory.Exists(dir)) return;
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c rmdir /s /q \"\\\\?\\" + dir + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            using (Process p = Process.Start(psi)) p.WaitForExit(15000);
        }

        /// <summary>A mono 16-bit WAV of silence. Returns its size on disk.</summary>
        static long WriteWav(string path, int frames) { return WriteWav(path, frames, 0); }

        /// <summary>The same, every data byte set to fill — a different sound of the same
        /// length.</summary>
        static long WriteWav(string path, int frames, byte fill)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream fs = File.Create(path))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                int data = frames * 2;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + data);
                w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16);
                w.Write((short)1); w.Write((short)1); w.Write(44100); w.Write(88200);
                w.Write((short)2); w.Write((short)16);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(data);
                byte[] body = new byte[data];
                if (fill != 0) for (int i = 0; i < body.Length; i++) body[i] = fill;
                w.Write(body);
            }
            return new FileInfo(path).Length;
        }

        /// <summary>A minimal .als: gzip over just enough XML for AlsFile to find the FileRefs.</summary>
        static void WriteAls(string path, params string[] fileRefs)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
            sb.Append("<Ableton Creator=\"Ableton Live 12.3\">\n<LiveSet>\n<Tracks>\n<AudioTrack Id=\"0\">\n");
            foreach (string r in fileRefs) sb.Append(r);
            sb.Append("</AudioTrack>\n</Tracks>\n</LiveSet>\n</Ableton>\n");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (FileStream fs = File.Create(path))
            using (GZipStream gz = new GZipStream(fs, CompressionMode.Compress))
            {
                byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
                gz.Write(b, 0, b.Length);
            }
        }

        /// <summary>A FileRef under the given container: SampleRef is a clip's sample, anything
        /// else only provenance.</summary>
        static string Ref(string container, string absolute, long size)
        {
            return "<" + container + "><FileRef>"
                 + "<RelativePathType Value=\"0\" /><RelativePath Value=\"\" />"
                 + "<Path Value=\"" + System.Security.SecurityElement.Escape(absolute) + "\" />"
                 + "<OriginalFileSize Value=\"" + size + "\" /><LivePackName Value=\"\" />"
                 + "</FileRef></" + container + ">\n";
        }

        static bool Same(string a, string b)
        {
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ index

        static void Index()
        {
            string root = Fresh("index");
            string lib = Path.Combine(root, "lib");
            string kick = Path.Combine(lib, "kick.wav");
            long kickSize = WriteWav(kick, 100);

            WriteAls(Path.Combine(Path.Combine(root, "song Project"), "song.als"),
                     Ref("SampleRef", kick, kickSize),
                     Ref("SampleRef", kick, kickSize),                          // a second clip on the same file
                     Ref("SampleRef", Path.Combine(lib, "gone.wav"), 10),        // lost
                     Ref("FilePresetRef", Path.Combine(lib, "x.adv"), 5));       // provenance, not a dependency

            Settings s = new Settings();
            s.Roots.Add(root);
            ProjectIndex idx = new ProjectIndex();
            idx.Scan(s, null, CancellationToken.None);

            Check(idx.Sets.Count == 1, "index: one set expected, got " + idx.Sets.Count);
            if (idx.Sets.Count != 1) return;
            SetEntry e = idx.Sets[0];
            Check(e.Samples.Length == 1, "index: one found sample expected, got " + e.Samples.Length);
            Check(e.Samples.Length == 1 && Same(e.Samples[0], kick), "index: the sample is not the kick");
            Check(e.SampleSizes.Length == e.Samples.Length, "index: sizes are not parallel to paths");
            Check(e.SampleSizes.Length == 1 && e.SampleSizes[0] == kickSize, "index: size is not OriginalFileSize");
            Check(e.TotalRefs == 2 && e.MissingFiles == 1, "index: the Files counters changed meaning");

            ProjectIndex again = new ProjectIndex();
            Check(again.LoadFromCache(), "index: the cache did not load");
            SetEntry c = again.Sets.Count == 1 ? again.Sets[0] : null;
            Check(c != null && c.Samples.Length == 1 && c.SampleSizes.Length == 1
                  && Same(c.Samples[0], kick) && c.SampleSizes[0] == kickSize,
                  "index: the samples did not survive the cache");
        }

        // ------------------------------------------------------------------- walk

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateDirectoryW(string path, IntPtr security);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CopyFileW(string from, string to, bool failIfExists);

        static bool Junction(string link, string target)
        {
            ProcessStartInfo psi = new ProcessStartInfo("cmd.exe", "/c mklink /J \"" + link + "\" \"" + target + "\"");
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.RedirectStandardOutput = true;
            using (Process p = Process.Start(psi))
            {
                p.StandardOutput.ReadToEnd();
                p.WaitForExit(10000);
                return p.ExitCode == 0;
            }
        }

        /// <summary>A chain of folders past 260 characters with one sample at the bottom.
        /// Returns the sample's path, or null when Windows refused to make it.</summary>
        static string LongSample(string root, string first)
        {
            string dir = Path.Combine(root, first);
            Directory.CreateDirectory(dir);
            while (dir.Length < 300)
            {
                dir = dir + "\\" + new string('x', 40);
                if (!CreateDirectoryW(@"\\?\" + dir, IntPtr.Zero)) return null;
            }
            string small = Path.Combine(DataRoot, "one.wav");
            WriteWav(small, 10);
            string file = dir + "\\deep.wav";
            return CopyFileW(small, @"\\?\" + file, false) ? file : null;
        }

        static SampleFolder Child(SampleFolder f, string name)
        {
            if (f == null) return null;
            foreach (SampleFolder c in f.Children)
                if (Same(c.Name, name)) return c;
            return null;
        }

        static long Size(string path) { return new FileInfo(path).Length; }

        static void Walk()
        {
            string lib = Path.Combine(Fresh("walk"), "Samples");
            WriteWav(Path.Combine(lib, "top.wav"), 10);                                       // right in the root
            string kick1 = Path.Combine(lib, @"Pack A\Kicks\kick 1.wav");
            WriteWav(kick1, 10);
            WriteWav(Path.Combine(lib, @"Pack A\Kicks\kick 2.WAV"), 10);                     // case of the extension
            File.WriteAllBytes(Path.Combine(lib, @"Pack A\Kicks\._kick 1.wav"), new byte[4096]);  // AppleDouble
            File.WriteAllBytes(Path.Combine(lib, @"Pack A\Kicks\kick 1.wav.asd"), new byte[100]); // analysis file
            string ogg = Path.Combine(lib, @"Pack A\Ableton Folder Info\Previews\kick.ogg");
            WriteWav(ogg, 10);                                                                  // Live's preview
            string take = Path.Combine(lib, @"Pack A\Demo Project\Samples\take.wav");
            WriteWav(take, 10);                                                                 // a project inside
            Directory.CreateDirectory(Path.Combine(lib, @"Pack A\Presets"));
            File.WriteAllBytes(Path.Combine(lib, @"Pack A\Presets\bass.adv"), new byte[300]);  // nothing to hear
            WriteWav(Path.Combine(lib, @"Pack B\loop.aif"), 10);
            bool junction = Junction(Path.Combine(lib, @"Pack B\again"), lib);
            string deep = LongSample(lib, "Pack C");
            if (!junction) Console.WriteLine("walk: could not make a junction - that check is skipped");
            if (deep == null) Console.WriteLine("walk: could not make a long path - that check is skipped");

            int expected = 4 + (deep != null ? 1 : 0);      // top, kick 1, kick 2, loop, and the deep one
            long packA = Size(kick1) + Size(Path.Combine(lib, @"Pack A\Kicks\kick 2.WAV")) + 4096 + 100
                       + Size(ogg) + Size(take) + 300;

            // The second root lies inside the first: it must not be walked on its own.
            List<string> roots = new List<string> { lib, Path.Combine(lib, "Pack A") };
            SampleIndex idx = SampleIndex.Build(roots, new List<string>(), null, CancellationToken.None);

            Check(idx.Roots.Count == 1, "walk: a root inside another root must be skipped");
            SampleFolder r = idx.Roots.Count > 0 ? idx.Roots[0] : null;
            Check(r != null && r.TotalSamples == expected,
                  "walk: " + expected + " samples expected, got " + (r != null ? r.TotalSamples : -1));
            SampleFolder a = Child(r, "Pack A");
            Check(a != null && a.Children.Count == 1 && Same(a.Children[0].Name, "Kicks"),
                  "walk: only folders with samples below them become nodes");
            SampleFolder kicks = Child(a, "Kicks");
            Check(kicks != null && kicks.Files.Count == 2, "walk: ._ files and .asd are not samples");
            Check(a != null && a.TotalBytes == packA,
                  "walk: Pack A should weigh " + packA + " with its weight-only folders, got " + (a != null ? a.TotalBytes : -1));
            Check(kicks != null && kicks.Files.Count > 0 && Same(kicks.Files[0].Path.Substring(0, kicks.Path.Length), kicks.Path),
                  "walk: a sample's path is not under its folder");
            if (junction)
                Check(Child(r, "Pack B") != null && Child(r, "Pack B").TotalSamples == 1, "walk: the junction was followed");
            Check(idx.Files.Count == expected, "walk: Files holds " + idx.Files.Count + " instead of " + expected);
            Check(SampleIndex.CountIn(lib, null) == expected, "walk: CountIn disagrees with the walk");
            Check(SampleIndex.CountIn(Path.Combine(lib, "nowhere"), null) == -1, "walk: a missing folder must count -1");

            // The dates come from the directory entries, as Explorer shows them.
            SampleFile k1 = null;
            foreach (SampleFile f in idx.Files) if (Same(f.Name, "kick 1.wav")) k1 = f;
            DateTime k1Written = File.GetLastWriteTimeUtc(kick1), k1Made = File.GetCreationTimeUtc(kick1);
            Check(k1 != null && Math.Abs((k1.Modified - k1Written).TotalSeconds) < 1
                  && Math.Abs((k1.Created - k1Made).TotalSeconds) < 1,
                  "walk: a sample's dates are not the file's");
            Check(kicks != null && Math.Abs((kicks.Created - Directory.GetCreationTimeUtc(kicks.Path)).TotalSeconds) < 1,
                  "walk: a folder's Created is not the folder's");
            Check(r != null && r.Created != default(DateTime), "walk: the root has no dates");

            idx.SaveCache();
            SampleIndex back = SampleIndex.LoadCache();
            Check(back.Roots.Count == 1 && back.Files.Count == expected && back.Folders.Count == idx.Folders.Count,
                  "walk: the cache does not give back the same index");
            SampleFile backK1 = null;
            foreach (SampleFile f in back.Files) if (Same(f.Name, "kick 1.wav")) backK1 = f;
            Check(backK1 != null && k1 != null && backK1.Modified == k1.Modified && backK1.Created == k1.Created
                  && back.Roots[0].Created == r.Created,
                  "walk: the cache lost the dates");
            SampleFolder backA = back.Roots.Count == 1 ? Child(back.Roots[0], "Pack A") : null;
            Check(backA != null && backA.TotalBytes == packA && Child(backA, "Kicks") != null
                  && Child(backA, "Kicks").Files.Count == 2,
                  "walk: the cache lost weights or files");

            Check(back.Only(new List<string>(), new List<string>()).Roots.Count == 0, "walk: Only kept a removed root");
            Check(back.Only(new List<string> { lib }, new List<string> { lib }).Files.Count == 0, "walk: Only kept a disabled root");
            Check(ReferenceEquals(back.Only(new List<string> { lib }, new List<string>()), back),
                  "walk: Only made a copy although nothing changed");

            // A cache from before the dates is still read: the first walk after an update keeps
            // the AIFF flags it holds.
            using (FileStream fs = File.Create(Path.Combine(Settings.Dir, "samples.cache")))
            using (BinaryWriter w = new BinaryWriter(fs, Encoding.UTF8))
            {
                w.Write(2);
                w.Write(1); w.Write(lib); w.Write(-1); w.Write(1); w.Write(10L);
                w.Write(1); w.Write(0); w.Write("old.aif"); w.Write(10L); w.Write(true);
                w.Write(1); w.Write(0);
            }
            SampleIndex v2 = SampleIndex.LoadCache();
            Check(v2.Files.Count == 1 && v2.Files[0].Silent && v2.Files[0].Created == default(DateTime),
                  "walk: a version 2 cache must still read, without dates");

            File.WriteAllBytes(Path.Combine(Settings.Dir, "samples.cache"), new byte[] { 99, 0, 0, 0 });
            Check(SampleIndex.LoadCache().Roots.Count == 0, "walk: a cache of another version must read as empty");

            Settings s = new Settings();
            s.SampleRoots.Add(lib);
            s.DisabledSampleRoots.Add(lib);
            s.Save();
            Settings loaded = Settings.Load();
            Check(loaded.SampleRoots.Count == 1 && Same(loaded.SampleRoots[0], lib)
                  && loaded.DisabledSampleRoots.Count == 1, "walk: sample folders did not survive settings.cfg");
        }

        // ------------------------------------------------------------------ usage

        static SetEntry Set(string path, DateTime modified, params object[] samplesAndSizes)
        {
            SetEntry s = new SetEntry();
            s.Path = path;
            s.Name = Path.GetFileNameWithoutExtension(path);
            s.Modified = modified;
            int n = samplesAndSizes.Length / 2;
            s.Samples = new string[n];
            s.SampleSizes = new long[n];
            for (int i = 0; i < n; i++)
            {
                s.Samples[i] = (string)samplesAndSizes[i * 2];
                s.SampleSizes[i] = (long)samplesAndSizes[i * 2 + 1];
            }
            return s;
        }

        static SampleFile FileNamed(SampleIndex idx, string name)
        {
            foreach (SampleFile f in idx.Files) if (Same(f.Name, name)) return f;
            return null;
        }

        static SampleFolder FolderNamed(SampleIndex idx, string name)
        {
            foreach (SampleFolder f in idx.Folders) if (Same(f.Name, name)) return f;
            return null;
        }

        static void Usage()
        {
            string root = Fresh("usage");
            string lib = Path.Combine(root, "lib");
            string kick = Path.Combine(lib, @"drums\kick.wav");
            string snare = Path.Combine(lib, @"drums\snare.wav");
            long kickSize = WriteWav(kick, 100), snareSize = WriteWav(snare, 200);
            WriteWav(Path.Combine(lib, @"fx\riser.wav"), 300);
            WriteWav(Path.Combine(lib, @"fx\sub\boom.wav"), 400);

            SampleIndex idx = SampleIndex.Build(new List<string> { lib }, new List<string>(), null, CancellationToken.None);

            string copyOfSnare = Path.Combine(root, @"B Project\Samples\Imported\snare.wav");
            DateTime jan = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            List<SetEntry> sets = new List<SetEntry>
            {
                Set(Path.Combine(root, @"A Project\a.als"), jan, kick, kickSize),
                Set(Path.Combine(root, @"A Project\a v2.als"), jan.AddMonths(1), kick, kickSize),       // the same project
                Set(Path.Combine(root, @"B Project\b.als"), jan.AddMonths(2), copyOfSnare, snareSize),  // a copy
                Set(Path.Combine(root, @"C Project\c.als"), jan.AddMonths(3),
                    Path.Combine(root, @"C Project\Samples\snare.wav"), snareSize + 1),                   // same name, other size
            };

            SampleUsage u = SampleUsage.Compute(idx, sets);
            SampleFile k = FileNamed(idx, "kick.wav"), sn = FileNamed(idx, "snare.wav");
            SampleFolder drums = FolderNamed(idx, "drums"), fx = FolderNamed(idx, "fx"), sub = FolderNamed(idx, "sub");

            SampleUse ku = u.Of(k);
            Check(ku != null && ku.Projects == 1 && ku.Sets.Count == 2,
                  "usage: two versions of one project are one project and two sets");
            Check(ku != null && ku.LastUsed == jan.AddMonths(1), "usage: LastUsed is not the newest set");
            SampleUse su = u.Of(sn);
            Check(su != null && su.Projects == 1 && su.Sets.Count == 1 && su.Sets[0].Name == "b",
                  "usage: the copy Collect All left in B Project was not recognised");

            FolderUse du = u.Of(drums);
            Check(du != null && du.Used == 2 && du.Projects == 2 && du.LastUsed == jan.AddMonths(2),
                  "usage: the drums folder should have 2 used, 2 projects, last in March");
            FolderUse ru = u.Of(idx.Roots[0]);
            Check(ru != null && ru.Used == 2, "usage: the root does not add up its folders");
            Check(u.Of(fx) == null && u.Of(sub) == null, "usage: fx is not used by anybody");

            List<SampleFolder> never = u.NeverUsed(idx);
            Check(never.Count == 1 && never[0] == fx, "usage: NeverUsed must give the topmost unused folder only");

            List<SampleFile> under = u.UsedUnder(drums);
            Check(under.Count == 2 && under[0] == sn && under[1] == k,
                  "usage: equal projects - the more recently used goes first");

            List<SetEntry> newest = SampleUsage.Newest(ku != null ? ku.Sets : new List<SetEntry>());
            Check(newest.Count == 1 && newest[0].Name == "a v2", "usage: Newest keeps the newest set of a project");

            List<SampleFile> ofB = u.FilesOf(sets[2]);
            Check(ofB.Count == 1 && ofB[0] == sn, "usage: FilesOf must give the library files a set uses");
            Check(u.FilesOf(sets[3]).Count == 0 && u.FilesOf(null).Count == 0, "usage: FilesOf of a set using nothing");

            Check(SampleUsage.Compute(idx, new List<SetEntry>()).UsedFiles.Count == 0, "usage: no sets - no usage");
        }

        // ----------------------------------------------------------------- copies

        static void CopiesCheck()
        {
            string lib = Path.Combine(Fresh("copies"), "lib");
            long big = WriteWav(Path.Combine(lib, @"Pack A\kick.wav"), 500);
            WriteWav(Path.Combine(lib, @"Pack B\kick.wav"), 500);                     // the same sample again
            WriteWav(Path.Combine(lib, @"Pack B\Deep\KICK.wav"), 500);                // and a third time
            long small = WriteWav(Path.Combine(lib, @"Pack A\hat.wav"), 20);
            WriteWav(Path.Combine(lib, @"Pack C\hat.wav"), 20);                       // a smaller pair
            WriteWav(Path.Combine(lib, @"Pack C\kick.wav"), 499);                     // same name, other size
            WriteWav(Path.Combine(lib, @"Pack C\snare.wav"), 20);                     // same size, other name
            WriteWav(Path.Combine(lib, @"Dry\pad.wav"), 9000, 1);                     // a pack's Dry and Wet
            WriteWav(Path.Combine(lib, @"Wet\pad.wav"), 9000, 2);                     // takes: not copies
            // The case that broke a print of a few blocks: silence at both ends, a difference
            // only in the middle.
            byte[] quiet = new byte[200000], other = new byte[200000];
            other[100000] = 1;
            Directory.CreateDirectory(Path.Combine(lib, "Dry2"));
            Directory.CreateDirectory(Path.Combine(lib, "Wet2"));
            File.WriteAllBytes(Path.Combine(lib, @"Dry2\guitar.wav"), quiet);
            File.WriteAllBytes(Path.Combine(lib, @"Wet2\guitar.wav"), other);

            SampleIndex idx = SampleIndex.Build(new List<string> { lib }, new List<string>(), null, CancellationToken.None);
            SampleCopies c = SampleCopies.Find(idx);

            Check(c.Files.Count == 5, "copies: 5 files have a copy, got " + c.Files.Count);
            Check(c.ExtraBytes == big * 2 + small, "copies: the extra bytes are " + c.ExtraBytes + " instead of " + (big * 2 + small));
            Check(c.Files.Count == 5 && Same(c.Files[0].Name, "kick.wav") && Same(c.Files[2].Name, "KICK.wav")
                  && Same(c.Files[3].Name, "hat.wav"),
                  "copies: the group wasting the most room goes first, its copies side by side");

            SampleFile a = null, c499 = null;
            foreach (SampleFile f in idx.Files)
            {
                if (Same(f.Name, "kick.wav") && f.Folder.Name == "Pack A") a = f;
                if (Same(f.Name, "kick.wav") && f.Folder.Name == "Pack C") c499 = f;
            }
            Check(a != null && c.CopiesOf(a) == 2 && c.Others(a).Count == 2 && !c.Others(a).Contains(a),
                  "copies: a sample in three places has two others");
            Check(c.CopiesOf(c499) == 0 && c.Others(c499).Count == 0, "copies: a different size is not a copy");
            SampleFile dry = FileNamed(idx, "pad.wav");
            Check(dry != null && dry.Print != 0 && c.CopiesOf(dry) == 0,
                  "copies: Dry and Wet takes of equal name and size are different sounds, not copies");
            Check(FileNamed(idx, "snare.wav").Print == 0, "copies: a file with no namesake needs no print");
            SampleFile guitar = FileNamed(idx, "guitar.wav");
            Check(guitar != null && guitar.Print != 0 && c.CopiesOf(guitar) == 0,
                  "copies: files that differ only in the middle are not copies");

            // A print survives the cache and is kept by the next walk while the file is the same.
            idx.SaveCache();
            SampleIndex back = SampleIndex.LoadCache();
            Check(FileNamed(back, "pad.wav") != null && FileNamed(back, "pad.wav").Print == dry.Print,
                  "copies: the cache lost the prints");
            SampleIndex again = SampleIndex.Build(new List<string> { lib }, new List<string>(), null, CancellationToken.None, back);
            Check(FileNamed(again, "pad.wav").Print == dry.Print, "copies: the next walk changed an unchanged print");
            Check(c.FilesIn(idx.Roots[0]) == 5 && c.FilesIn(FolderNamed(idx, "Pack B")) == 2
                  && c.BytesIn(FolderNamed(idx, "Pack B")) == big * 2,
                  "copies: a folder counts the samples of its subtree that lie elsewhere too");
            Check(SampleCopies.Find(SampleIndex.Empty).Files.Count == 0, "copies: an empty index has none");
        }

        // ------------------------------------------------------------------- aiff

        static byte[] Be16(int v) { return new byte[] { (byte)(v >> 8), (byte)v }; }
        static byte[] Be32(long v) { return new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }; }

        /// <summary>An 80-bit IEEE extended for a whole number — AIFF keeps its sample rate so.</summary>
        static byte[] Extended80(int v)
        {
            byte[] b = new byte[10];
            if (v <= 0) return b;
            int e = 0;
            while ((1L << (e + 1)) <= v) e++;
            int exp = 16383 + e;
            ulong mant = (ulong)v << (63 - e);
            b[0] = (byte)(exp >> 8);
            b[1] = (byte)exp;
            for (int i = 0; i < 8; i++) b[2 + i] = (byte)(mant >> (56 - 8 * i));
            return b;
        }

        static void Chunk(MemoryStream ms, string id, params byte[][] parts)
        {
            int size = 0;
            foreach (byte[] p in parts) size += p.Length;
            ms.Write(Encoding.ASCII.GetBytes(id), 0, 4);
            ms.Write(Be32(size), 0, 4);
            foreach (byte[] p in parts) ms.Write(p, 0, p.Length);
            if ((size & 1) != 0) ms.WriteByte(0);
        }

        /// <summary>An AIFF — or an AIFC when compression is given — around sample bytes already
        /// in the file's own byte order.</summary>
        static string WriteAiff(string name, int channels, int bits, int rate, byte[] data, string compression)
        {
            string path = Path.Combine(Fresh("aiff-" + name), name + ".aif");
            int frames = data.Length / (channels * ((bits + 7) / 8));
            MemoryStream body = new MemoryStream();
            if (compression == null)
                Chunk(body, "COMM", Be16(channels), Be32(frames), Be16(bits), Extended80(rate));
            else
                Chunk(body, "COMM", Be16(channels), Be32(frames), Be16(bits), Extended80(rate),
                      Encoding.ASCII.GetBytes(compression), new byte[] { 0, 0 });
            Chunk(body, "SSND", new byte[8], data);
            byte[] b = body.ToArray();
            using (FileStream fs = File.Create(path))
            {
                fs.Write(Encoding.ASCII.GetBytes("FORM"), 0, 4);
                fs.Write(Be32(4 + b.Length), 0, 4);
                fs.Write(Encoding.ASCII.GetBytes(compression == null ? "AIFF" : "AIFC"), 0, 4);
                fs.Write(b, 0, b.Length);
            }
            return path;
        }

        static float[] ReadAll(AiffReader a)
        {
            List<float> all = new List<float>();
            byte[] buf = new byte[a.Channels * 4 * 3];      // small on purpose: several Read calls
            int n;
            while ((n = a.Read(buf)) > 0)
                for (int i = 0; i < n; i += 4) all.Add(BitConverter.ToSingle(buf, i));
            return all.ToArray();
        }

        static bool Near(float[] got, params float[] want)
        {
            if (got.Length != want.Length) return false;
            for (int i = 0; i < got.Length; i++)
                if (Math.Abs(got[i] - want[i]) > 1e-6f) return false;
            return true;
        }

        static void Aiff()
        {
            // 16-bit mono, big-endian: 0, 16384, -16384, 32767, -32768
            byte[] s16 = { 0x00, 0x00, 0x40, 0x00, 0xC0, 0x00, 0x7F, 0xFF, 0x80, 0x00 };
            string p16 = WriteAiff("s16", 1, 16, 44100, s16, null);
            using (AiffReader a = AiffReader.Open(p16))
            {
                Check(a != null && a.Channels == 1 && a.Rate == 44100 && a.Bits == 16 && a.Frames == 5,
                      "aiff: the header of a 16-bit mono file");
                if (a != null)
                {
                    Check(Near(ReadAll(a), 0f, 0.5f, -0.5f, 32767f / 32768f, -1f), "aiff: 16-bit samples");
                    a.Seek(3);
                    Check(Near(ReadAll(a), 32767f / 32768f, -1f), "aiff: Seek(3) must start at the fourth frame");
                }
            }

            // 24-bit stereo: (8388607, -8388608), (0, 4194304)
            byte[] s24 = { 0x7F, 0xFF, 0xFF, 0x80, 0x00, 0x00, 0x00, 0x00, 0x00, 0x40, 0x00, 0x00 };
            using (AiffReader a = AiffReader.Open(WriteAiff("s24", 2, 24, 48000, s24, null)))
                Check(a != null && a.Rate == 48000 && a.Frames == 2
                      && Near(ReadAll(a), 8388607f / 8388608f, -1f, 0f, 0.5f), "aiff: 24-bit stereo");

            // AIFC "sowt": the same 16-bit samples, little-endian
            byte[] le = { 0x00, 0x00, 0x00, 0x40, 0x00, 0xC0, 0xFF, 0x7F, 0x00, 0x80 };
            using (AiffReader a = AiffReader.Open(WriteAiff("sowt", 1, 16, 44100, le, "sowt")))
                Check(a != null && Near(ReadAll(a), 0f, 0.5f, -0.5f, 32767f / 32768f, -1f), "aiff: sowt");

            // AIFC "fl32": 0.25, -0.75 as big-endian floats
            byte[] fl = { 0x3E, 0x80, 0x00, 0x00, 0xBF, 0x40, 0x00, 0x00 };
            using (AiffReader a = AiffReader.Open(WriteAiff("fl32", 1, 32, 44100, fl, "fl32")))
                Check(a != null && Near(ReadAll(a), 0.25f, -0.75f), "aiff: fl32");

            // 8-bit: AIFF keeps it signed, unlike WAV
            using (AiffReader a = AiffReader.Open(WriteAiff("s8", 1, 8, 22050, new byte[] { 0x80, 0x00, 0x40 }, null)))
                Check(a != null && Near(ReadAll(a), -1f, 0f, 0.5f), "aiff: 8-bit is signed");

            Check(AiffReader.Open(WriteAiff("ima4", 1, 16, 44100, new byte[34], "ima4")) == null,
                  "aiff: a compressed AIFC must be refused rather than read as noise");
            string notAiff = Path.Combine(Fresh("aiff-wav"), "x.wav");
            WriteWav(notAiff, 10);
            Check(AiffReader.Open(notAiff) == null, "aiff: a WAV is not an AIFF");

            Waveform w = WaveReader.Read(p16, 32);
            Check(w.Ok, "aiff: no envelope for an AIFF");
            int ms;
            string said = MediaDecoder.Describe(p16, out ms);
            Check(said == "44.1 kHz · 16-bit · mono", "aiff: Describe says '" + said + "'");
            string wav = Path.Combine(Fresh("describe"), "x.wav");
            WriteWav(wav, 44100);
            said = MediaDecoder.Describe(wav, out ms);
            Check(said == "44.1 kHz · 16-bit · mono" && ms == 1000,
                  "aiff: Describe of a one-second WAV says '" + said + "', " + ms + " ms");

            // The walk marks what only Live can play: Ableton's own compressed AIFC.
            string dir = Fresh("aiff-index");
            File.Copy(p16, Path.Combine(dir, "plain.aif"));
            File.Copy(WriteAiff("able", 1, 16, 44100, new byte[20], "able"), Path.Combine(dir, "live.aif"));
            SampleIndex idx = SampleIndex.Build(new List<string> { dir }, new List<string>(), null, CancellationToken.None);
            SampleFile plain = FileNamed(idx, "plain.aif"), live = FileNamed(idx, "live.aif");
            Check(plain != null && plain.CanPreview && live != null && live.Silent && !live.CanPreview,
                  "aiff: the walk must mark the AIFF only Live plays");
            idx.SaveCache();
            SampleFile back = FileNamed(SampleIndex.LoadCache(), "live.aif");
            Check(back != null && back.Silent, "aiff: Silent did not survive the cache");

            // A file with the same path and size is not opened again: its flag comes from the
            // index being replaced. Spoil plain.aif without changing its size — a fresh look
            // would call it silent, the remembered one still plays.
            string spoiled = Path.Combine(dir, "plain.aif");
            File.WriteAllBytes(spoiled, new byte[new FileInfo(spoiled).Length]);
            SampleIndex again = SampleIndex.Build(new List<string> { dir }, new List<string>(), null, CancellationToken.None, idx);
            SampleIndex fresh = SampleIndex.Build(new List<string> { dir }, new List<string>(), null, CancellationToken.None);
            Check(!FileNamed(again, "plain.aif").Silent && FileNamed(fresh, "plain.aif").Silent,
                  "aiff: an unchanged AIFF must keep its flag from the previous index instead of being opened");

            // Real ones, when this machine has them: a plain AIFF from a sample library plays,
            // an Ableton-compressed one from Live's packs is refused.
            string real = FirstAiff(@"E:\Music\Samples", true) ?? FirstAiff(@"D:\Music\Samples", true);
            if (real == null) Console.WriteLine("aiff: no plain AIFF in the sample folders - that check is skipped");
            else
                using (AiffReader a = AiffReader.Open(real))
                {
                    Check(a != null && a.Frames > 0 && a.DurationMs > 0, "aiff: could not open " + real);
                    if (a != null) Check(ReadAll(a).Length == a.Frames * a.Channels, "aiff: not every frame of " + real + " was read");
                }

            string able = FirstAiff(@"E:\Music\Factory Packs", false);
            if (able == null) Console.WriteLine("aiff: no Ableton-compressed AIFF here - that check is skipped");
            else Check(!WaveReader.Read(able, 32).Ok, "aiff: an Ableton-compressed AIFF must not pretend to have a wave: " + able);
        }

        /// <summary>The first AIFF under root the reader does (readable) or does not take — at
        /// most 500 looked at.</summary>
        static string FirstAiff(string root, bool readable)
        {
            if (!Directory.Exists(root)) return null;
            int seen = 0;
            try
            {
                foreach (string f in Directory.EnumerateFiles(root, "*.aif", SearchOption.AllDirectories))
                {
                    if (Path.GetFileName(f).StartsWith("._", StringComparison.Ordinal)) continue;
                    if (AiffReader.CanRead(f) == readable) return f;
                    if (++seen >= 500) break;
                }
            }
            catch { }
            return null;
        }

        // ---------------------------------------------------------------- sources

        static void Sources()
        {
            string root = Fresh("sources");
            string samples = Path.Combine(root, "Samples"), projects = Path.Combine(root, "Projects");
            string series = Path.Combine(projects, "Series 1"), packs = Path.Combine(root, "Packs");
            foreach (string d in new string[] { samples, series, packs }) Directory.CreateDirectory(d);

            string cfg = Path.Combine(root, "Library.cfg");
            File.WriteAllText(cfg,
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<Ableton><ContentLibrary>\n"
              + "<UserFolderInfoList>\n"
              + "<UserFolderInfo Id=\"1\" Path=\"" + samples + "\" DisplayName=\"Samples E:\\\" IconName=\"\" />\n"
              + "<UserFolderInfo Id=\"2\" Path=\"" + series + "\" DisplayName=\"Series 1\" IconName=\"\" />\n"
              + "<UserFolderInfo Id=\"3\" Path=\"" + Path.Combine(root, "gone") + "\" DisplayName=\"Gone\" IconName=\"\" />\n"
              + "</UserFolderInfoList>\n"
              + "<PreferredFactoryPacksInstallationPath Value=\"" + packs + "\" />\n"
              + "</ContentLibrary></Ableton>\n", Encoding.UTF8);

            LiveEnvironment env = new LiveEnvironment();
            env.ReadLibraryConfig(cfg);
            Check(env.Places.Count == 2, "sources: a Place whose folder is gone must be left out, got " + env.Places.Count);
            Check(env.Places.Count > 0 && env.Places[0].Key == "Samples E:\\" && Same(env.Places[0].Value, samples),
                  "sources: the Place keeps Live's own name");
            Check(Same(env.PacksFolder, packs), "sources: PreferredFactoryPacksInstallationPath was not read");

            List<RootsDialog.Suggestion> s = RootsDialog.LiveSuggestions(env, new List<string> { projects });
            RootsDialog.Suggestion sam = null, ser = null, pk = null;
            foreach (RootsDialog.Suggestion x in s)
            {
                if (Same(x.Path, samples)) sam = x;
                if (Same(x.Path, series)) ser = x;
                if (Same(x.Path, packs)) pk = x;
            }
            Check(sam != null && !sam.Projects, "sources: a sample Place is offered as samples");
            Check(ser != null && ser.Projects, "sources: a Place inside a project root is marked as projects");
            Check(pk != null && pk.Title == "Packs" && pk.StartsGroup, "sources: the packs folder opens the second group");
        }

        // -------------------------------------------------------------------- fit

        /// <summary>Names and paths too long for their cell keep what tells them apart: the
        /// end of a name, the last folder of a path.</summary>
        static void Fit()
        {
            System.Drawing.Font f = Theme.FBody;
            string vol1 = "[SAOL] ASCENDING DRUM KIT VOL.1", vol2 = "[SAOL] ASCENDING DRUM KIT VOL.2";
            int w = RowListView.TextW(vol1, f) * 2 / 3;
            string a = RowListView.FitMiddle(vol1, f, w), b = RowListView.FitMiddle(vol2, f, w);
            Check(a.EndsWith("VOL.1") && a.Contains("…") && a != b && RowListView.TextW(a, f) <= w,
                  "fit: a name cut in the middle keeps its end, got '" + a + "'");
            Check(RowListView.FitMiddle("Kick.wav", f, 1000) == "Kick.wav", "fit: a name that fits is left alone");

            // "&" is a character here, not a mnemonic: measured the default way it vanished,
            // and a path with it overflowed the cell it had been cut for.
            Check(RowListView.TextW("FX & PERCS", f) > System.Windows.Forms.TextRenderer.MeasureText("FX & PERCS", f).Width,
                  "fit: '&' must count in a cell's width");

            string path = vol1 + @"\[S] FX & PERCS";
            int pw = RowListView.TextW(path, f) * 7 / 10;
            string p = RowListView.FitPath(path, f, pw);
            Check(p.EndsWith(@"\[S] FX & PERCS") && p.Contains("VOL.1") && RowListView.TextW(p, f) <= pw,
                  "fit: a two-folder path keeps the last folder and the end of the first, got '" + p + "'");

            string pack = @"Samples\Avant Riddim Drop Ammunition Vol 1";
            int kw = RowListView.TextW(pack, f) * 7 / 10;
            string k = RowListView.FitPath(pack, f, kw);
            Check(k.StartsWith(@"Samples\") && k.EndsWith("Vol 1") && k.Contains("…") && RowListView.TextW(k, f) <= kw,
                  "fit: a short first folder stays whole and the long name is cut, got '" + k + "'");
        }

        // ---------------------------------------------------------------- catalog

        const BindingFlags Any = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        static object Field(object o, string name) { return o.GetType().GetField(name, Any).GetValue(o); }

        static object Call(object o, string name, params object[] args)
        {
            Type[] types = new Type[args.Length];
            for (int i = 0; i < args.Length; i++) types[i] = args[i].GetType();
            return o.GetType().GetMethod(name, Any, null, types, null).Invoke(o, args);
        }

        static void Catalog()
        {
            ScanKeepsItsRoots();
            Views();
            PanelPicture();

            // A project folder whose name only begins like another one's is not inside it: "Song
            // Project 2" is not in "Song Project".
            Check(!SampleIndex.Inside(@"C:\Music\Song Project 2\Samples\x.wav", @"C:\Music\Song Project")
                  && SampleIndex.Inside(@"C:\Music\Song Project\Samples\x.wav", @"C:\Music\Song Project"),
                  "catalog: a folder that only begins like the project folder counts as inside it");
        }

        /// <summary>A folder dropped onto the window, or picked in Folders, while the scan walks
        /// the roots: the walk under way must not break, and it finishes the roots it began
        /// with. The progress report is the moment the window gets to run in the middle of the
        /// walk, so that is where the folder is added here.</summary>
        static void ScanKeepsItsRoots()
        {
            string root = Fresh("scan-roots");
            string one = Path.Combine(root, "one"), two = Path.Combine(root, "two"), late = Path.Combine(root, "late");
            // Sixteen: the walk reports progress once per sixteen sets found.
            for (int i = 0; i < 16; i++) WriteAls(Path.Combine(one, "p" + i + @" Project\s" + i + ".als"));
            WriteAls(Path.Combine(two, @"t Project\t.als"));
            WriteAls(Path.Combine(late, @"l Project\l.als"));

            Settings s = new Settings();
            s.Roots.Add(one);
            s.Roots.Add(two);
            bool added = false;
            string failure = null;
            ProjectIndex idx = new ProjectIndex();
            try
            {
                idx.Scan(s, delegate { if (!added) { added = true; s.Roots.Add(late); } }, CancellationToken.None);
            }
            catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }

            Check(added, "catalog: the walk reported no progress - the check proves nothing");
            Check(failure == null, "catalog: a root added mid-scan broke the scan: " + failure);
            Check(idx.Sets.Count == 17, "catalog: the scan must finish the roots it began with, got " + idx.Sets.Count + " sets");
        }

        /// <summary>
        /// The main window, never shown, driven the way the search field and the keyboard drive
        /// it. Its catalog comes from where OnShown takes it — the cache of the scan made here.
        /// </summary>
        static void Views()
        {
            string root = Fresh("views");
            WriteAls(Path.Combine(root, @"Song Project\alpha.als"));
            WriteAls(Path.Combine(root, @"Song Project 2\beta.als"));
            Settings s = new Settings();
            s.Roots.Add(root);
            new ProjectIndex().Scan(s, null, CancellationToken.None);

            MainForm m = new MainForm();
            try
            {
                ProjectIndex idx = (ProjectIndex)Field(m, "_index");
                idx.LoadFromCache();
                SetEntry alpha = null;
                foreach (SetEntry e in idx.Sets) if (e.Name == "alpha") alpha = e;
                Check(alpha != null && idx.Sets.Count == 2, "catalog: setup - the two sets did not come back from the cache");
                if (alpha == null) return;
                ProjectMeta.Set(alpha.ProjectDir, new string[] { "riddim" }, "second drop still empty");

                Segmented mode = (Segmented)Field(m, "_mode");
                TextBox search = ((FieldBox)Field(m, "_search")).Box;
                HomeView home = (HomeView)Field(m, "_home");
                Call(m, "Refill");                      // the first fill OnShown does, on Home

                // Home finds what Sets finds: a set by its tag, by its note.
                search.Text = "riddim";
                Check(home.VisibleSets().Count == 1 && home.VisibleSets()[0] == alpha,
                      "catalog: Home does not find a set by its tag");
                search.Text = "second drop";
                Check(home.VisibleSets().Count == 1 && home.VisibleSets()[0] == alpha,
                      "catalog: Home does not find a set by its note");

                // A tile the search has hidden is no selection: Enter would open it in Live.
                search.Text = "";
                home.Select(alpha);
                Check(Call(m, "SelectedSet") == alpha, "catalog: setup - the tile did not get selected");
                search.Text = "beta";
                Check(Call(m, "SelectedSet") == null,
                      "catalog: Home keeps a selection the search has hidden - Enter opens it in Live");

                // The set name in the mini player leads to the set even when the search hides it.
                mode.SelectedIndex = 1;
                search.Text = "beta";
                Call(m, "NavigateToSet", alpha);
                Check(search.Text.Length == 0 && Call(m, "SelectedSet") == alpha,
                      "catalog: going to the playing set keeps the search that hides it");

                // A scan asked for while another one runs is done afterwards, not dropped: that
                // is how a folder added mid-scan gets scanned at all.
                FieldInfo scanning = typeof(MainForm).GetField("_scanning", Any);
                scanning.SetValue(m, true);
                Call(m, "StartScan", true);
                Check((bool)Field(m, "_rescanPending"), "catalog: a scan asked for during another one is dropped");
                scanning.SetValue(m, false);
            }
            finally { m.Dispose(); }
        }

        /// <summary>
        /// The panel draws a set's arrangement in the background. Pick another set while that
        /// picture is on its way, and the late picture must not go up under the other set's
        /// name. Nothing is timed here: the answer is marshalled back through BeginInvoke, so
        /// it cannot land before the messages are pumped.
        /// </summary>
        static void PanelPicture()
        {
            DetailPanel p = new DetailPanel();
            p.Size = new Size(332, 900);
            IntPtr handle = p.Handle;                  // BeginInvoke needs one; the panel is never shown
            SetEntry a = new SetEntry(), b = new SetEntry();
            a.Name = "a"; a.Path = @"C:\nowhere\a Project\a.als";
            b.Name = "b"; b.Path = @"C:\nowhere\b Project\b.als";
            Arrangement ra = OneClip(a.Path, 0, 5), rb = OneClip(b.Path, 16, 20);

            p.Show(a); p.OnArrangement(ra);
            Paint(p);                                  // a's picture starts drawing...
            p.Show(b); p.OnArrangement(rb);            // ...and b is picked before it arrives
            Settle(p);
            Paint(p);
            Settle(p);

            Bitmap shown = (Bitmap)Field(p, "_thumb");
            Size size = (Size)Field(p, "_thumbSize");
            RenderOptions o = new RenderOptions();
            o.Dpi = p.DeviceDpi / 96f;
            o.MaxLane = 10;
            using (Bitmap want = ArrangementRender.ToBitmap(rb, size.Width, size.Height, o))
                Check(shown != null && SamePixels(shown, want),
                      "catalog: the panel shows the arrangement of the set picked before");
            p.Dispose();
        }

        static Arrangement OneClip(string path, double start, int color)
        {
            Arrangement a = new Arrangement();
            a.Path = path;
            TrackLane t = new TrackLane();
            ClipBlock c = new ClipBlock();
            c.Start = start;
            c.End = start + 16;
            c.Color = color;
            t.Clips.Add(c);
            a.Tracks.Add(t);
            a.ClipCount = 1;
            a.End = 32;
            return a;
        }

        static void Paint(Control c)
        {
            using (Bitmap bmp = new Bitmap(c.Width, c.Height))
                c.DrawToBitmap(bmp, new Rectangle(0, 0, c.Width, c.Height));
        }

        /// <summary>Pump messages until the picture drawn in the background has come back — five
        /// seconds at most.</summary>
        static void Settle(DetailPanel p)
        {
            Stopwatch sw = Stopwatch.StartNew();
            while ((bool)Field(p, "_thumbRendering") && sw.ElapsedMilliseconds < 5000)
            {
                Application.DoEvents();
                Thread.Sleep(5);
            }
        }

        static bool SamePixels(Bitmap x, Bitmap y)
        {
            if (x.Width != y.Width || x.Height != y.Height) return false;
            for (int j = 0; j < x.Height; j++)
                for (int i = 0; i < x.Width; i++)
                    if (x.GetPixel(i, j).ToArgb() != y.GetPixel(i, j).ToArgb()) return false;
            return true;
        }

        // ------------------------------------------------------------------- data

        /// <summary>
        /// The data folder's files are written whole or not at all, and a file that would not
        /// be read at start is not written over: notes.cfg holds what nothing can rebuild.
        /// "Would not be read" is played here by another handle holding the file shut.
        /// </summary>
        static void Data()
        {
            string notes = Path.Combine(Settings.Dir, "notes.cfg");
            ForgetNotes();
            ProjectMeta.Set(@"C:\data\A Project", new string[] { "first" }, "");
            ProjectMeta.Set(@"C:\data\A Project", new string[] { "second" }, "");
            Check(File.Exists(notes + ".bak") && File.ReadAllText(notes + ".bak").Contains("first")
                  && File.ReadAllText(notes).Contains("second"),
                  "data: a save does not keep the previous notes.cfg as notes.cfg.bak");
            Check(!File.Exists(notes + ".tmp"), "data: a save leaves its temporary file behind");

            // Another program holds notes.cfg while it is read at start; later the file is free
            // and a tag gets saved. What was on disk must still be there.
            File.WriteAllText(notes, "tags=C:\\data\\Old Project\tkeep\r\n", new UTF8Encoding(false));
            ForgetNotes();
            using (new FileStream(notes, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                ProjectMeta.TagsOf(@"C:\data\Old Project");
            ProjectMeta.Set(@"C:\data\New Project", new string[] { "new" }, "");
            Check(File.ReadAllText(notes).Contains("keep"), "data: notes.cfg that would not be read got written over");
            ForgetNotes();

            // The same with the settings — the roots would be gone.
            string cfg = Path.Combine(Settings.Dir, "settings.cfg");
            File.WriteAllText(cfg, "root=C:\\data\\Projects\r\n", new UTF8Encoding(false));
            Settings s;
            using (new FileStream(cfg, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                s = Settings.Load();
            s.Save();
            Check(File.ReadAllText(cfg).Contains(@"root=C:\data\Projects"),
                  "data: settings.cfg that would not be read got written over");

            // A read that fails halfway — a drive pulled out, here a locked range past the
            // header — comes back as "no wave": the player asks on a pool thread, where an
            // exception ends the program.
            string aif = WriteAiff("halfway", 1, 16, 44100, new byte[256 * 1024], null);
            using (FileStream holder = new FileStream(aif, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                long from = 160 * 1024;
                holder.Lock(from, holder.Length - from);
                string failure = null;
                Waveform w = null;
                try { w = WaveReader.Read(aif, 32); }
                catch (Exception ex) { failure = ex.GetType().Name + ": " + ex.Message; }
                holder.Unlock(from, holder.Length - from);
                Check(failure == null && w != null && !w.Ok,
                      "data: a read failing halfway throws out of WaveReader: " + failure);
            }

            InventoryAtOnce();
        }

        /// <summary>
        /// Loads of the plugin inventory at once — the settings window makes two the moment it
        /// opens, and a scan may be loading besides. They used to share one cache of file
        /// checks, and a HashSet written from several threads can loop forever: measured, the
        /// threads spinning and the window "Reading…" for good. The race is a matter of chance,
        /// so it is given room: four loads a round, ten rounds — the shared cache hung by the
        /// fifth round on every try. Needs Live's own database on this machine; without one
        /// there is nothing to race over.
        /// </summary>
        static void InventoryAtOnce()
        {
            int alone = PluginInventory.Load(new Settings()).All.Count;
            if (alone == 0)
            {
                Console.WriteLine("data: no plugin database on this machine - the loads-at-once check is skipped");
                return;
            }
            for (int round = 0; round < 10; round++)
            {
                int[] got = new int[4];
                Thread[] ts = new Thread[got.Length];
                for (int i = 0; i < ts.Length; i++)
                {
                    int k = i;
                    ts[k] = new Thread(delegate () { try { got[k] = PluginInventory.Load(new Settings()).All.Count; } catch { got[k] = -1; } });
                    ts[k].IsBackground = true;      // a looping one must not keep the bench alive
                    ts[k].Start();
                }
                bool done = true, right = true;
                foreach (Thread t in ts) done &= t.Join(8000);
                foreach (int g in got) right &= g == alone;
                Check(done && right, "data: plugin loads at once " + (done ? "disagree with a lone load" : "never finished"));
                if (!done || !right) return;
            }
        }

        // ----------------------------------------------------------------- visual

        static void Visual()
        {
            EnglishNumbers();
            RowTextFades();
            TileTextFades();
            FilterCount();
        }

        /// <summary>The interface is English, and so are its numbers whatever the machine's
        /// language: on a Russian Windows the preview said "128,5 BPM".</summary>
        static void EnglishNumbers()
        {
            CultureInfo was = Thread.CurrentThread.CurrentCulture;
            Thread.CurrentThread.CurrentCulture = new CultureInfo("ru-RU");
            try
            {
                string tb = (string)typeof(OverviewPanel).GetMethod("Bytes", BindingFlags.Static | BindingFlags.NonPublic)
                                     .Invoke(null, new object[] { 3L << 39 });          // 1.5 TB
                Check(tb == "1.5 TB", "visual: the library size reads '" + tb + "' on a Russian machine");

                SetEntry set = new SetEntry();
                set.Name = "x";
                set.Path = @"C:\nowhere\x Project\x.als";
                Control host = new Control();
                IntPtr handle = host.Handle;               // the loader answers through it
                using (PreviewDialog d = new PreviewDialog(set, new ArrangementLoader(host)))
                {
                    Arrangement a = OneClip(set.Path, 0, 5);
                    a.Tempo = 128.5;
                    typeof(PreviewDialog).GetField("_arr", Any).SetValue(d, a);
                    string sub = (string)Call(d, "Subtitle");
                    Check(sub.StartsWith("128.5 BPM"), "visual: the preview's tempo reads '" + sub + "' on a Russian machine");
                }
                host.Dispose();
            }
            finally { Thread.CurrentThread.CurrentCulture = was; }
        }

        /// <summary>
        /// Rows rising into place fade in, their text too. Text is drawn by GDI, which ignores a
        /// colour's alpha: faded through it, the text stood at full strength from the first
        /// frame while the rest of the row was still coming up.
        /// </summary>
        static void RowTextFades()
        {
            RowListView list = new RowListView();
            list.Size = new Size(600, 200);
            list.SetColumns(new Column("Name", 0));
            RowData r = new RowData();
            r.Cells = new string[] { "WWWWWWWWWW" };
            r.Marks.Add(new CellMark(0, Theme.Red, "missing"));
            list.SetRows(new List<RowData> { r }, true);
            ((float[])Field(list, "_rowEntrance"))[0] = 0.3f;      // a third of the way in
            int header = (int)typeof(RowListView).GetProperty("HeaderHeight", Any).GetValue(list, null);
            int brightest = 0;
            using (Bitmap bmp = new Bitmap(list.Width, list.Height))
            {
                list.DrawToBitmap(bmp, new Rectangle(0, 0, list.Width, list.Height));
                for (int y = header + 1; y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        brightest = Math.Max(brightest, Math.Max(c.R, Math.Max(c.G, c.B)));
                    }
            }
            list.Dispose();
            Check(brightest < 0x90, "visual: a row's text stands at full strength while the row is a third of the way in ("
                                    + brightest + " of 255)");
        }

        /// <summary>
        /// Home fades its heading and tiles in the same way, the "New Live Set" card among them.
        /// A tenth of the way in, every word under the year overview is still close to what it
        /// lies on; the dim grey of the dates, unfaded, is already brighter than the limit.
        /// </summary>
        static void TileTextFades()
        {
            HomeView home = new HomeView();
            home.Size = new Size(1200, 1400);
            ProjectIndex idx = new ProjectIndex();
            SetEntry s = new SetEntry();
            s.Name = "WWWWWWWWWW";
            s.Path = @"C:\nowhere\W Project\W.als";
            s.Modified = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
            idx.Sets.Add(s);
            home.Index = idx;
            home.Rebuild(false);

            float[] entrance = (float[])Field(home, "_tileEntrance");
            for (int i = 0; i < entrance.Length; i++) entrance[i] = 0.1f;
            System.Collections.IList heads = (System.Collections.IList)Field(home, "_heads");
            int top = int.MaxValue;
            foreach (object h in heads)
            {
                h.GetType().GetField("Alpha").SetValue(h, 0.1f);
                top = Math.Min(top, ((Rectangle)h.GetType().GetField("Bounds").GetValue(h)).Top);
            }

            int brightest = 0;
            using (Bitmap bmp = new Bitmap(home.Width, home.Height))
            {
                home.DrawToBitmap(bmp, new Rectangle(0, 0, home.Width, home.Height));
                for (int y = Math.Max(0, top); y < bmp.Height; y++)
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        brightest = Math.Max(brightest, Math.Max(c.R, Math.Max(c.G, c.B)));
                    }
            }
            home.Dispose();
            Check(heads.Count == 1 && entrance.Length == 2, "visual: setup - Home did not lay out its heading and two tiles");
            Check(brightest < 0x50, "visual: Home's words stand brighter than the tiles they lie on a tenth of the way in ("
                                    + brightest + " of 255)");
        }

        /// <summary>
        /// The filters window says how many will show, and it is the number the list behind it
        /// shows: the search counts too, and with one row per folder the versions of a project
        /// are one row. It counted every version and left the search out.
        /// </summary>
        static void FilterCount()
        {
            List<SetEntry> sets = new List<SetEntry>();
            foreach (string p in new string[] { @"C:\f\A Project\a v1.als", @"C:\f\A Project\a v2.als", @"C:\f\B Project\b.als" })
            {
                SetEntry s = new SetEntry();
                s.Path = p;
                s.Name = Path.GetFileNameWithoutExtension(p);
                sets.Add(s);
            }
            using (FiltersDialog d = new FiltersDialog(new SetFilter(), sets, new List<string>(), "a v", true))
                Check((int)Field(d, "_matches") == 1,
                      "visual: the filters count " + Field(d, "_matches") + " where the list shows 1 row");
        }

        // ------------------------------------------------------------------ texts

        static void Texts()
        {
            PinMovesRow();
            PluginCountInLog();
            VersionTimes();
            PreviewCounts();
        }

        /// <summary>
        /// Q with the pinned first: the row goes up to the pinned at once, and the selection goes
        /// with it. It used to light its star and stay where it was until the next rebuild.
        /// </summary>
        static void PinMovesRow()
        {
            string root = Fresh("pin");
            string older = Path.Combine(root, @"Old Project\old.als");
            WriteAls(older);
            WriteAls(Path.Combine(root, @"New Project\new.als"));
            File.SetLastWriteTimeUtc(older, new DateTime(2025, 1, 1, 12, 0, 0, DateTimeKind.Utc));
            Settings s = new Settings();
            s.Roots.Add(root);
            new ProjectIndex().Scan(s, null, CancellationToken.None);

            MainForm m = new MainForm();
            SetEntry old = null;
            try
            {
                ((ProjectIndex)Field(m, "_index")).LoadFromCache();
                ((Settings)Field(m, "_settings")).PinnedFirst = true;
                ((Segmented)Field(m, "_mode")).SelectedIndex = 1;          // Sets
                Call(m, "Refill");
                RowListView list = (RowListView)Field(m, "_list");
                foreach (RowData r in list.Rows)
                {
                    SetEntry e = r.Tag as SetEntry;
                    if (e != null && e.Name == "old") old = e;
                }
                Check(list.Rows.Count == 2 && old != null && list.Rows[1].Tag == old,
                      "texts: setup - the older set should be the second of two rows");
                if (old == null) return;

                SetEntry target = old;
                list.SelectRow(delegate (RowData r) { return r.Tag == target; });
                Call(m, "TogglePinAndRefresh", old);
                Check(list.Rows[0].Tag == old, "texts: Q with the pinned first leaves the row where it was");
                Check(Call(m, "SelectedSet") == old, "texts: the selection does not go with the pinned row");
            }
            finally
            {
                if (old != null && HomeStore.IsPinned(old.Path)) HomeStore.TogglePin(old.Path);
                m.Dispose();
            }
        }

        /// <summary>
        /// The log says how many plugins there are — the number the Plugins tab shows. It wrote
        /// the sum of every Live install's snapshot before they were merged: 6446 against 736.
        /// </summary>
        static void PluginCountInLog()
        {
            Diag.Start();
            PluginInventory inv = PluginInventory.Load(new Settings());
            if (inv.All.Count == 0)
            {
                Console.WriteLine("texts: no plugin database on this machine - the log count check is skipped");
                return;
            }
            string line = null;
            foreach (string l in File.ReadAllLines(Diag.LogPath))
                if (l.Contains("  plugins: ") && l.Contains(" from ")) line = l;
            Check(line != null && line.Contains("plugins: " + inv.All.Count + " from "),
                  "texts: the log says '" + line + "' where the list has " + inv.All.Count);
        }

        /// <summary>Two versions saved the same day read apart in the panel: the time joins the
        /// date where the date alone repeats, and only there.</summary>
        static void VersionTimes()
        {
            DateTime day = new DateTime(2026, 9, 25, 10, 0, 0, DateTimeKind.Local);
            SetEntry a = new SetEntry(), b = new SetEntry(), c = new SetEntry();
            a.Modified = day.ToUniversalTime();
            b.Modified = day.AddHours(4).ToUniversalTime();
            c.Modified = day.AddDays(-3).ToUniversalTime();
            List<SetEntry> all = new List<SetEntry> { a, b, c };
            string sa = DetailPanel.VersionStamp(a, all), sb = DetailPanel.VersionStamp(b, all);
            Check(sa != sb, "texts: two versions of one day read the same: " + sa);
            Check(DetailPanel.VersionStamp(c, all) == "2026-09-22",
                  "texts: a version alone on its day reads " + DetailPanel.VersionStamp(c, all));
        }

        /// <summary>"1 track", not "1 tracks", in the preview's line under the name.</summary>
        static void PreviewCounts()
        {
            SetEntry set = new SetEntry();
            set.Name = "x";
            set.Path = @"C:\nowhere\x Project\x.als";
            Control host = new Control();
            IntPtr handle = host.Handle;               // the loader answers through it
            using (PreviewDialog d = new PreviewDialog(set, new ArrangementLoader(host)))
            {
                typeof(PreviewDialog).GetField("_arr", Any).SetValue(d, OneClip(set.Path, 0, 5));
                string sub = (string)Call(d, "Subtitle");
                Check(sub.EndsWith("1 track   ·   1 clip"), "texts: the preview counts '" + sub + "'");
            }
            host.Dispose();
        }

        /// <summary>Make ProjectMeta read notes.cfg again, as at the next start — it keeps what
        /// it read in statics for the life of the process.</summary>
        static void ForgetNotes()
        {
            const BindingFlags st = BindingFlags.Static | BindingFlags.NonPublic;
            typeof(ProjectMeta).GetField("_loaded", st).SetValue(null, false);
            FieldInfo unread = typeof(ProjectMeta).GetField("_unread", st);
            if (unread != null) unread.SetValue(null, false);
            ((System.Collections.IDictionary)typeof(ProjectMeta).GetField("_byDir", st).GetValue(null)).Clear();
        }

        // ------------------------------------------------------------------- real

        /// <summary>No checks — numbers to look at on a real library: how long the walk and
        /// the usage take and what they found.</summary>
        static void Real(string[] args)
        {
            if (args.Length < 3)
            {
                Console.WriteLine("usage: LibraryTest.exe real <projects folder> <sample folder> [more sample folders]");
                return;
            }
            Settings s = new Settings();
            s.Roots.Add(args[1]);
            Stopwatch sw = Stopwatch.StartNew();
            ProjectIndex sets = new ProjectIndex();
            sets.Scan(s, null, CancellationToken.None);
            Console.WriteLine("sets: " + sets.Sets.Count + " in " + sw.ElapsedMilliseconds + " ms");

            List<string> roots = new List<string>();
            for (int i = 2; i < args.Length; i++) roots.Add(args[i]);
            sw.Restart();
            SampleIndex idx = SampleIndex.Build(roots, new List<string>(), null, CancellationToken.None);
            Console.WriteLine("walk: " + idx.TotalSamples + " samples, " + idx.Folders.Count + " folders, "
                              + MainForm.SizeMB(idx.TotalBytes) + " in " + sw.ElapsedMilliseconds + " ms");

            sw.Restart();
            idx.SaveCache();
            long save = sw.ElapsedMilliseconds;
            sw.Restart();
            SampleIndex.LoadCache();
            Console.WriteLine("cache: save " + save + " ms, load " + sw.ElapsedMilliseconds + " ms, "
                              + new FileInfo(Path.Combine(Settings.Dir, "samples.cache")).Length / 1024 + " KB");

            sw.Restart();
            SampleUsage u = SampleUsage.Compute(idx, sets.Sets);
            Console.WriteLine("usage: " + u.UsedFiles.Count + " files used, " + u.NeverUsed(idx).Count
                              + " never-used folders, in " + sw.ElapsedMilliseconds + " ms");

            sw.Restart();
            SampleCopies c = SampleCopies.Find(idx);
            int printed = 0;
            foreach (SampleFile f in idx.Files) if (f.Print != 0) printed++;
            Console.WriteLine("copies: " + c.Files.Count + " files are copies, of " + printed
                              + " with a namesake of the same size; " + MainForm.SizeMB(c.ExtraBytes)
                              + " extra, in " + sw.ElapsedMilliseconds + " ms");

            sw.Restart();
            SampleIndex.Build(roots, new List<string>(), null, CancellationToken.None, idx);
            Console.WriteLine("walk again, AIFF flags and prints kept: " + sw.ElapsedMilliseconds + " ms");
        }
    }
}
