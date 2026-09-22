using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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

            if (_checks == 0)
            {
                Console.WriteLine("usage: LibraryTest.exe all | index | walk | usage | aiff | sources | real <projects> <samples...>");
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
        static long WriteWav(string path, int frames)
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
                w.Write(new byte[data]);
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

            idx.SaveCache();
            SampleIndex back = SampleIndex.LoadCache();
            Check(back.Roots.Count == 1 && back.Files.Count == expected && back.Folders.Count == idx.Folders.Count,
                  "walk: the cache does not give back the same index");
            SampleFolder backA = back.Roots.Count == 1 ? Child(back.Roots[0], "Pack A") : null;
            Check(backA != null && backA.TotalBytes == packA && Child(backA, "Kicks") != null
                  && Child(backA, "Kicks").Files.Count == 2,
                  "walk: the cache lost weights or files");

            Check(back.Only(new List<string>(), new List<string>()).Roots.Count == 0, "walk: Only kept a removed root");
            Check(back.Only(new List<string> { lib }, new List<string> { lib }).Files.Count == 0, "walk: Only kept a disabled root");
            Check(ReferenceEquals(back.Only(new List<string> { lib }, new List<string>()), back),
                  "walk: Only made a copy although nothing changed");

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

            Check(SampleUsage.Compute(idx, new List<SetEntry>()).UsedFiles.Count == 0, "usage: no sets - no usage");
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
        }
    }
}
