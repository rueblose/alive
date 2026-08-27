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
    /// Выгрузка окон программы в SVG — подложка под разметку подсказок.
    ///
    /// Рисует всё настоящий код приложения: окна создаются как в жизни, показываются и
    /// снимаются as is. Стекло выключается ДО первого обращения к Theme — иначе сквозь
    /// акрил в снимок попадут обои рабочего стола, а для подложки нужен ровный фон.
    ///
    /// В каждом svg два слоя: картинка окна и векторные рамки контролов с их именами из
    /// кода — к ним и цеплять выноски.
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

            Shot(f, "01-sets-tiles", "Sets - tiles (default view)");

            SetIndex(f, "_viewToggle", 1); Pump(700);
            Shot(f, "02-sets-list", "Sets - list");

            // Выделим первый сет, чтобы панель справа была не пустой.
            SelectFirstRow(f); Pump(700);
            Shot(f, "03-sets-list-selected", "Sets - list with details panel");

            SetIndex(f, "_mode", 1); Pump(900);
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
                return new RootsDialog(new string[] { @"D:\Music" }, false, new string[0]);
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
            Console.WriteLine(name + "  " + r.Width + "x" + r.Height);
        }

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
