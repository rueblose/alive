using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using AbletonManager;

namespace Reel
{
    /// <summary>
    /// Консольный прогон движка по настоящему проекту: снимки, история, разница между
    /// соседними версиями, отчёт о зависимостях. Окно тут ни при чём — проверяется то,
    /// что под ним. Склад берётся тот же, что и у окна: <проект>\Backup\Alive.
    ///
    /// Сборка: см. proto\test\build-test.cmd. В сборку прототипа не попадает — csc
    /// разворачивает proto\*.cs без подпапок.
    /// </summary>
    internal static class ProtoTest
    {
        static int Main(string[] args)
        {
            if (args.Length < 1)
            {
                Console.WriteLine("usage: ReelTest.exe <project folder>");
                return 2;
            }

            string projectDir = args[0];

            Console.WriteLine("project: " + projectDir);
            Console.WriteLine("store:   " + SnapshotStore.RootFor(projectDir));
            Console.WriteLine();

            SnapshotStore store = SnapshotStore.Open(projectDir);

            foreach (string als in Directory.GetFiles(projectDir, "*.als"))
            {
                Snapshot s = store.Capture(als, "current file");
                Console.WriteLine("captured " + Path.GetFileName(als) + ": "
                                  + (s == null ? "no change" : s.Short));
            }

            Console.WriteLine();
            Console.WriteLine("HISTORY  (" + store.Describe() + ")");
            foreach (Snapshot s in store.Entries)
                Console.WriteLine("  " + s.Short + "  "
                    + s.Time.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    + "  " + SnapshotStore.Size(s.Size).PadLeft(8)
                    + "  " + s.Source + "  |  " + s.Message);

            // ------------------------------------------------------------- разница
            Console.WriteLine();
            Console.WriteLine("DIFFS");
            for (int i = 0; i < store.Entries.Count; i++)
            {
                Snapshot newer = store.Entries[i];
                Snapshot older = store.Previous(newer);
                if (older == null) continue;

                Console.WriteLine();
                Console.WriteLine("  " + older.Short + " -> " + newer.Short
                    + "   (" + older.Time.ToString("HH:mm", CultureInfo.InvariantCulture)
                    + " -> " + newer.Time.ToString("HH:mm", CultureInfo.InvariantCulture) + ")");

                SetModel a = SetModel.Read(older.ObjectPath);
                SetModel b = SetModel.Read(newer.ObjectPath);
                if (a.Error != null) Console.WriteLine("    ! old: " + a.Error);
                if (b.Error != null) Console.WriteLine("    ! new: " + b.Error);

                foreach (DiffLine l in SetDiff.Compare(a, b))
                    Console.WriteLine("    " + new string(' ', l.Indent * 2)
                        + Glyph(l.Kind) + l.Text);
            }

            // ------------------------------------------------------------- модель
            Console.WriteLine();
            Console.WriteLine("MODEL of newest snapshot");
            if (store.Entries.Count > 0)
            {
                SetModel m = SetModel.Read(store.Entries[0].ObjectPath);
                Console.WriteLine("  " + m.Creator + "   " + m.Tempo.ToString("0.##", CultureInfo.InvariantCulture)
                    + " BPM   key " + (m.Key.Length > 0 ? m.Key : "-")
                    + "   tracks " + m.Tracks.Count + "   clips " + m.TotalClips
                    + "   notes " + m.TotalNotes + "   devices " + m.DeviceCount);
                foreach (TrackEntry t in m.Tracks)
                {
                    List<string> devs = new List<string>();
                    foreach (DeviceEntry d in t.Devices) devs.Add(d.Label + (d.On ? "" : " (off)"));
                    Console.WriteLine("    [" + t.Kind.PadRight(6) + "] " + t.Label.PadRight(22)
                        + " vol " + TrackEntry.Db(t.Volume).PadLeft(8)
                        + "  clips " + (t.ArrClips + t.SessionClips).ToString().PadLeft(3)
                        + "  notes " + t.Notes.ToString().PadLeft(5)
                        + "  samples " + t.Samples.Count.ToString().PadLeft(3)
                        + "  " + string.Join(" > ", devs.ToArray()));
                }
            }

            // ------------------------------------------------------------- зависимости
            Console.WriteLine();
            Console.WriteLine("DEPENDENCIES of newest snapshot");
            LiveEnvironment env = LiveEnvironment.Detect();
            PluginInventory inv = PluginInventory.Load();
            Console.WriteLine("  live install: " + (env.InstallDir.Length > 0 ? env.InstallDir : "not found"));
            Console.WriteLine("  plugin db:    " + (inv.IsEmpty ? "empty - " + inv.Error : inv.All.Count + " plugins"));
            Console.WriteLine();

            if (store.Entries.Count > 0)
            {
                DependencyReport rep = DependencyReport.Build(
                    store.Entries[0].ObjectPath, projectDir, env, inv, store.Entries[0].Source);
                Console.WriteLine(rep.ToText());
            }

            return 0;
        }

        static string Glyph(DiffKind k)
        {
            switch (k)
            {
                case DiffKind.Header: return "# ";
                case DiffKind.Added: return "";
                case DiffKind.Removed: return "";
                case DiffKind.Changed: return "~ ";
                default: return "  ";
            }
        }
    }
}
