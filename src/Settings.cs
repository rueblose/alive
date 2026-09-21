using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace AbletonManager
{
    /// <summary>Search roots and small settings. The format is line-based so the file can be
    /// edited by hand.</summary>
    public sealed class Settings
    {
        public readonly List<string> Roots = new List<string>();
        // A subset of Roots temporarily excluded from scanning — the folder stays in the list
        // (and in the UI), but Scan() skips it until it is put back.
        public readonly List<string> DisabledRoots = new List<string>();

        /// <summary>
        /// Columns of the sets list: visibility, order and widths in one string of the form
        /// "Set,Modified:150,BPM:81,…". Empty means the default set. It is stored as is; the
        /// list itself parses and builds that string — the settings have no business knowing
        /// its format.
        /// </summary>
        public string SetColumns = "";

        /// <summary>The same for the plugins table — it is configurable on equal terms with the
        /// sets.</summary>
        public string PluginColumns = "";

        /// <summary>
        /// Column order has already been brought back to the catalog's. A column switched on
        /// used to go to the end, and the table stopped matching the menu. A one-off repair:
        /// after it the order is anyone's again — the headers are still dragged with the mouse.
        /// </summary>
        public bool ColumnsSorted;

        /// <summary>Whether to keep pinned sets at the top of the list — the star in the
        /// header.</summary>
        public bool PinnedFirst;

        /// <summary>Whether the summary above the project list is expanded.</summary>
        public bool OverviewOpen = true;

        /// <summary>
        /// Turn the translucent window background off for good — the windows become flat and
        /// dark.
        ///
        /// Needed because of Windows 10: acrylic there goes through the undocumented
        /// SetWindowCompositionAttribute, and DWM recomputes the blur on every window move.
        /// While dragging, the message queue builds up, the window trails behind the cursor and
        /// keeps moving for a couple of seconds after the mouse has stopped. Windows 11 runs a
        /// different branch (the system backdrop) and does not have this — which is why we do
        /// not switch it off ourselves but hand over a toggle.
        /// </summary>
        public bool DisableGlass;


        /// <summary>
        /// Whether smooth vertical scrolling is on (settling by timer). With false, scrolling
        /// in every list and panel is instant.
        /// </summary>
        public bool SmoothScroll = true;

        /// <summary>
        /// Whether to collapse the sets of one folder into a single row. On by default: a
        /// project usually holds a dozen .als files (v1, v2, final, final2), and without this
        /// the catalog is a list of versions rather than a list of projects.
        /// </summary>
        public bool GroupByFolder = true;

        // ------------------------------------------------------------------ plugins

        /// <summary>
        /// Where to take the list of installed plugins from: false — from Live's own database
        /// (the default, and almost always right: Live has already walked every folder from its
        /// settings, parsed the binaries, and expanded shells like Waves into hundreds of
        /// separate plugins); true — walk the folders ourselves by the settings below. The
        /// second is needed when Live's database is empty or lying.
        /// </summary>
        public bool PluginsFromFolders;

        /// <summary>
        /// Which Live installation to ask: empty means all of them at once, newest wins.
        /// Otherwise a folder name ("Live 12.4.3"). Choosing by hand is needed because of
        /// betas: they have their own settings folder, and the freshest scanner log there is
        /// sometimes about the single plugin currently being developed.
        /// </summary>
        public string PluginSource = "";

        /// <summary>Folders for the manual walk — the same three switches Live has.</summary>
        public bool Vst2CustomOn;
        public string Vst2CustomPath = "";
        public bool Vst3SystemOn = true;
        public bool Vst3CustomOn;
        public string Vst3CustomPath = "";

        // ------------------------------------------------------- collecting a project

        /// <summary>
        /// The Collect All dialog's boxes — the same four "Collect All and Save" has in Live.
        /// Packs are off by default: they weigh an order of magnitude more than everything
        /// else, and anyone who bought them has them already.
        /// </summary>
        public bool CollectElsewhere = true;
        public bool CollectOtherProjects = true;
        public bool CollectUserLibrary = true;
        public bool CollectFactoryPacks;

        /// <summary>Put the collected material into a .zip instead of a folder.</summary>
        public bool CollectToZip;

        /// <summary>
        /// Where the main window stood and how big it was, as "x,y,w,h" — empty until it has
        /// been closed once. Physical pixels of the screen it was left on, which is what the
        /// window is measured in; restoring is refused if the rectangle no longer meets any
        /// screen (see MainForm.RestoreGeometry).
        /// </summary>
        public string WindowBounds = "";

        /// <summary>Whether it was left maximized. The bounds above are then the size it
        /// unfolds back to, not the size of the screen.</summary>
        public bool WindowMaximized;

        public static string Dir
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "Alive");
            }
        }

        static string FilePath { get { return Path.Combine(Dir, "settings.cfg"); } }

        public bool IsFirstRun { get { return Roots.Count == 0; } }

        public static Settings Load()
        {
            Settings s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string key = line.Substring(0, eq).Trim();
                    string val = line.Substring(eq + 1).Trim();

                    if (key == "root" && val.Length > 0 && !Has(s.Roots, val)) s.Roots.Add(val);
                    else if (key == "root_off" && val.Length > 0 && !Has(s.DisabledRoots, val))
                        s.DisabledRoots.Add(val);
                    else if (key == "setcolumns") s.SetColumns = val;
                    else if (key == "plugincolumns") s.PluginColumns = val;
                    else if (key == "columnssorted") s.ColumnsSorted = val == "1";
                    else if (key == "pinnedfirst") s.PinnedFirst = val == "1";
                    else if (key == "overviewopen") s.OverviewOpen = val == "1";
                    else if (key == "noglass") s.DisableGlass = val == "1";
                    else if (key == "smoothscroll") s.SmoothScroll = val == "1";
                    else if (key == "nosmoothscroll") s.SmoothScroll = val == "0";
                    else if (key == "groupbyfolder") s.GroupByFolder = val == "1";
                    else if (key == "pluginfolders") s.PluginsFromFolders = val == "1";
                    else if (key == "pluginsource") s.PluginSource = val;
                    else if (key == "vst2custom") s.Vst2CustomOn = val == "1";
                    else if (key == "vst2path") s.Vst2CustomPath = val;
                    else if (key == "vst3system") s.Vst3SystemOn = val == "1";
                    else if (key == "vst3custom") s.Vst3CustomOn = val == "1";
                    else if (key == "vst3path") s.Vst3CustomPath = val;
                    else if (key == "collectelsewhere") s.CollectElsewhere = val == "1";
                    else if (key == "collectotherprojects") s.CollectOtherProjects = val == "1";
                    else if (key == "collectuserlibrary") s.CollectUserLibrary = val == "1";
                    else if (key == "collectfactorypacks") s.CollectFactoryPacks = val == "1";
                    else if (key == "collecttozip") s.CollectToZip = val == "1";
                    else if (key == "window") s.WindowBounds = val;
                    else if (key == "windowmax") s.WindowMaximized = val == "1";
                }
            }
            catch { }
            Theme.SmoothScroll = s.SmoothScroll;
            return s;
        }

        // Paths on Windows are case-insensitive while List<string>.Contains is not, so without
        // this helper "C:\Ableton" and "c:\ableton" would count as different roots.
        static bool Has(List<string> list, string value)
        {
            foreach (string s in list)
                if (string.Equals(s, value, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static event Action<object> RootsChanged;

        public static void NotifyRootsChanged(object source)
        {
            Action<object> h = RootsChanged;
            if (h != null)
            {
                try { h(source); } catch { }
            }
        }

        public void ReloadRoots()
        {
            Settings s = Load();
            Roots.Clear();
            Roots.AddRange(s.Roots);
            DisabledRoots.Clear();
            DisabledRoots.AddRange(s.DisabledRoots);
        }

        public void Save()
        {
            try
            {
                if (!Directory.Exists(Dir)) Directory.CreateDirectory(Dir);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Alive - folders to scan for projects");
                foreach (string r in Roots) sb.Append("root=").AppendLine(r);
                foreach (string r in DisabledRoots) sb.Append("root_off=").AppendLine(r);
                sb.Append("pinnedfirst=").AppendLine(PinnedFirst ? "1" : "0");
                sb.Append("overviewopen=").AppendLine(OverviewOpen ? "1" : "0");
                sb.Append("noglass=").AppendLine(DisableGlass ? "1" : "0");
                sb.Append("smoothscroll=").AppendLine(SmoothScroll ? "1" : "0");
                sb.Append("groupbyfolder=").AppendLine(GroupByFolder ? "1" : "0");
                sb.Append("pluginfolders=").AppendLine(PluginsFromFolders ? "1" : "0");
                sb.Append("pluginsource=").AppendLine(PluginSource);
                sb.Append("vst2custom=").AppendLine(Vst2CustomOn ? "1" : "0");
                sb.Append("vst2path=").AppendLine(Vst2CustomPath);
                sb.Append("vst3system=").AppendLine(Vst3SystemOn ? "1" : "0");
                sb.Append("vst3custom=").AppendLine(Vst3CustomOn ? "1" : "0");
                sb.Append("vst3path=").AppendLine(Vst3CustomPath);
                sb.Append("collectelsewhere=").AppendLine(CollectElsewhere ? "1" : "0");
                sb.Append("collectotherprojects=").AppendLine(CollectOtherProjects ? "1" : "0");
                sb.Append("collectuserlibrary=").AppendLine(CollectUserLibrary ? "1" : "0");
                sb.Append("collectfactorypacks=").AppendLine(CollectFactoryPacks ? "1" : "0");
                sb.Append("collecttozip=").AppendLine(CollectToZip ? "1" : "0");
                if (SetColumns.Length > 0) sb.Append("setcolumns=").AppendLine(SetColumns);
                if (PluginColumns.Length > 0) sb.Append("plugincolumns=").AppendLine(PluginColumns);
                sb.Append("columnssorted=").AppendLine(ColumnsSorted ? "1" : "0");
                if (WindowBounds.Length > 0) sb.Append("window=").AppendLine(WindowBounds);
                sb.Append("windowmax=").AppendLine(WindowMaximized ? "1" : "0");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch { }
        }
    }
}
