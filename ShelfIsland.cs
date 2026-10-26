// Shelf Island â€” dynamic-island style file shelf for Windows.
// Drop many files onto the pill at the top of the screen, then drag them all out anywhere.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Program
{
    [STAThread]
    static void Main()
    {
        bool created;
        using (var m = new System.Threading.Mutex(true, "ShelfIsland_Single", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.Run(new Island());
        }
    }
}

class Island : Form
{
    const int PillW = 180, PillH = 34, OpenW = 380, OpenH = 260, TopGap = 8;
    readonly List<string> files = new List<string>();
    readonly string store = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShelfIsland", "shelf.txt");
    readonly Timer anim = new Timer { Interval = 12 };
    readonly Timer hoverCheck = new Timer { Interval = 150 };
    readonly NotifyIcon tray = new NotifyIcon();
    float w = PillW, h = PillH, tw = PillW, th = PillH;
    bool expanded, dragOver;
    int hot = -1, scroll;
    Point downPt; bool mouseDown;
    readonly Font fTitle = new Font("Segoe UI Semibold", 9.5f);
    readonly Font fItem = new Font("Segoe UI", 9f);
    readonly Font fSmall = new Font("Segoe UI", 8f);
    const int RowH = 26, ListTop = 44;

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr h);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr h);
    const int WM_CLIPBOARDUPDATE = 0x031D;
    static readonly string[] ImgExt = { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif", ".tiff", ".heic" };
    string lastClipHash;
    readonly Timer flash = new Timer { Interval = 900 };
    bool justAdded;

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); AddClipboardFormatListener(Handle); }
    protected override void OnHandleDestroyed(EventArgs e) { RemoveClipboardFormatListener(Handle); base.OnHandleDestroyed(e); }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE) BeginInvoke(new Action(GrabClipboard));
        base.WndProc(ref m);
    }

    void GrabClipboard()
    {
        try
        {
            var data = Clipboard.GetDataObject();
            if (data == null) return;
            var fmts = data.GetFormats();
            // Excel/Sheets cell ranges also carry a picture of the cells; only take real images.
            bool isCells = fmts.Contains("Csv") || fmts.Contains("XML Spreadsheet") || fmts.Contains("Link");

            if (data.GetDataPresent(DataFormats.FileDrop))
            {
                // Ctrl+C on image files in Explorer
                int before = files.Count;
                foreach (var f in (string[])data.GetData(DataFormats.FileDrop))
                    if (ImgExt.Contains(Path.GetExtension(f).ToLowerInvariant()) && !files.Contains(f, StringComparer.OrdinalIgnoreCase)) files.Add(f);
                if (files.Count > before) Added();
                return;
            }

            if (!isCells)
            {
                // Excel/Office/Chrome put a lossless "PNG" stream; prefer it over the bitmap.
                var png = data.GetData("PNG") as MemoryStream;
                if (png != null) { AddImageBytes(png.ToArray()); return; }
                if (data.GetDataPresent(DataFormats.Bitmap))
                {
                    using (var img = data.GetData(DataFormats.Bitmap) as Image)
                        if (img != null) { AddImage(img); return; }
                }
                using (var emf = GetClipboardEmf())
                    if (emf != null) { AddImage(emf); return; }
            }

            // Google Sheets (and cells with images in Excel Online) only give HTML with <img> tags.
            if (data.GetDataPresent(DataFormats.Html))
            {
                var html = data.GetData(DataFormats.Html) as string;
                var srcs = System.Text.RegularExpressions.Regex.Matches(html ?? "", "<img[^>]+src=[\"']([^\"']+)[\"']", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                    .Cast<System.Text.RegularExpressions.Match>().Select(m => System.Net.WebUtility.HtmlDecode(m.Groups[1].Value)).Distinct().ToList();
                foreach (var src in srcs) FetchImage(src);
            }
        }
        catch { } // clipboard can be locked by another app; skip silently
    }

    void FetchImage(string src)
    {
        if (src.StartsWith("data:image", StringComparison.OrdinalIgnoreCase))
        {
            int c = src.IndexOf(','); if (c > 0) AddImageBytes(Convert.FromBase64String(src.Substring(c + 1)));
            return;
        }
        if (src.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
        {
            // Excel desktop "Place in Cell" images: HTML points at temp files in %TEMP%\msohtmlclip*
            try { var local = new Uri(src).LocalPath; if (File.Exists(local)) AddImageBytes(File.ReadAllBytes(local)); } catch { }
            return;
        }
        if (src.StartsWith("//")) src = "https:" + src;
        if (!src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return;
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                System.Net.ServicePointManager.SecurityProtocol = System.Net.SecurityProtocolType.Tls12;
                using (var wc = new System.Net.WebClient())
                {
                    wc.Headers.Add("User-Agent", "Mozilla/5.0");
                    var bytes = wc.DownloadData(src);
                    BeginInvoke(new Action(() => AddImageBytes(bytes)));
                }
            }
            catch { }
        });
    }

    void AddImage(Image img)
    {
        using (var ms = new MemoryStream()) { img.Save(ms, System.Drawing.Imaging.ImageFormat.Png); AddImageBytes(ms.ToArray()); }
    }

    void AddImageBytes(byte[] bytes)
    {
        try
        {
            // Normalize anything (jpg/webp-less/gif) to PNG when GDI+ can read it.
            using (var src = new MemoryStream(bytes))
            using (var img = Image.FromStream(src))
            using (var ms = new MemoryStream()) { img.Save(ms, System.Drawing.Imaging.ImageFormat.Png); bytes = ms.ToArray(); }
        }
        catch { }
        string hash;
        using (var md5 = System.Security.Cryptography.MD5.Create()) hash = BitConverter.ToString(md5.ComputeHash(bytes));
        if (hash == lastClipHash) return; // apps often fire the event twice
        lastClipHash = hash;
        var dir = Path.Combine(Path.GetDirectoryName(store), "clips");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "clip_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".png");
        File.WriteAllBytes(path, bytes);
        files.Add(path);
        Added();
    }

    void Added() { Save(); justAdded = true; flash.Stop(); flash.Start(); Invalidate(); }

    [DllImport("user32.dll")] static extern bool OpenClipboard(IntPtr h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern IntPtr GetClipboardData(uint f);
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint f);

    // Excel pictures/charts are sometimes only offered as an enhanced metafile.
    Image GetClipboardEmf()
    {
        if (!IsClipboardFormatAvailable(14) || !OpenClipboard(Handle)) return null;
        try
        {
            var h = GetClipboardData(14);
            if (h == IntPtr.Zero) return null;
            using (var mf = new System.Drawing.Imaging.Metafile(h, false))
            {
                var bmp = new Bitmap(Math.Max(1, mf.Width), Math.Max(1, mf.Height));
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.White); g.DrawImage(mf, 0, 0, bmp.Width, bmp.Height); }
                return bmp;
            }
        }
        catch { return null; }
        finally { CloseClipboard(); }
    }

    public Island()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        DoubleBuffered = true;
        AllowDrop = true;
        Load_();
        Relayout();

        anim.Tick += (s, e) => Step();
        hoverCheck.Tick += (s, e) =>
        {
            if (expanded && !dragOver && !mouseDown && !Bounds.Contains(Cursor.Position)) SetExpanded(false);
        };
        hoverCheck.Start();
        flash.Tick += (s, e) => { flash.Stop(); justAdded = false; Invalidate(); };

        DragEnter += (s, e) =>
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effect = DragDropEffects.Copy; dragOver = true; SetExpanded(true); }
        };
        DragOver += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
        DragLeave += (s, e) => { dragOver = false; Invalidate(); };
        DragDrop += (s, e) =>
        {
            dragOver = false;
            var dropped = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (dropped != null)
                foreach (var f in dropped) if (!files.Contains(f, StringComparer.OrdinalIgnoreCase)) files.Add(f);
            Save(); SetExpanded(true); Invalidate();
        };

        MouseEnter += (s, e) => SetExpanded(true);
        MouseMove += OnMove;
        MouseDown += (s, e) => { mouseDown = true; downPt = e.Location; };
        MouseUp += OnUp;
        MouseWheel += (s, e) => { scroll = Math.Max(0, Math.Min(MaxScroll(), scroll - Math.Sign(e.Delta))); Invalidate(); };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Kosongkan shelf", null, (s, e) => { files.Clear(); Save(); Invalidate(); });
        menu.Items.Add("Jalankan saat Windows start", null, (s, e) => ToggleStartup((ToolStripMenuItem)s)).Name = "startup";
        menu.Items.Add("Keluar", null, (s, e) => { tray.Visible = false; Application.Exit(); });
        ((ToolStripMenuItem)menu.Items["startup"]).Checked = StartupOn();
        tray.Icon = SystemIcons.Application;
        tray.Text = "Shelf Island";
        tray.ContextMenuStrip = menu;
        tray.Visible = true;
    }

    protected override CreateParams CreateParams
    {
        get { var cp = base.CreateParams; cp.ExStyle |= 0x80 /*TOOLWINDOW*/ | 0x08000000 /*NOACTIVATE*/; return cp; }
    }
    protected override bool ShowWithoutActivation { get { return true; } }

    void SetExpanded(bool on)
    {
        if (expanded == on) return;
        expanded = on;
        tw = on ? OpenW : PillW; th = on ? OpenH : PillH;
        if (!on) { hot = -1; scroll = 0; }
        anim.Start();
    }

    void Step()
    {
        w += (tw - w) * 0.28f; h += (th - h) * 0.28f;
        if (Math.Abs(tw - w) < 0.6 && Math.Abs(th - h) < 0.6) { w = tw; h = th; anim.Stop(); }
        Relayout();
    }

    void Relayout()
    {
        var scr = Screen.PrimaryScreen.Bounds;
        int W = (int)w, H = (int)h;
        Bounds = new Rectangle(scr.Left + (scr.Width - W) / 2, scr.Top + TopGap, W, H);
        using (var p = RoundPath(new Rectangle(0, 0, W, H), Math.Min(H / 2, expanded ? 22 : H / 2)))
            Region = new Region(p);
        Invalidate();
    }

    static GraphicsPath RoundPath(Rectangle r, int rad)
    {
        var p = new GraphicsPath(); int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }

    int Visible_() { return Math.Max(1, (OpenH - ListTop - 10) / RowH); }
    int MaxScroll() { return Math.Max(0, files.Count - Visible_()); }
    Rectangle ClearBtn() { return new Rectangle(Width - 76, 10, 64, 24); }
    Rectangle AllBtn() { return new Rectangle(Width - 150, 10, 68, 24); }

    int RowAt(Point p)
    {
        if (p.Y < ListTop) return -1;
        int i = (p.Y - ListTop) / RowH + scroll;
        return i < files.Count ? i : -1;
    }

    void OnMove(object s, MouseEventArgs e)
    {
        int r = expanded ? RowAt(e.Location) : -1;
        if (r != hot) { hot = r; Invalidate(); }
        if (mouseDown && files.Count > 0 && (Math.Abs(e.X - downPt.X) > 6 || Math.Abs(e.Y - downPt.Y) > 6))
        {
            mouseDown = false;
            // Drag a single row, or everything if the drag started from the "Semua" button / header.
            int row = RowAt(downPt);
            var payload = row >= 0 && !AllBtn().Contains(downPt) ? new[] { files[row] } : files.ToArray();
            payload = payload.Where(f => File.Exists(f) || Directory.Exists(f)).ToArray();
            if (payload.Length == 0) return;
            var sc = new StringCollection(); sc.AddRange(payload);
            var data = new DataObject(); data.SetFileDropList(sc);
            var res = DoDragDrop(data, DragDropEffects.Copy | DragDropEffects.Move);
            if (res == DragDropEffects.Move) { foreach (var f in payload) files.Remove(f); Save(); }
            Invalidate();
        }
    }

    void OnUp(object s, MouseEventArgs e)
    {
        if (mouseDown && e.Button == MouseButtons.Left)
        {
            if (ClearBtn().Contains(e.Location)) { files.Clear(); Save(); }
            else
            {
                int r = RowAt(e.Location);
                // Click the Ã— on the right of a row to remove it.
                if (r >= 0 && e.X > Width - 34) { files.RemoveAt(r); scroll = Math.Min(scroll, MaxScroll()); Save(); }
            }
            Invalidate();
        }
        mouseDown = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Black);
        var white = Brushes.White;
        var dim = new SolidBrush(Color.FromArgb(150, 255, 255, 255));
        var accent = Color.FromArgb(80, 160, 255);

        if (Height < 60)
        {
            // Collapsed pill: dot + count
            using (var b = new SolidBrush(files.Count > 0 ? accent : Color.FromArgb(90, 90, 90)))
                g.FillEllipse(b, 14, Height / 2 - 4, 8, 8);
            string t = justAdded ? "+ Tersimpan · " + files.Count : files.Count == 0 ? "Shelf" : files.Count + " file";
            g.DrawString(t, fTitle, white, 30, Height / 2 - 9);
            return;
        }

        g.DrawString("Shelf", fTitle, white, 16, 12);
        g.DrawString(files.Count + " item", fSmall, dim, 60, 14);
        if (files.Count > 0)
        {
            DrawBtn(g, AllBtn(), "â‡± Semua", accent);
            DrawBtn(g, ClearBtn(), "Kosongkan", Color.FromArgb(70, 70, 70));
        }

        if (files.Count == 0 || dragOver)
        {
            var zone = new Rectangle(14, ListTop, Width - 28, Height - ListTop - 12);
            using (var pen = new Pen(dragOver ? accent : Color.FromArgb(80, 80, 80), 1.5f) { DashStyle = DashStyle.Dash })
            using (var path = RoundPath(zone, 12)) g.DrawPath(pen, path);
            var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(dragOver ? "Lepas untuk menyimpan" : "Tarik file ke sini\n(bisa banyak sekaligus)", fItem, dragOver ? white : dim, zone, sf);
            if (files.Count == 0 || dragOver) return;
        }

        int vis = Visible_();
        for (int i = scroll; i < Math.Min(files.Count, scroll + vis); i++)
        {
            int y = ListTop + (i - scroll) * RowH;
            var row = new Rectangle(8, y, Width - 16, RowH - 2);
            if (i == hot) using (var b = new SolidBrush(Color.FromArgb(38, 255, 255, 255))) using (var p = RoundPath(row, 8)) g.FillPath(b, p);
            var f = files[i];
            bool exists = File.Exists(f) || Directory.Exists(f);
            try
            {
                if (File.Exists(f)) using (var ic = Icon.ExtractAssociatedIcon(f)) g.DrawIcon(ic, new Rectangle(14, y + 4, 16, 16));
                else using (var b = new SolidBrush(Color.FromArgb(200, 170, 60))) g.FillRectangle(b, 15, y + 7, 14, 11);
            }
            catch { }
            string name = Path.GetFileName(f.TrimEnd('\\')); if (name == "") name = f;
            var txt = new RectangleF(36, y + 4, Width - 80, RowH);
            g.DrawString(name, fItem, exists ? white : dim, txt, new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap });
            if (i == hot) g.DrawString("Ã—", fTitle, dim, Width - 28, y + 3);
        }
        if (files.Count > vis)
            g.DrawString((scroll + 1) + "â€“" + Math.Min(files.Count, scroll + vis) + " / " + files.Count, fSmall, dim, Width - 80, Height - 18);
    }

    void DrawBtn(Graphics g, Rectangle r, string t, Color c)
    {
        using (var b = new SolidBrush(c)) using (var p = RoundPath(r, 12)) g.FillPath(b, p);
        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString(t, fSmall, Brushes.White, r, sf);
    }

    void Load_()
    {
        try { if (File.Exists(store)) files.AddRange(File.ReadAllLines(store).Where(l => l.Trim() != "")); } catch { }
    }
    void Save()
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(store)); File.WriteAllLines(store, files); } catch { }
    }

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    bool StartupOn()
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue("ShelfIsland") != null;
    }
    void ToggleStartup(ToolStripMenuItem item)
    {
        using (var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey))
        {
            if (StartupOn()) k.DeleteValue("ShelfIsland", false);
            else k.SetValue("ShelfIsland", "\"" + Application.ExecutablePath + "\"");
        }
        item.Checked = StartupOn();
    }
}


