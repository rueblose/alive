using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AbletonManager
{
    /// <summary>Choosing the folders to look for projects in. Shown on first run and from the
    /// button.</summary>
    public sealed class RootsDialog : GlassDialog
    {
        readonly RowListView _list = new RowListView();
        readonly GlassButton _remove = new GlassButton();
        readonly GlassButton _ok = new GlassButton();
        readonly GlassButton _cancel = new GlassButton();
        readonly List<string> _roots = new List<string>();
        // The folder stays in the list but is temporarily left out of scanning — not the same
        // as "remove": its settings (filters and so on) need not be rebuilt from scratch.
        readonly HashSet<string> _disabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public List<string> Result { get { return _roots; } }
        public List<string> DisabledRoots { get { return new List<string>(_disabled); } }

        public RootsDialog(IEnumerable<string> current, IEnumerable<string> disabledRoots)
        {
            Caption = "Where to look for projects";
            ClientSize = new Size(820, 470);

            foreach (string r in current) if (!_roots.Contains(r)) _roots.Add(r);
            if (disabledRoots != null) foreach (string r in disabledRoots) _disabled.Add(r);

            _list.SetColumns(new Column("Folder", 0),
                             new Column("sets", 150) { Right = true });
            _list.ShowCheckboxes = true;
            _list.RowCheckedChanged += OnRowCheckedChanged;
            Controls.Add(_list);

            _remove.Text = "Remove";
            _remove.FitToText(16);
            _remove.Click += delegate { RemoveSelected(); };
            Controls.Add(_remove);

            _ok.Text = "Scan";
            _ok.Primary = true;
            _ok.FitToText(20);
            _ok.Click += delegate { DialogResult = DialogResult.OK; Close(); };
            Controls.Add(_ok);

            _cancel.Text = "Cancel";
            _cancel.FitToText(16);
            _cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_cancel);

            AllowDrop = true;
            _list.AllowDrop = true;
            _list.DragEnter += delegate (object s, DragEventArgs e) { OnDragEnter(e); };
            _list.DragLeave += delegate (object s, EventArgs e) { OnDragLeave(e); };
            _list.DragDrop += delegate (object s, DragEventArgs e) { OnDragDrop(e); };

            Refill();
        }

        // Counting the sets in a folder means walking its whole tree. On a real library that
        // costs hundreds of milliseconds per root (measured: 469 ms for three folders with a
        // warm filesystem cache and an SSD; on a cold cache, an HDD or a network drive —
        // seconds), and it used to run right in the dialog's constructor, synchronously, and
        // repeated in full on every checkbox click. That is where "it thinks for several
        // seconds when the folder list opens" came from.
        //
        // Now we count in the background and remember the answer: the dialog opens instantly
        // and the numbers arrive after it. The key includes the Backup flag — the answer
        // differs with it.
        readonly Dictionary<string, int> _counts =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _counting =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        volatile bool _closed;

        // the Backup flag is gone — the switch was removed, sets in Backup are always excluded
        string CountKey(string root) { return (false ? "1|" : "0|") + root; }

        void Refill()
        {
            List<RowData> rows = new List<RowData>();
            List<string> pending = new List<string>();

            foreach (string r in _roots)
            {
                RowData row = new RowData();
                bool missing = !Directory.Exists(r);

                string cell;
                if (missing) cell = "";
                else
                {
                    int n;
                    if (_counts.TryGetValue(CountKey(r), out n))
                        cell = n < 0 ? "no access" : n.ToString();
                    else { cell = "…"; pending.Add(r); }
                }

                // When the folder is missing entirely, the red "not found" mark already carries
                // the whole meaning — we do not repeat it as text in the very same column on
                // top of it.
                row.Cells = new string[] { r, cell };
                row.Tag = r;
                row.Checked = !_disabled.Contains(r);
                if (missing)
                    row.Marks.Add(new CellMark(1, Theme.Red, "not found"));
                rows.Add(row);
            }

            _list.SetRows(rows);
            _ok.Enabled = _roots.Count > 0;

            foreach (string r in pending) StartCount(r);
        }

        void StartCount(string root)
        {
            string key = CountKey(root);
            if (!_counting.Add(key)) return;          // this count is already running

            Thread t = new Thread(delegate ()
            {
                int n = CountSets(root);
                if (_closed) return;
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        _counts[key] = n;
                        _counting.Remove(key);
                        ShowCount(root, key);
                    });
                }
                catch { /* the window closed while we counted - nobody needs the answer now */ }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal;
            t.Start();
        }

        /// <summary>
        /// The finished number is written straight into its own row rather than rebuilding the
        /// whole list: SetRows resets the selection and the scroll position, and they would
        /// jump under the hand every time another answer arrived.
        /// </summary>
        void ShowCount(string root, string key)
        {
            // While we were counting, "Include Backup" may have been toggled — then this is an
            // answer to a question that no longer applies and must not be shown (it stays in
            // _counts and comes in handy if the box is switched back).
            if (key != CountKey(root)) return;

            int n = _counts[key];
            foreach (RowData r in _list.Rows)
            {
                string path = r.Tag as string;
                if (path == null || !string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) continue;
                if (r.Cells.Length > 1)
                    r.Cells[1] = n < 0 ? "no access" : n.ToString();
                break;
            }
            _list.Invalidate();
        }

        void OnRowCheckedChanged(int idx)
        {
            RowData row = idx >= 0 && idx < _list.Rows.Count ? _list.Rows[idx] : null;
            if (row == null) return;
            string path = (string)row.Tag;
            if (row.Checked) _disabled.Remove(path); else _disabled.Add(path);
            // Refill() used to sit here for "recount the sets without the folder that was
            // switched off", but there is nothing to recount: the number shown is each folder's
            // own and does not depend on the neighbours' checkboxes — Refill simply walked
            // every tree again.
        }

        /// <summary>
        /// How many sets are in a folder. It counts with exactly the same walk the scan itself
        /// later uses — otherwise the number in the column disagrees with what ends up in the
        /// catalog. There used to be a separate walk here with a depth cap of 8 folders: under
        /// a root like D:\ the projects lie deeper, and the column showed zero for a folder the
        /// scan then honestly parsed.
        ///
        /// _closed — so a closed dialog stops hammering the disk in the background.
        /// </summary>
        int CountSets(string dir)
        {
            if (!Directory.Exists(dir)) return -1;
            FolderScan.Result r = FolderScan.Find(dir, ".als", false, null,
                                                  delegate { return _closed; });
            return r.RootFailed ? -1 : r.Files;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closed = true;
            base.OnFormClosed(e);
        }

        void AddFolder()
        {
            string path = ModernFolderPicker.PickFolder(Handle, "Pick a folder with Ableton projects");
            if (string.IsNullOrEmpty(path)) return;
            if (!_roots.Contains(path))
            {
                _roots.Add(path);
                _disabled.Remove(path);
                Refill();
            }
        }

        void RemoveSelected()
        {
            RowData sel = _list.Selected;
            if (sel == null) return;
            string path = (string)sel.Tag;
            _roots.Remove(path);
            _disabled.Remove(path);
            Refill();
        }

        bool _isDragOver;
        bool _dropZoneHot;
        Rectangle _rDropZone;

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hot = _rDropZone.Contains(e.Location);
            if (hot != _dropZoneHot)
            {
                _dropZoneHot = hot;
                Cursor = hot ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (_dropZoneHot)
            {
                _dropZoneHot = false;
                Cursor = Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Left && _rDropZone.Contains(e.Location))
            {
                AddFolder();
            }
        }

        protected override void OnDragEnter(DragEventArgs drgevent)
        {
            base.OnDragEnter(drgevent);
            if (drgevent.Data.GetDataPresent(DataFormats.FileDrop))
            {
                drgevent.Effect = DragDropEffects.Copy;
                _isDragOver = true;
                Invalidate();
            }
        }

        protected override void OnDragLeave(EventArgs e)
        {
            base.OnDragLeave(e);
            _isDragOver = false;
            Invalidate();
        }

        protected override void OnDragDrop(DragEventArgs drgevent)
        {
            base.OnDragDrop(drgevent);
            _isDragOver = false;
            Invalidate();

            string[] files = drgevent.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null)
            {
                bool added = false;
                foreach (string path in files)
                {
                    string folder = path;
                    if (File.Exists(path)) folder = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && !_roots.Contains(folder))
                    {
                        _roots.Add(folder);
                        _disabled.Remove(folder);
                        added = true;
                    }
                }
                if (added) Refill();
            }
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_list == null) return;
            int pad = Sc(Theme.Pad);
            int w = Card.Width - pad * 2;

            int dropH = Sc(42);
            int top = Card.Top + Sc(56);
            int bottom = Card.Bottom - pad - _ok.Height - Sc(18) - dropH - Sc(10);
            _list.SetBounds(Card.Left + pad - Sc(Theme.CellPadX), top,
                            w + Sc(Theme.CellPadX) * 2, Math.Max(Sc(80), bottom - top));

            int dropY = _list.Bottom + Sc(10);
            _rDropZone = new Rectangle(Card.Left + pad, dropY, w, dropH);

            int by = Card.Bottom - pad - _ok.Height;
            _remove.Location = new Point(Card.Left + pad, by);
            _ok.Location = new Point(Card.Right - pad - _ok.Width, by);
            _cancel.Location = new Point(_ok.Left - Sc(10) - _cancel.Width, by);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;

            if (_rDropZone.Width > 0 && _rDropZone.Height > 0)
            {
                Theme.Smooth(g);
                bool active = _isDragOver || _dropZoneHot;
                Color border = _isDragOver ? Theme.Light : (active ? Color.FromArgb(140, 255, 255, 255) : Theme.Hairline);
                Color textCol = Theme.TextDim;

                using (GraphicsPath path = Theme.Round(_rDropZone, Sc(10)))
                using (Pen pen = new Pen(border, active ? 1.4f : 1.2f))
                {
                    pen.DashPattern = new float[] { 4f, 3f };
                    g.DrawPath(pen, path);
                }

                string hint = _isDragOver
                    ? "Release to add folders"
                    : "Drag and drop folders here or click to browse…";

                Font font = Theme.FSmall;
                Size sz = TextRenderer.MeasureText(hint, font);
                int iconSize = Sc(16);
                int gap = Sc(8);
                int totalW = iconSize + gap + sz.Width;

                float startX = _rDropZone.X + (_rDropZone.Width - totalW) / 2f;
                float iconY = _rDropZone.Y + (_rDropZone.Height - iconSize) / 2f;

                RectangleF iconRect = new RectangleF(startX, iconY, iconSize, iconSize);
                Icons.Draw(g, Glyph.Folder, iconRect, textCol, 1.4f);

                Rectangle textRect = new Rectangle((int)(startX + iconSize + gap), _rDropZone.Y, sz.Width + Sc(4), _rDropZone.Height);
                Chrome.DrawText(g, hint, font, textRect, textCol, Chrome.Left);
            }
        }
    }

    internal static class ModernFolderPicker
    {
        [ComImport]
        [Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        [ClassInterface(ClassInterfaceType.None)]
        private class FileOpenDialogRC { }

        [ComImport]
        [Guid("42f85136-db7e-439c-85f1-e4075d135fc8")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes(uint cNames, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint foptions);
            void GetOptions(out uint pfoptions);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, uint fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close([MarshalAs(UnmanagedType.Error)] int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter([MarshalAs(UnmanagedType.IUnknown)] object pFilter);
        }

        [ComImport]
        [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog : IFileDialog
        {
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            out IShellItem ppv);

        public static string PickFolder(IntPtr ownerHandle, string title)
        {
            return PickFolder(ownerHandle, title, null);
        }

        public static string PickFolder(IntPtr ownerHandle, string title, string initialFolder)
        {
            try
            {
                IFileOpenDialog dialog = (IFileOpenDialog)new FileOpenDialogRC();
                uint options;
                dialog.GetOptions(out options);
                options |= FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM;
                dialog.SetOptions(options);
                if (!string.IsNullOrEmpty(title))
                    dialog.SetTitle(title);

                if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                {
                    try
                    {
                        Guid riid = new Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe");
                        IShellItem folderItem;
                        if (SHCreateItemFromParsingName(initialFolder, IntPtr.Zero, ref riid, out folderItem) == 0 && folderItem != null)
                        {
                            dialog.SetFolder(folderItem);
                        }
                    }
                    catch { }
                }

                int hr = dialog.Show(ownerHandle);
                if (hr == 0) // S_OK
                {
                    IShellItem result;
                    dialog.GetResult(out result);
                    if (result != null)
                    {
                        string path;
                        result.GetDisplayName(0x80058000, out path); // SIGDN_FILESYSPATH
                        return path;
                    }
                }
                return null;
            }
            catch
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.Description = title;
                    if (!string.IsNullOrEmpty(initialFolder) && Directory.Exists(initialFolder))
                        dlg.SelectedPath = initialFolder;
                    if (dlg.ShowDialog() == DialogResult.OK)
                        return dlg.SelectedPath;
                }
                return null;
            }
        }
    }
}
