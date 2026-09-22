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
            Console.WriteLine("real: added in Task 3");
        }
    }
}
