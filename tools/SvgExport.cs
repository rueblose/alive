using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>
    /// Выгрузка окон программы — подложка под разметку подсказок (.png/.svg) и вектор
    /// для презентаций (.emf).
    ///
    /// Рисует всё настоящий код приложения: окна создаются как в жизни, показываются и
    /// снимаются as is. Стекло выключается ДО первого обращения к Theme — иначе сквозь
    /// акрил в снимок попадут обои рабочего стола, а для подложки нужен ровный фон.
    ///
    /// В каждом svg два слоя: картинка окна и векторные рамки контролов с их именами из
    /// кода — к ним и цеплять выноски. .emf снимается тем же приёмом, что и системная
    /// печать окна: WM_PRINT прямо в HDC метафайла — рамки, текст и иконки остаются
    /// настоящими контурами, а превью рендеров и прочие растровые куски входят как есть.
    /// </summary>
    static class SvgExport
    {
        static string _out;

        [STAThread]
        static void Main(string[] args)
        {
            _out = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(_out);

            Glass.Enabled = false;                 // строго до первого Theme.*

            CultureInfo en = new CultureInfo("en-US");
            CultureInfo.DefaultThreadCurrentUICulture = en;
            Thread.CurrentThread.CurrentUICulture = en;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            MainForm f = new MainForm();

            // Корень подсовываем в уже загруженные настройки, а не в файл на диске:
            // трогать настоящий settings.cfg ради экспорта незачем.
            Settings s = (Settings)Field(f, "_settings");
            if (s.Roots.Count == 0) s.Roots.Add(@"D:\Music");

            f.Show();
            Pump(800);
            WaitScan(f);
            Pump(600);

            Shot(f, "01-home", "Home (overview + tiles)");

            SetIndex(f, "_mode", 1); Pump(700);
            Shot(f, "02-sets-list", "Sets - list");

            // Выделим первый сет, чтобы панель справа была не пустой.
            SelectFirstRow(f); Pump(700);
            Shot(f, "03-sets-list-selected", "Sets - list with details panel");

            SetIndex(f, "_mode", 2); Pump(900);
            Shot(f, "04-plugins", "Plugins - default view");

            SelectFirstRow(f); Pump(700);
            Shot(f, "05-plugins-selected", "Plugins - with details panel");

            Dialogs(f);

            f.Close();
            Console.WriteLine("done -> " + _out);
        }

        static void Dialogs(MainForm f)
        {
            ProjectIndex index = (ProjectIndex)Field(f, "_index");
            Settings settings = (Settings)Field(f, "_settings");
            SetEntry sample = FirstOk(index.Sets);

            Try("06-filters", "Filters dialog", 1500, delegate
            {
                List<string> versions = new List<string>();
                foreach (SetEntry e in index.Sets)
                    if (e.ShortVersion.Length > 0 && !versions.Contains(e.ShortVersion))
                        versions.Add(e.ShortVersion);
                return new FiltersDialog(new SetFilter(), index.Sets, versions);
            });

            Try("07-plugin-filters", "Plugin filters dialog", 1500, delegate
            {
                return new PluginFiltersDialog(new PluginFilter(), index.PluginUsage());
            });

            Try("08-folders", "Folders dialog", 2500, delegate
            {
                // Подольше: число сетов в колонке досчитывается в фоне.
                return new RootsDialog(new string[] { @"D:\Music" }, new string[0]);
            });

            Try("09-preview", "Arrangement preview", 6000, delegate
            {
                // Загрузчик берём тот же, что у главного окна: свой, с null вместо
                // контрола, разобранную аранжировку никому не отдаст — окно так и
                // останется на «Reading the set…».
                ArrangementLoader loader = Field(f, "_arrangements") as ArrangementLoader;
                if (loader == null) return null;

                SetEntry best = null;
                foreach (SetEntry e in index.Sets)
                    if (e.Error.Length == 0 && e.Tracks > 8) { best = e; break; }
                if (best == null) return null;
                return new PreviewDialog(best, loader);
            });

            Try("10-settings", "Settings dialog", 800, delegate
            {
                return new SettingsDialog(settings);
            });

            Try("11-notes-tags", "Tags & Notes dialog", 800, delegate
            {
                if (sample == null) return null;
                return new NotesDialog(sample);
            });

            Try("12-rescue", "Rescue dialog", 1200, delegate
            {
                if (sample == null) return null;
                return new RescueDialog(sample, index.Inventory);
            });

            Try("13-collect-all", "Collect All dialog", 1200, delegate
            {
                if (sample == null) return null;
                return new CollectDialog(sample, index.Env, settings);
            });

            Try("14-forks", "Forks - version history", 3000, delegate
            {
                if (sample == null) return null;
                return new Reel.ReelWindow(sample.Path);
            });

            Try("15-stat", "Stat - library point cloud", 4000, delegate
            {
                return new AbletonManager.Nebula.NebulaForm();
            });
        }

        /// <summary>Первый сет без ошибки чтения — если такого нет, вообще первый.</summary>
        static SetEntry FirstOk(IEnumerable<SetEntry> sets)
        {
            SetEntry any = null;
            foreach (SetEntry e in sets)
            {
                if (any == null) any = e;
                if (e.Error.Length == 0) return e;
            }
            return any;
        }

        delegate Form MakeDialog();

        static void Try(string name, string title, int settle, MakeDialog make)
        {
            Form d = null;
            try
            {
                d = make();
                if (d == null) { Console.WriteLine("skip " + name); return; }
                d.StartPosition = FormStartPosition.CenterScreen;
                d.Show();
                Pump(settle);                   // дать дорисоваться и догрузить содержимое
                Shot(d, name, title);
            }
            catch (Exception ex) { Console.WriteLine("skip " + name + ": " + ex.Message); }
            finally { if (d != null) { try { d.Close(); } catch { } } }
            Pump(200);
        }

        // ------------------------------------------------------------------ снимок

        static void Shot(Form f, string name, string title)
        {
            // Строки списка появляются с анимацией — снятый слишком рано кадр ловит
            // нижние полупрозрачными.
            f.Activate();
            Pump(900);

            Rectangle r = f.Bounds;
            Bitmap bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                bool ok = PrintWindow(f.Handle, hdc, PW_RENDERFULLCONTENT);
                g.ReleaseHdc(hdc);
                if (!ok || Blank(bmp))
                {
                    // Запасной путь: снять прямо с экрана. Окно к этому моменту сверху.
                    bool was = f.TopMost;
                    f.TopMost = true; Pump(250);
                    using (Graphics g2 = Graphics.FromImage(bmp))
                        g2.CopyFromScreen(r.Left, r.Top, 0, 0, r.Size, CopyPixelOperation.SourceCopy);
                    f.TopMost = was;
                }
            }

            string png = Path.Combine(_out, name + ".png");
            bmp.Save(png, ImageFormat.Png);
            File.WriteAllText(Path.Combine(_out, name + ".svg"),
                              Svg(f, bmp, png, title), new UTF8Encoding(false));
            bmp.Dispose();

            string emfError = CaptureEmf(f, name);
            Console.WriteLine(name + "  " + r.Width + "x" + r.Height
                             + (emfError == null ? "  +emf" : "  emf failed: " + emfError));
        }

        // ------------------------------------------------------------------ вектор (emf)

        /// <summary>
        /// Тот же трюк, которым Windows печатает произвольное окно: WM_PRINT с HDC
        /// метафайла вместо экрана. Контролы рисуют себя как обычно (OnPaint зовётся с
        /// этим HDC через встроенный в Control обработчик WM_PRINTCLIENT), поэтому линии,
        /// текст и заливки попадают в .emf настоящими векторными записями. Растром войдёт
        /// только то, что и само по себе растр — превью рендера, миниатюры аранжировки.
        ///
        /// Акриловый фон (Glass.cs) сюда не попадёт в принципе: это DWM-композитинг поверх
        /// готового кадра, а не рисование в HDC окна. Экспорт всегда идёт с Glass.Enabled
        /// = false (см. Main), так что фон и так ровный — WM_PRINT добросовестно рисует
        /// именно его.
        /// </summary>
        static string CaptureEmf(Form f, string name)
        {
            Rectangle r = f.Bounds;
            string path = Path.Combine(_out, name + ".emf");
            try
            {
                using (Graphics refG = Graphics.FromHwnd(IntPtr.Zero))
                {
                    IntPtr refHdc = refG.GetHdc();
                    try
                    {
                        using (Metafile mf = new Metafile(path, refHdc,
                                   new RectangleF(0, 0, r.Width, r.Height), MetafileFrameUnit.Pixel,
                                   EmfType.EmfPlusDual, name))
                        using (Graphics mg = Graphics.FromImage(mf))
                        {
                            IntPtr mfHdc = mg.GetHdc();
                            try { SendMessage(f.Handle, WM_PRINT, mfHdc, (IntPtr)PRF_ALL); }
                            finally { mg.ReleaseHdc(mfHdc); }
                        }
                    }
                    finally { refG.ReleaseHdc(refHdc); }
                }
                return null;
            }
            catch (Exception ex) { return ex.Message; }
        }

        const int WM_PRINT = 0x317;
        // PRF_CHECKVISIBLE | PRF_NONCLIENT | PRF_CLIENT | PRF_ERASEBKGND | PRF_CHILDREN
        const int PRF_ALL = 0x1 | 0x2 | 0x4 | 0x8 | 0x10;

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Снимок вышел одноцветным — значит окно так и не отрисовалось в него.</summary>
        static bool Blank(Bitmap b)
        {
            Color first = b.GetPixel(0, 0);
            for (int y = 0; y < b.Height; y += 17)
                for (int x = 0; x < b.Width; x += 17)
                    if (b.GetPixel(x, y) != first) return false;
            return true;
        }

        // -------------------------------------------------------------------- svg

        static string Svg(Form f, Bitmap bmp, string pngPath, string title)
        {
            int w = bmp.Width, h = bmp.Height;
            string b64 = Convert.ToBase64String(File.ReadAllBytes(pngPath));

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\"");
            sb.AppendLine("     width=\"" + w + "\" height=\"" + h + "\" viewBox=\"0 0 " + w + " " + h + "\">");
            sb.AppendLine("  <title>" + Esc(title) + "</title>");
            sb.AppendLine("  <g id=\"window\">");
            sb.AppendLine("    <image x=\"0\" y=\"0\" width=\"" + w + "\" height=\"" + h + "\"");
            sb.AppendLine("           xlink:href=\"data:image/png;base64," + b64 + "\"/>");
            sb.AppendLine("  </g>");

            // Слой якорей: где что лежит, по настоящим координатам контролов. Выключается
            // одним кликом по слою в редакторе, если мешает.
            sb.AppendLine("  <g id=\"anchors\" fill=\"none\" stroke=\"#4DA3FF\" stroke-width=\"1\"");
            sb.AppendLine("     stroke-dasharray=\"5 4\" opacity=\"0.85\">");
            Dictionary<Control, string> names = FieldNames(f);
            foreach (Control c in Walk(f))
            {
                if (!c.Visible || c.Width < 8 || c.Height < 8) continue;
                Point p = f.PointToClient(c.Parent.PointToScreen(c.Location));
                string n;
                if (!names.TryGetValue(c, out n)) n = c.GetType().Name;
                sb.AppendLine("    <g id=\"" + Esc(n) + "\">");
                sb.AppendLine("      <title>" + Esc(n + "  (" + c.GetType().Name + ")") + "</title>");
                sb.AppendLine("      <rect x=\"" + p.X + "\" y=\"" + p.Y + "\" width=\"" + c.Width
                            + "\" height=\"" + c.Height + "\" rx=\"3\"/>");
                sb.AppendLine("    </g>");
            }
            sb.AppendLine("  </g>");
            sb.AppendLine("</svg>");
            return sb.ToString();
        }

        static IEnumerable<Control> Walk(Control root)
        {
            foreach (Control c in root.Controls)
            {
                yield return c;
                foreach (Control inner in Walk(c)) yield return inner;
            }
        }

        /// <summary>Имена контролов берём из имён полей формы — они говорящие.</summary>
        static Dictionary<Control, string> FieldNames(Form f)
        {
            Dictionary<Control, string> map = new Dictionary<Control, string>();
            for (Type t = f.GetType(); t != null && t != typeof(Form); t = t.BaseType)
                foreach (FieldInfo fi in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic
                                                   | BindingFlags.Public | BindingFlags.DeclaredOnly))
                {
                    Control c = null;
                    try { c = fi.GetValue(f) as Control; }
                    catch { }
                    if (c != null && !map.ContainsKey(c)) map[c] = fi.Name.TrimStart('_');
                }
            return map;
        }

        static string Esc(string s)
        {
            return s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        // ----------------------------------------------------------------- утилиты

        static object Field(object o, string name)
        {
            FieldInfo fi = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            return fi == null ? null : fi.GetValue(o);
        }

        static void SetIndex(Form f, string field, int index)
        {
            object c = Field(f, field);
            if (c == null) return;
            c.GetType().GetProperty("SelectedIndex").SetValue(c, index, null);
        }

        static void SelectFirstRow(Form f)
        {
            RowListView list = Field(f, "_list") as RowListView;
            if (list == null || list.Rows.Count == 0) return;
            bool first = true;
            list.SelectRow(delegate (RowData r) { bool take = first; first = false; return take; });
        }

        static void WaitScan(Form f)
        {
            int t = Environment.TickCount;
            while (Environment.TickCount - t < 120000)
            {
                object v = Field(f, "_scanning");
                if (v is bool && !(bool)v) return;
                Pump(200);
            }
        }

        static void Pump(int ms)
        {
            int t = Environment.TickCount;
            do { Application.DoEvents(); Thread.Sleep(15); }
            while (Environment.TickCount - t < ms);
        }

        const uint PW_RENDERFULLCONTENT = 2;

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    }
}
