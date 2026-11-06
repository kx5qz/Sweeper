using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Program
{
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static void Main()
    {
        bool isFirstInstance;
        using (new Mutex(true, @"Local\Sweeper", out isFirstInstance))
        {
            if (!isFirstInstance)
            {
                MessageBox.Show("Sweeper is already running. Look for it in the system tray.", "Sweeper");
                return;
            }

            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Theme.Init();

            Application.Run(new MainForm());
        }
    }
}

static class Processes
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

    public static uint ForegroundProcess()
    {
        uint process;
        GetWindowThreadProcessId(GetForegroundWindow(), out process);
        return process;
    }

    public static string ProcessName(uint process)
    {
        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, process);
        if (handle == IntPtr.Zero) return "";
        try
        {
            var path = new StringBuilder(1024);
            uint size = (uint)path.Capacity;
            return QueryFullProcessImageName(handle, 0, path, ref size) ? Path.GetFileNameWithoutExtension(path.ToString()) : "";
        }
        finally { CloseHandle(handle); }
    }
}

class MainForm : ThemedForm
{
    readonly TileCheckBox solverButton = new TileCheckBox("solver");
    readonly TileCheckBox autoButton = new TileCheckBox("auto solve");
    readonly TileCheckBox guessButton = new TileCheckBox("auto guess") { Enabled = false };
    readonly TileCheckBox streamButton = new TileCheckBox("streamproof") { Checked = true };
    readonly StrokeLabel statusLabel = new StrokeLabel("solver off", 24) { Color = Theme.Off, Dock = DockStyle.Fill, Margin = new Padding(0) };
    readonly GameTip tips = new GameTip();
    readonly MineSolver solver;
    readonly NotifyIcon tray = new NotifyIcon();
    bool restoring;

    public MainForm()
    {
        Text = "Sweeper";
        MaximizeBox = false;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        int width = Theme.S(360);
        var body = new TableLayoutPanel { AutoSize = true, ColumnCount = 1, Margin = new Padding(0), Padding = new Padding(Theme.S(12)) };
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));

        var header = new TableLayoutPanel { ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, Theme.S(8)), Size = new Size(width, Theme.S(30)) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(statusLabel, 0, 0);
        header.Controls.Add(new CreditLabel(16) { Anchor = AnchorStyles.Right, Margin = new Padding(Theme.S(8), 0, 0, 0) }, 1, 0);
        body.Controls.Add(header);

        var options = new GamePanel { ColumnCount = 2, AutoSize = true, Margin = new Padding(0), Padding = new Padding(Theme.S(10), Theme.S(8), Theme.S(10), Theme.S(8)) };
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (width - Theme.S(20)) / 2));
        options.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, (width - Theme.S(20)) / 2));
        options.Controls.Add(solverButton, 0, 0);
        options.Controls.Add(autoButton, 1, 0);
        options.Controls.Add(guessButton, 0, 1);
        options.Controls.Add(streamButton, 1, 1);
        body.Controls.Add(options);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Theme.S(10), 0, 0) };
        var resetButton = new TileButton("reset", 120, TileButton.Look.Gold) { Margin = new Padding(0, 0, Theme.S(10), 0) };
        var exitButton = new TileButton("exit", 90, TileButton.Look.White) { Margin = new Padding(0) };
        buttons.Controls.Add(resetButton);
        buttons.Controls.Add(exitButton);
        body.Controls.Add(buttons);
        SetContent(body);

        solverButton.Click += (s, e) => ToggleSolver();
        autoButton.Click += (s, e) => ToggleAuto();
        guessButton.Click += (s, e) => ToggleGuess();
        streamButton.Click += (s, e) => ToggleStream();
        resetButton.Click += (s, e) => ResetAll();
        exitButton.Click += (s, e) => Close();
        solver = new MineSolver(ShowSolverStatus);
        SetUpTray();

        tips.SetToolTip(solverButton, "marks the safe tiles and mines for you");
        tips.SetToolTip(autoButton, "clicks the safe tiles for you. H to pause");
        tips.SetToolTip(guessButton, "nothing safe? it just guesses the least risky tile");
        tips.SetToolTip(streamButton, "hides the overlay from obs and discord. check your preview tho");
        tips.SetToolTip(resetButton, "puts your settings back to default");

        LoadSettings();
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        Rectangle work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - Width - Theme.S(10), work.Bottom - Height - Theme.S(14));
    }

    static string SettingsPath
    {
        get { return Path.Combine(Path.GetDirectoryName(Application.ExecutablePath), "Sweeper.settings"); }
    }

    void LoadSettings()
    {
        if (!File.Exists(SettingsPath)) return;
        try
        {
            foreach (string line in File.ReadAllLines(SettingsPath))
            {
                if (line == "AutoGuess=True") solver.AutoGuess = true;
                else if (line == "Streamproof=False") solver.ShownOnStream = true;
            }
            RefreshSolverButtons();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void SaveSettings()
    {
        try
        {
            File.WriteAllLines(SettingsPath, new[]
            {
                "AutoGuess=" + solver.AutoGuess,
                "Streamproof=" + !solver.ShownOnStream
            });
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    void ToggleSolver()
    {
        if (solver.Running) solver.Stop();
        else solver.Start();
        RefreshSolverButtons();
    }

    void ToggleAuto()
    {
        solver.AutoSolve = !solver.AutoSolve;
        if (solver.AutoSolve && !solver.Running) solver.Start();
        RefreshSolverButtons();
    }

    void ToggleGuess()
    {
        solver.AutoGuess = !solver.AutoGuess;
        RefreshSolverButtons();
        SaveSettings();
    }

    void ToggleStream()
    {
        solver.ShownOnStream = !solver.ShownOnStream;
        RefreshSolverButtons();
        SaveSettings();
    }

    void RefreshSolverButtons()
    {
        solverButton.Checked = solver.Running;
        autoButton.Checked = solver.AutoSolve;
        guessButton.Checked = solver.AutoGuess;
        guessButton.Enabled = solver.AutoSolve;
        streamButton.Checked = !solver.ShownOnStream;
    }

    void ShowSolverStatus(string text)
    {
        bool on = text.Length > 0;
        statusLabel.Text = on ? text : "solver off";
        statusLabel.Color = on ? Theme.On : Theme.Off;
    }

    void ResetAll()
    {
        using (var dialog = new ConfirmForm("reset", "reset your settings?"))
        {
            if (dialog.ShowDialog(this) != DialogResult.Yes) return;
        }

        solver.Stop();
        solver.AutoGuess = false;
        solver.ShownOnStream = false;
        RefreshSolverButtons();
        SaveSettings();
    }

    void SetUpTray()
    {
        var menu = new ContextMenu();
        var show = new MenuItem("Show", (s, e) => ShowWindow()) { DefaultItem = true };
        menu.MenuItems.Add(show);
        menu.MenuItems.Add("Exit", (s, e) => Close());

        tray.Icon = Theme.SmallIcon;
        tray.Text = "Sweeper";
        tray.ContextMenu = menu;
        tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowWindow(); };
    }

    void ShowWindow()
    {
        tray.Visible = false;
        restoring = true;
        Show();
        WindowState = FormWindowState.Normal;
        restoring = false;
        Activate();
    }

    [DllImport("kernel32.dll")] static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);

    void HideToTray()
    {
        Hide();
        tray.Visible = true;

        GC.Collect();
        GC.WaitForPendingFinalizers();
        SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized && Visible && !restoring) HideToTray();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        solver.Stop();
        tray.Visible = false;
        base.OnFormClosing(e);
    }
}

static class Theme
{
    public static readonly Color
        Board = Color.FromArgb(0x53, 0x53, 0x53),
        Frame = Color.FromArgb(0x1E, 0x1E, 0x1E),
        Panel = Color.FromArgb(0x68, 0x68, 0x68),
        Hidden = Color.FromArgb(0xDA, 0xDA, 0xDA),
        HiddenEdge = Color.FromArgb(0xA3, 0xA3, 0xA3),
        Opened = Color.FromArgb(0xBC, 0xBC, 0xBC),
        OpenedEdge = Color.FromArgb(0x81, 0x81, 0x81),
        Gold = Color.FromArgb(0xFF, 0xE4, 0x93),
        GoldEdge = Color.FromArgb(0xA3, 0x97, 0x5D),
        GoldHover = Color.FromArgb(0xFF, 0xEE, 0xBB),
        On = Color.FromArgb(0x00, 0xFF, 0x00),
        Off = Color.FromArgb(0xFF, 0x00, 0x00),
        Text = Color.White;

    public static float Scale = 1f;
    public static FontFamily Family;
    public static Font Body;
    public static Icon AppIcon, SmallIcon;
    public static Image FlagImage;

    static readonly System.Drawing.Text.PrivateFontCollection fonts = new System.Drawing.Text.PrivateFontCollection();

    public static int S(float value)
    {
        return (int)Math.Round(value * Scale);
    }

    public static void Init()
    {
        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Scale = g.DpiX / 96f;

        byte[] font = Resource("Inconsolata-Regular.ttf");
        if (font != null)
        {
            IntPtr memory = Marshal.AllocCoTaskMem(font.Length);
            Marshal.Copy(font, 0, memory, font.Length);
            fonts.AddMemoryFont(memory, font.Length);
            Family = fonts.Families.FirstOrDefault();
        }
        if (Family == null) Family = new FontFamily("Consolas");
        Body = new Font(Family, 16 * Scale, FontStyle.Regular, GraphicsUnit.Pixel);

        AppIcon = LoadIcon(SystemInformation.IconSize);
        SmallIcon = LoadIcon(SystemInformation.SmallIconSize);
        byte[] flag = Resource("flag.png");
        if (flag != null) FlagImage = Image.FromStream(new MemoryStream(flag));
    }

    static byte[] Resource(string name)
    {
        using (Stream stream = typeof(Theme).Assembly.GetManifestResourceStream(name))
        {
            if (stream == null) return null;
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
    }

    static Icon LoadIcon(Size size)
    {
        byte[] data = Resource("Sweeper.ico");
        if (data == null) return SystemIcons.Application;
        using (var stream = new MemoryStream(data)) return new Icon(stream, size);
    }

    static GraphicsPath TextPath(string text, float size, RectangleF area, StringAlignment align)
    {
        var path = new GraphicsPath();
        using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = align, LineAlignment = StringAlignment.Center, FormatFlags = StringFormatFlags.NoWrap })
            path.AddString(text, Family, (int)FontStyle.Regular, size * Scale, area, format);
        return path;
    }

    public static float TextWidth(string text, float size)
    {
        using (GraphicsPath path = TextPath(text, size, new RectangleF(0, 0, 10000, 1000), StringAlignment.Near))
            return text.Trim().Length == 0 ? 0 : path.GetBounds().Right;
    }

    public static void DrawText(Graphics g, string text, float size, Color color, RectangleF area, StringAlignment align, bool stroke, float alpha)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (GraphicsPath path = TextPath(text, size, area, align))
        {
            if (stroke)
                using (var pen = new Pen(Color.FromArgb((int)(128 * alpha), 0, 0, 0), 2 * Scale) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            using (var brush = new SolidBrush(Color.FromArgb((int)(255 * alpha), color)))
                g.FillPath(brush, path);
        }
    }

    public static void Tile(Graphics g, Rectangle r, Color fill, Color edge, int border)
    {
        g.SmoothingMode = SmoothingMode.None;
        using (var brush = new SolidBrush(edge)) g.FillRectangle(brush, r);
        using (var brush = new SolidBrush(fill)) g.FillRectangle(brush, Rectangle.Inflate(r, -border, -border));
    }
}

class GameTip : Form
{
    const int TextSize = 16, MaxChars = 34, DelayMilliseconds = 400;
    readonly Dictionary<Control, string> texts = new Dictionary<Control, string>();
    readonly System.Windows.Forms.Timer delay = new System.Windows.Forms.Timer { Interval = DelayMilliseconds };
    Control pending;
    string[] lines = new string[0];

    public GameTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Theme.Board;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        delay.Tick += (s, e) => { delay.Stop(); if (pending != null) Popup(pending); };
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8;
            CreateParams p = base.CreateParams;
            p.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
            return p;
        }
    }

    public void SetToolTip(Control control, string text)
    {
        texts[control] = text;
        control.MouseEnter += (s, e) => { pending = control; delay.Stop(); delay.Start(); };
        control.MouseLeave += (s, e) => Dismiss();
        control.MouseDown += (s, e) => Dismiss();
    }

    void Dismiss()
    {
        pending = null;
        delay.Stop();
        Hide();
    }

    static string[] Wrap(string text)
    {
        var result = new List<string>();
        string line = "";
        foreach (string word in text.Split(' '))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > MaxChars)
            {
                result.Add(line);
                line = word;
            }
            else line = line.Length == 0 ? word : line + " " + word;
        }
        if (line.Length > 0) result.Add(line);
        return result.ToArray();
    }

    void Popup(Control control)
    {
        if (!control.IsHandleCreated || !control.Visible) return;
        lines = Wrap(texts[control]);
        float width = lines.Max(l => Theme.TextWidth(l, TextSize));
        int lineHeight = Theme.S(TextSize + 4);
        Size = new Size((int)Math.Ceiling(width) + Theme.S(20), lines.Length * lineHeight + Theme.S(12));

        Point cursor = Cursor.Position;
        Rectangle screen = Screen.FromPoint(cursor).WorkingArea;
        int x = Math.Max(screen.Left, Math.Min(cursor.X, screen.Right - Width));
        int y = cursor.Y + Theme.S(22);
        if (y + Height > screen.Bottom) y = cursor.Y - Height - Theme.S(6);
        Location = new Point(x, Math.Max(screen.Top, y));

        Invalidate();
        Show();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Theme.Tile(e.Graphics, ClientRectangle, Theme.Board, Theme.Frame, Theme.S(2));
        int lineHeight = Theme.S(TextSize + 4);
        for (int i = 0; i < lines.Length; i++)
            Theme.DrawText(e.Graphics, lines[i], TextSize, Theme.Text,
                new RectangleF(Theme.S(10), Theme.S(6) + i * lineHeight, Width - Theme.S(20), lineHeight), StringAlignment.Near, true, 1f);
    }
}

class CreditLabel : GameControl
{
    const string Lead = "made by ", User = "kx5qz";
    static readonly Color NameColor = Color.FromArgb(0x00, 0xA2, 0xFF);
    readonly float size;

    public CreditLabel(float size)
    {
        this.size = size;
        Cursor = Cursors.Default;
        Size = new Size((int)Math.Ceiling(Theme.TextWidth(Lead + User, size)) + Theme.S(4), Theme.S(size + 6));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float charWidth = Theme.TextWidth("MM", size) - Theme.TextWidth("M", size);
        float nameX = Theme.S(2) + charWidth * Lead.Length;
        Theme.DrawText(e.Graphics, Lead.TrimEnd(), size, Theme.Text, new RectangleF(Theme.S(2), 0, Width, Height), StringAlignment.Near, true, 1f);
        Theme.DrawText(e.Graphics, User, size, NameColor, new RectangleF(nameX, 0, Width, Height), StringAlignment.Near, true, 1f);
    }
}

class GameControl : Control
{
    protected bool hover;

    protected GameControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        SetStyle(ControlStyles.Selectable, false);
        BackColor = Color.Transparent;
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnEnabledChanged(EventArgs e)
    {
        Cursor = Enabled ? Cursors.Hand : Cursors.Default;
        Invalidate();
        base.OnEnabledChanged(e);
    }
}

class StrokeLabel : GameControl
{
    readonly float size;
    Color color = Theme.Text;
    bool autoFit;

    public StrokeLabel(string text, float size)
    {
        this.size = size;
        base.Text = text;
        Cursor = Cursors.Default;
        Height = Theme.S(size + 6);
    }

    public bool AutoFit
    {
        get { return autoFit; }
        set { autoFit = value; if (value) Width = (int)Math.Ceiling(Theme.TextWidth(Text, size)) + Theme.S(4); }
    }

    public override string Text
    {
        get { return base.Text; }
        set { if (base.Text != value) { base.Text = value; Invalidate(); } }
    }

    public Color Color
    {
        get { return color; }
        set { if (color != value) { color = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float fit = size;
        float width = Theme.TextWidth(Text, size);
        if (width > Width - Theme.S(4)) fit = size * (Width - Theme.S(4)) / width;
        Theme.DrawText(e.Graphics, Text, fit, color, new RectangleF(Theme.S(2), 0, Width, Height), StringAlignment.Near, true, 1f);
    }
}

class TileCheckBox : GameControl
{
    bool isChecked;

    public TileCheckBox(string text)
    {
        Text = text;
        Cursor = Cursors.Hand;
        Size = new Size(Theme.S(26 + 8) + (int)Math.Ceiling(Theme.TextWidth(text, 20)) + Theme.S(4), Theme.S(30));
        Margin = new Padding(0, Theme.S(3), 0, Theme.S(3));
    }

    public bool Checked
    {
        get { return isChecked; }
        set { if (isChecked != value) { isChecked = value; Invalidate(); } }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        int side = Theme.S(26);
        var tile = new Rectangle(0, (Height - side) / 2, side, side);

        if (!Enabled) Theme.Tile(g, tile, Theme.Opened, Theme.OpenedEdge, Math.Max(1, Theme.S(1)));
        else if (hover) Theme.Tile(g, tile, Theme.Gold, Theme.GoldEdge, Theme.S(2));
        else Theme.Tile(g, tile, Theme.Hidden, Theme.HiddenEdge, Theme.S(2));

        if (isChecked && Theme.FlagImage != null)
        {
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Theme.FlagImage, new Rectangle(tile.X, tile.Y, tile.Width, (int)(tile.Height * 0.9f)));
        }

        var textArea = new RectangleF(side + Theme.S(8), 0, Width - side - Theme.S(8), Height);
        Theme.DrawText(g, Text, 20, Theme.Text, textArea, StringAlignment.Near, true, Enabled ? 1f : 0.5f);
    }
}

class TileButton : GameControl, IButtonControl
{
    public enum Look { Gold, White }

    readonly Look look;

    public DialogResult DialogResult { get; set; }

    public TileButton(string text, int width, Look look)
    {
        this.look = look;
        Text = text;
        Cursor = Cursors.Hand;
        Size = new Size(Theme.S(width), Theme.S(36));
    }

    public void NotifyDefault(bool value) { }

    public void PerformClick()
    {
        if (Enabled) OnClick(EventArgs.Empty);
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        Form form = FindForm();
        if (DialogResult != DialogResult.None && form != null) form.DialogResult = DialogResult;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        var area = new Rectangle(0, 0, Width, Height);
        if (look == Look.Gold)
        {
            Theme.Tile(g, area, hover ? Theme.GoldHover : Theme.Gold, Theme.GoldEdge, Theme.S(2));
            Theme.DrawText(g, Text, 22, Theme.Text, area, StringAlignment.Center, true, 1f);
        }
        else
        {
            Theme.Tile(g, area, hover ? Color.FromArgb(0xE6, 0xE6, 0xE6) : Color.White, Color.Black, Theme.S(2));
            Theme.DrawText(g, Text, 22, Color.Black, area, StringAlignment.Center, false, 1f);
        }
    }
}

class GamePanel : TableLayoutPanel
{
    public GamePanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Theme.Panel;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        Theme.Tile(e.Graphics, ClientRectangle, Theme.Panel, Theme.Frame, Theme.S(2));
    }
}

class ThemedForm : Form
{
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    public ThemedForm()
    {
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        BackColor = Theme.Board;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Icon = Theme.AppIcon;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int on = 1;
        if (DwmSetWindowAttribute(Handle, 20, ref on, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, 19, ref on, sizeof(int));
        int frame = Theme.Frame.R | (Theme.Frame.G << 8) | (Theme.Frame.B << 16);
        int white = 0xFFFFFF;
        DwmSetWindowAttribute(Handle, 35, ref frame, sizeof(int));
        DwmSetWindowAttribute(Handle, 34, ref frame, sizeof(int));
        DwmSetWindowAttribute(Handle, 36, ref white, sizeof(int));
    }

    protected void SetContent(Control content)
    {
        content.Margin = new Padding(0);
        Controls.Add(content);
    }
}

class ConfirmForm : ThemedForm
{
    public ConfirmForm(string title, string question)
    {
        Text = title;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        ShowIcon = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;

        var yes = new TileButton("yea", 90, TileButton.Look.Gold) { DialogResult = DialogResult.Yes, Margin = new Padding(0, 0, Theme.S(10), 0) };
        var no = new TileButton("nah", 90, TileButton.Look.White) { DialogResult = DialogResult.No, Margin = new Padding(0) };
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, Theme.S(14), 0, 0) };
        buttons.Controls.Add(yes);
        buttons.Controls.Add(no);

        var panel = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, Padding = new Padding(Theme.S(16)) };
        panel.Controls.Add(new StrokeLabel(question, 20) { AutoFit = true, Margin = new Padding(0) });
        panel.Controls.Add(buttons);
        SetContent(panel);

        AcceptButton = no;
        CancelButton = no;
    }
}

class SolveResult
{
    public bool[] Safe, Mine;
    public int Guess = -1;
    public double GuessRisk;
    public bool Contradiction;
    public bool Partial;

    public double[] Risk;
    public double LogWeight = double.NaN;

    public int HintTarget = -1;
}

static class MineLogic
{
    public const int Hidden = -1, Flag = -2, HintFlag = -3, Blown = -4, Unread = -5;

    const int MaxEnumeratedSquares = 400;

    public static int MinesFor(int size)
    {
        switch (size)
        {
            case 6: return 3;
            case 8: return 8;
            case 9: return 12;
            case 16: return 50;
            case 30: return 200;
            case 33: return 400;
            default: return 0;
        }
    }

    class Constraint
    {
        public int Cell, Need;
        public List<int> Squares = new List<int>();
    }

    public static SolveResult SolveForgiving(int[] cells, int n, int totalMines, long budget)
    {
        SolveResult result = Solve(cells, n, totalMines, budget);
        if (!result.Contradiction || !cells.Contains(HintFlag)) return result;
        return Solve(cells.Select(c => c == HintFlag ? Unread : c).ToArray(), n, totalMines, budget);
    }

    public static int OpeningCell(int n)
    {
        return (n - 1) * n;
    }

    public static int HintsFor(int size)
    {
        switch (size)
        {
            case 6: case 8: return 5;
            case 9: return 4;
            case 16: case 30: return 3;
            case 33: return 1;
            default: return 0;
        }
    }

    public static double MinCleared = 0.2, HintRiskFloor = 0.08, LastHintRisk = 0.15, LastHintLateRisk = 0.05, LateBoard = 0.6;

    public static bool IsFresh(int[] cells)
    {
        foreach (int c in cells) if (c >= 0 || c == HintFlag || c == Blown) return false;
        return true;
    }

    public static bool ShouldHint(double risk, int hintsLeft, double cleared)
    {
        if (hintsLeft <= 0 || risk <= 0 || cleared <= 0 || cleared < MinCleared) return false;
        if (hintsLeft >= 2) return risk >= HintRiskFloor;
        return risk >= LastHintRisk || (cleared >= LateBoard && risk >= LastHintLateRisk);
    }

    public static long LookaheadBudget = 200000;
    public static bool UseLookahead = true;

    public static SolveResult SolveAndPlan(int[] cells, int n, int totalMines, long budget)
    {
        SolveResult result = Solve(cells, n, totalMines, budget);
        if (result.Contradiction && cells.Contains(HintFlag)) return SolveForgiving(cells, n, totalMines, budget);
        if (UseLookahead && !result.Contradiction && result.Guess >= 0 && !result.Partial && !double.IsNaN(result.LogWeight))
            Plan(cells, n, totalMines, result);
        if (result.HintTarget < 0) result.HintTarget = result.Guess;
        return result;
    }

    class Outlook
    {
        public int Cell;
        public double Risk, Progress, HintProgress, Expected;
    }

    static void Plan(int[] cells, int n, int totalMines, SolveResult r)
    {
        int total = n * n;
        var neighbors = new int[8];
        var frontier = new bool[total];
        for (int i = 0; i < total; i++)
        {
            if (cells[i] < 0) continue;
            int count = Neighbors(i, n, neighbors);
            for (int k = 0; k < count; k++) frontier[neighbors[k]] = true;
        }

        var open = new List<int>();
        for (int i = 0; i < total; i++) if (cells[i] == Hidden && !r.Mine[i] && !r.Safe[i]) open.Add(i);
        if (open.Count == 0) return;

        double best = r.GuessRisk;
        double band = best + Math.Max(0.01, best * 0.1);

        var picks = new List<int> { r.Guess };
        picks.AddRange(open.Where(i => frontier[i] && r.Risk[i] <= band).OrderBy(i => r.Risk[i]).Take(8));
        picks.AddRange(open.Where(i => frontier[i]).OrderBy(i => Math.Abs(r.Risk[i] - 0.5)).Take(6));

        Func<int, int> openNeighbors = i =>
        {
            int count = Neighbors(i, n, neighbors), u = 0;
            for (int k = 0; k < count; k++) if (cells[neighbors[k]] == Hidden || cells[neighbors[k]] == Flag || cells[neighbors[k]] == Unread) u++;
            return u;
        };
        picks.AddRange(open.Where(i => !frontier[i]).GroupBy(openNeighbors).OrderBy(g => g.Key).Take(3).Select(g => g.First()));

        var outlooks = new List<Outlook>();
        var trial = (int[])cells.Clone();
        foreach (int c in picks.Distinct())
        {
            Outlook o = Look(trial, n, totalMines, r, c);
            if (o != null) outlooks.Add(o);
        }
        if (outlooks.Count == 0) return;

        Outlook guess = outlooks.Where(o => o.Risk <= band)
            .OrderByDescending(o => Math.Round(o.Progress, 4)).ThenBy(o => o.Risk).ThenByDescending(o => o.Expected).FirstOrDefault();
        if (guess != null)
        {
            r.Guess = guess.Cell;
            r.GuessRisk = guess.Risk;
        }

        Outlook hint = outlooks.OrderByDescending(o => Math.Round(o.HintProgress, 4)).ThenByDescending(o => o.Risk).ThenByDescending(o => o.Expected).First();
        r.HintTarget = hint.Cell;
    }

    static Outlook Look(int[] trial, int n, int totalMines, SolveResult r, int c)
    {
        var o = new Outlook { Cell = c, Risk = r.Risk[c] };
        var neighbors = new int[8];
        int count = Neighbors(c, n, neighbors);
        int original = trial[c];
        try
        {
            for (int v = -1; v <= count; v++)
            {
                trial[c] = v < 0 ? HintFlag : v;
                SolveResult t = Solve(trial, n, totalMines, LookaheadBudget);
                if (t.Contradiction) continue;
                if (t.Partial || double.IsNaN(t.LogWeight)) return null;

                double share = Math.Exp(t.LogWeight - r.LogWeight);
                int gained = 0;
                for (int i = 0; i < trial.Length; i++) if (i != c && t.Safe[i] && trial[i] == Hidden && !r.Safe[i]) gained++;

                if (gained > 0) o.HintProgress += share;
                if (v >= 0)
                {
                    if (gained > 0) o.Progress += share;
                    o.Expected += share * gained;
                }
            }
        }
        finally { trial[c] = original; }
        return o;
    }

    public static SolveResult Solve(int[] cells, int n, int totalMines, long budget)
    {
        int total = n * n;
        var result = new SolveResult { Safe = new bool[total], Mine = new bool[total] };

        var unknown = new bool[total];
        int remaining = totalMines;
        for (int i = 0; i < total; i++)
        {
            if (cells[i] == Hidden || cells[i] == Flag || cells[i] == Unread) unknown[i] = true;
            else if (cells[i] == HintFlag || cells[i] == Blown) remaining--;
        }
        if (remaining < 0) { result.Contradiction = true; return result; }

        var neighbors = new int[8];
        var constraints = new List<Constraint>();
        for (int i = 0; i < total; i++)
        {
            if (cells[i] < 0) continue;
            var c = new Constraint { Cell = i, Need = cells[i] };
            int count = Neighbors(i, n, neighbors);
            for (int k = 0; k < count; k++)
            {
                int cell = cells[neighbors[k]];
                if (cell == HintFlag || cell == Blown) c.Need--;
                else if (unknown[neighbors[k]]) c.Squares.Add(neighbors[k]);
            }
            if (c.Need < 0 || c.Need > c.Squares.Count) { result.Contradiction = true; return result; }
            if (c.Squares.Count > 0) constraints.Add(c);
        }

        var fixedMine = new bool[total];
        var fixedSafe = new bool[total];
        if (!Propagate(constraints, n, fixedMine, fixedSafe)) { result.Contradiction = true; return result; }

        int unknownLeft = 0;
        for (int i = 0; i < total; i++)
        {
            if (fixedMine[i]) { result.Mine[i] = true; remaining--; }
            else if (fixedSafe[i]) result.Safe[i] = true;
            else if (unknown[i]) unknownLeft++;
        }
        if (remaining < 0) { result.Contradiction = true; return result; }

        var remainingConstraints = new List<Constraint>();
        foreach (Constraint c in constraints)
        {
            var left = new Constraint { Cell = c.Cell, Need = c.Need };
            foreach (int s in c.Squares)
            {
                if (fixedMine[s]) left.Need--;
                else if (!fixedSafe[s]) left.Squares.Add(s);
            }
            if (left.Squares.Count > 0) remainingConstraints.Add(left);
        }

        List<Group> groups = BuildGroups(remainingConstraints, total);
        long spent = 0;
        var inFrontier = new bool[total];
        var estimate = new double[total];
        foreach (Group g in groups)
        {
            foreach (int s in g.Squares) inFrontier[s] = true;
            long share = Math.Max(1, (budget - spent) / Math.Max(1, groups.Count));
            if (g.Squares.Count > MaxEnumeratedSquares || !g.Enumerate(share)) result.Partial = true;
            spent += g.Spent;
        }
        foreach (Group g in groups)
            if (g.Complete && g.Possible.All(w => w == 0)) { result.Contradiction = true; return result; }

        int outside = 0;
        for (int i = 0; i < total; i++) if (unknown[i] && !fixedMine[i] && !fixedSafe[i] && !inFrontier[i]) outside++;

        Combine(groups, remaining, outside, unknown, fixedMine, fixedSafe, inFrontier, result, estimate, remainingConstraints);
        if (result.Contradiction) return result;
        result.Risk = estimate;

        bool anyToClick = false;
        for (int i = 0; i < total; i++) if (result.Safe[i] && cells[i] == Hidden) anyToClick = true;
        if (!anyToClick)
        {
            double best = double.MaxValue;
            for (int i = 0; i < total; i++)
            {
                if (cells[i] != Hidden || result.Mine[i]) continue;
                if (estimate[i] < best) { best = estimate[i]; result.Guess = i; result.GuessRisk = estimate[i]; }
            }

            if (IsFresh(cells))
            {
                result.Guess = -1;
                result.GuessRisk = 0;
                result.Safe[OpeningCell(n)] = true;
            }
        }
        return result;
    }

    public static int Neighbors(int cell, int n, int[] output)
    {
        int x = cell % n, y = cell / n, count = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                if (dx == 0 && dy == 0) continue;
                int nx = x + dx, ny = y + dy;
                if (nx >= 0 && nx < n && ny >= 0 && ny < n) output[count++] = ny * n + nx;
            }
        }
        return count;
    }

    static bool Propagate(List<Constraint> constraints, int n, bool[] fixedMine, bool[] fixedSafe)
    {
        bool changed = true;
        while (changed)
        {
            changed = false;

            var open = new List<Constraint>();
            foreach (Constraint c in constraints)
            {
                var left = new Constraint { Cell = c.Cell, Need = c.Need };
                foreach (int s in c.Squares)
                {
                    if (fixedMine[s]) left.Need--;
                    else if (!fixedSafe[s]) left.Squares.Add(s);
                }
                if (left.Need < 0 || left.Need > left.Squares.Count) return false;
                if (left.Squares.Count == 0) continue;

                if (left.Need == 0) { foreach (int s in left.Squares) fixedSafe[s] = true; changed = true; }
                else if (left.Need == left.Squares.Count) { foreach (int s in left.Squares) fixedMine[s] = true; changed = true; }
                else open.Add(left);
            }
            if (changed) continue;

            for (int a = 0; a < open.Count && !changed; a++)
            {
                Constraint A = open[a];
                for (int b = 0; b < open.Count; b++)
                {
                    Constraint B = open[b];
                    if (a == b || A.Squares.Count >= B.Squares.Count) continue;
                    if (Math.Abs(A.Cell % n - B.Cell % n) > 2 || Math.Abs(A.Cell / n - B.Cell / n) > 2) continue;
                    if (!A.Squares.All(B.Squares.Contains)) continue;

                    List<int> diff = B.Squares.Where(s => !A.Squares.Contains(s)).ToList();
                    int needDiff = B.Need - A.Need;
                    if (needDiff < 0 || needDiff > diff.Count) return false;
                    if (needDiff == 0) { foreach (int s in diff) fixedSafe[s] = true; changed = true; break; }
                    if (needDiff == diff.Count) { foreach (int s in diff) fixedMine[s] = true; changed = true; break; }
                }
            }
        }
        return true;
    }

    class Group
    {
        public List<int> Squares = new List<int>();
        public List<Constraint> Rules = new List<Constraint>();
        public double[] Possible;
        public double[][] MineCounts;
        public bool Complete;
        public long Spent;

        int[] need, left;
        int[][] rulesOf;
        bool[] assigned;
        long limit;

        public bool Enumerate(long budget)
        {
            limit = budget;
            int m = Squares.Count;
            Possible = new double[m + 1];
            MineCounts = new double[m][];
            for (int i = 0; i < m; i++) MineCounts[i] = new double[m + 1];

            var position = new Dictionary<int, int>();
            for (int i = 0; i < m; i++) position[Squares[i]] = i;

            need = Rules.Select(r => r.Need).ToArray();
            left = Rules.Select(r => r.Squares.Count).ToArray();
            var lists = new List<int>[m];
            for (int i = 0; i < m; i++) lists[i] = new List<int>();
            for (int r = 0; r < Rules.Count; r++)
                foreach (int s in Rules[r].Squares) lists[position[s]].Add(r);
            rulesOf = lists.Select(l => l.ToArray()).ToArray();
            assigned = new bool[m];

            Complete = Walk(0, 0);
            return Complete;
        }

        bool Walk(int index, int mines)
        {
            if (++Spent > limit) return false;
            if (index == Squares.Count)
            {
                Possible[mines]++;
                Spent += Squares.Count;
                for (int i = 0; i < assigned.Length; i++) if (assigned[i]) MineCounts[i][mines]++;
                return true;
            }

            int[] rules = rulesOf[index];

            bool can = true;
            foreach (int r in rules) if (left[r] - 1 < need[r]) can = false;
            if (can)
            {
                foreach (int r in rules) left[r]--;
                assigned[index] = false;
                bool ok = Walk(index + 1, mines);
                foreach (int r in rules) left[r]++;
                if (!ok) return false;
            }

            can = true;
            foreach (int r in rules) if (need[r] < 1) can = false;
            if (can)
            {
                foreach (int r in rules) { left[r]--; need[r]--; }
                assigned[index] = true;
                bool ok = Walk(index + 1, mines + 1);
                foreach (int r in rules) { left[r]++; need[r]++; }
                assigned[index] = false;
                if (!ok) return false;
            }
            return true;
        }
    }

    static List<Group> BuildGroups(List<Constraint> constraints, int total)
    {
        var parent = new int[total];
        for (int i = 0; i < total; i++) parent[i] = i;
        Func<int, int> find = null;
        find = x => { while (parent[x] != x) { parent[x] = parent[parent[x]]; x = parent[x]; } return x; };

        foreach (Constraint c in constraints)
            for (int i = 1; i < c.Squares.Count; i++) parent[find(c.Squares[i])] = find(c.Squares[0]);

        var groups = new Dictionary<int, Group>();
        foreach (Constraint c in constraints)
        {
            int root = find(c.Squares[0]);
            Group g;
            if (!groups.TryGetValue(root, out g)) { g = new Group(); groups[root] = g; }
            g.Rules.Add(c);
        }

        foreach (Group g in groups.Values)
        {
            var seen = new HashSet<int>();
            var used = new HashSet<Constraint>();
            var queue = new Queue<Constraint>();
            queue.Enqueue(g.Rules[0]);
            used.Add(g.Rules[0]);
            while (queue.Count > 0)
            {
                Constraint c = queue.Dequeue();
                foreach (int s in c.Squares)
                {
                    if (!seen.Add(s)) continue;
                    g.Squares.Add(s);
                    foreach (Constraint other in g.Rules)
                        if (!used.Contains(other) && other.Squares.Contains(s)) { used.Add(other); queue.Enqueue(other); }
                }
            }
        }
        return groups.Values.ToList();
    }

    static double LogChoose(int n, int k)
    {
        double sum = 0;
        for (int i = 1; i <= k; i++) sum += Math.Log(n - k + i) - Math.Log(i);
        return sum;
    }

    static void Combine(List<Group> groups, int remaining, int outside, bool[] unknown, bool[] fixedMine, bool[] fixedSafe,
        bool[] inFrontier, SolveResult result, double[] estimate, List<Constraint> rules)
    {
        int total = unknown.Length;
        List<Group> done = groups.Where(g => g.Complete).ToList();
        bool partial = done.Count != groups.Count;

        Func<int, bool> possibleTotal = k => partial || (remaining - k >= 0 && remaining - k <= outside);

        foreach (Group g in groups.Where(x => !x.Complete))
            foreach (int s in g.Squares)
                foreach (Constraint r in g.Rules)
                    if (r.Squares.Contains(s)) estimate[s] = Math.Max(estimate[s], (double)r.Need / r.Squares.Count);

        var others = new double[done.Count][];
        for (int i = 0; i < done.Count; i++)
        {
            double[] poly = { 1 };
            for (int j = 0; j < done.Count; j++) if (j != i) poly = Multiply(poly, done[j].Possible);
            others[i] = poly;
        }
        double[] all = { 1 };
        foreach (Group g in done) all = Multiply(all, g.Possible);

        double top = double.NegativeInfinity;
        for (int k = 0; k < all.Length; k++)
            if (all[k] > 0 && remaining - k >= 0 && remaining - k <= outside) top = Math.Max(top, LogChoose(outside, remaining - k));
        Func<int, double> weight = k =>
            remaining - k < 0 || remaining - k > outside ? 0 : Math.Exp(LogChoose(outside, remaining - k) - top);

        bool anyTotal = false;
        double grand = 0, outsideMines = 0;
        bool outsideCanBeMine = false, outsideCanBeSafe = false;
        for (int k = 0; k < all.Length; k++)
        {
            if (all[k] <= 0 || !possibleTotal(k)) continue;
            anyTotal = true;
            int leftover = remaining - k;
            if (leftover > 0) outsideCanBeMine = true;
            if (leftover < outside) outsideCanBeSafe = true;
            if (!partial)
            {
                double w = all[k] * weight(k);
                grand += w;
                if (outside > 0) outsideMines += w * leftover / outside;
            }
        }
        if (!anyTotal) { result.Contradiction = true; return; }
        if (!partial && grand > 0) result.LogWeight = Math.Log(grand) + top;

        for (int g = 0; g < done.Count; g++)
        {
            Group group = done[g];
            for (int s = 0; s < group.Squares.Count; s++)
            {
                bool canBeMine = false, canBeSafe = false;
                double mineWeight = 0;
                for (int k = 0; k < group.Possible.Length; k++)
                {
                    double asMine = group.MineCounts[s][k], asSafe = group.Possible[k] - asMine;
                    if (asMine <= 0 && asSafe <= 0) continue;
                    for (int o = 0; o < others[g].Length; o++)
                    {
                        if (others[g][o] <= 0 || !possibleTotal(k + o)) continue;
                        if (asMine > 0) canBeMine = true;
                        if (asSafe > 0) canBeSafe = true;
                        if (!partial) mineWeight += asMine * others[g][o] * weight(k + o);
                    }
                }
                int square = group.Squares[s];
                if (!canBeMine) result.Safe[square] = true;
                if (!canBeSafe) result.Mine[square] = true;
                if (partial)
                {
                    double arrangements = group.Possible.Sum(), mines = 0;
                    for (int k = 0; k < group.Possible.Length; k++) mines += group.MineCounts[s][k];
                    estimate[square] = arrangements > 0 ? mines / arrangements : 1;
                }
                else estimate[square] = grand > 0 ? mineWeight / grand : 1;
            }
        }

        double outsideRisk = !partial && grand > 0 ? outsideMines / grand
            : outside > 0 ? Math.Min(1, (double)Math.Max(0, remaining) / (outside + rules.Count)) : 1;
        for (int i = 0; i < total; i++)
        {
            if (!unknown[i] || fixedMine[i] || fixedSafe[i] || inFrontier[i]) continue;
            if (!partial && !outsideCanBeMine) result.Safe[i] = true;
            if (!partial && !outsideCanBeSafe) result.Mine[i] = true;
            estimate[i] = outsideRisk;
        }
        for (int i = 0; i < total; i++)
        {
            if (result.Safe[i]) estimate[i] = 0;
            if (result.Mine[i]) estimate[i] = 1;
        }
    }

    static double[] Multiply(double[] a, double[] b)
    {
        var r = new double[a.Length + b.Length - 1];
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == 0) continue;
            for (int j = 0; j < b.Length; j++) r[i + j] += a[i] * b[j];
        }
        return r;
    }
}

class BoardReader
{
    public Rectangle Inner;
    public int Size;

    static readonly int[] Sizes = { 6, 8, 9, 16, 30, 33 };

    static readonly int[,] Digits =
    {
        { 0, 0, 255 }, { 0, 102, 0 }, { 255, 0, 0 }, { 0, 0, 128 },
        { 128, 0, 0 }, { 0, 128, 128 }, { 0, 0, 0 }, { 102, 102, 102 }
    };

    static bool Near(int c, int r, int g, int b, int tolerance)
    {
        return Math.Abs(((c >> 16) & 255) - r) <= tolerance && Math.Abs(((c >> 8) & 255) - g) <= tolerance && Math.Abs((c & 255) - b) <= tolerance;
    }

    static bool IsBorder(int c) { return Near(c, 30, 30, 30, 6); }
    static bool IsLine(int c) { return Near(c, 129, 129, 129, 14); }

    public static BoardReader Find(int[] px, int w, int h)
    {
        const int MinSide = 80;
        for (int y = 0; y + MinSide < h; y++)
        {
            int x = 0;
            while (x + MinSide < w)
            {
                if (!IsBorder(px[y * w + x])) { x++; continue; }
                int run = 0;
                while (x + run < w && IsBorder(px[y * w + x + run])) run++;
                if (run >= MinSide)
                {
                    BoardReader found = TryRun(px, w, h, x, y, run, MinSide);
                    if (found != null) return found;
                }
                x += run;
            }
        }
        return null;
    }

    static BoardReader TryRun(int[] px, int w, int h, int x0, int y, int run, int minSide)
    {
        int end = x0 + run;
        for (int t = 1; t <= 5 && y + t < h; t++)
        {
            int row = (y + t) * w;
            int i = x0 + 1;
            while (i < end)
            {
                if (IsBorder(px[row + i]) || !IsBorder(px[row + i - 1])) { i++; continue; }
                int a = i;
                while (i < end && !IsBorder(px[row + i])) i++;
                int b = i;
                if (b >= end || b - a < minSide - 2 * t) continue;
                if (!IsBorder(px[row - w + (a + b) / 2])) continue;

                int left = a - t, side = b - a + 2 * t;
                if (left < x0 || left + side > end || y + side > h) continue;

                bool sides = true;
                for (int k = 1; k <= t; k++)
                    sides &= IsBorder(px[row + a - k]) && IsBorder(px[row + b + k - 1]);
                if (!sides) continue;

                BoardReader found = TryOutline(px, w, left, y, side, t);
                if (found != null) return found;
            }
        }
        return null;
    }

    static BoardReader TryOutline(int[] px, int w, int x, int y, int side, int t)
    {
        int misses = 0;
        for (int i = 0; i < side; i++)
        {
            if (!IsBorder(px[(y + i) * w + x])) misses++;
            if (!IsBorder(px[(y + i) * w + x + side - 1])) misses++;
            if (!IsBorder(px[(y + side - 1) * w + x + i])) misses++;
        }
        if (misses > side / 20) return null;

        var inner = new Rectangle(x + t, y + t, side - 2 * t, side - 2 * t);
        var scores = new double[Sizes.Length];
        double top = 0;
        for (int i = 0; i < Sizes.Length; i++)
        {
            scores[i] = GridScore(px, w, inner, Sizes[i]);
            top = Math.Max(top, scores[i]);
        }
        if (top < 0.5) return null;

        for (int i = Sizes.Length - 1; i >= 0; i--)
            if (scores[i] >= top * 0.85) return new BoardReader { Inner = inner, Size = Sizes[i] };
        return null;
    }

    static double GridScore(int[] px, int w, Rectangle inner, int n)
    {
        double pitch = (double)inner.Width / n;
        int onLine = 0, offLine = 0, count = 0;
        for (int row = 0; row < n; row++)
        {
            int y = inner.Y + (int)((row + 0.5) * pitch);
            for (int k = 1; k < n; k++)
            {
                int line = inner.X + (int)Math.Round(k * pitch) - 1;
                int middle = inner.X + (int)((k + 0.5) * pitch);
                if (IsLine(px[y * w + line])) onLine++;
                if (IsLine(px[y * w + middle])) offLine++;
                count++;
            }
        }
        return count == 0 ? 0 : (double)(onLine - offLine) / count;
    }

    public static Point? FindHintButton(int[] px, int w, int h)
    {
        int left = w, top = h, right = -1, bottom = -1, count = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                if (!Near(px[y * w + x], 255, 228, 147, 24)) continue;
                count++;
                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }
        int width = right - left + 1, height = bottom - top + 1;
        if (width < 16 || height < 16 || width > 80 || height > 80 || Math.Abs(width - height) > width / 4 || count < width * height / 2) return null;
        return new Point((left + right) / 2, (top + bottom) / 2);
    }

    public static int CountWhite(int[] px)
    {
        return px.Count(c => Near(c, 255, 255, 255, 1));
    }

    public bool StillThere(int[] px, int w)
    {
        return IsBorder(px[(Inner.Y - 1) * w + Inner.X + Inner.Width / 2]) &&
               IsBorder(px[(Inner.Y + Inner.Height / 2) * w + Inner.X - 1]) &&
               IsBorder(px[Inner.Bottom * w + Inner.X + Inner.Width / 2]) &&
               IsBorder(px[(Inner.Y + Inner.Height / 2) * w + Inner.Right]);
    }

    public Rectangle CellBounds(int col, int row)
    {
        double pitch = (double)Inner.Width / Size;
        int x = Inner.X + (int)Math.Round(col * pitch), y = Inner.Y + (int)Math.Round(row * pitch);
        return new Rectangle(x, y, Inner.X + (int)Math.Round((col + 1) * pitch) - x, Inner.Y + (int)Math.Round((row + 1) * pitch) - y);
    }

    public int[] ReadAll(int[] px, int w)
    {
        var cells = new int[Size * Size];
        for (int row = 0; row < Size; row++)
            for (int col = 0; col < Size; col++)
                cells[row * Size + col] = ReadCell(px, w, CellBounds(col, row));
        return cells;
    }

    static int ReadCell(int[] px, int w, Rectangle cell)
    {
        int inset = Math.Max(2, cell.Width / 8);
        int light = 0, opened = 0, gold = 0, openedGold = 0, red = 0, black = 0, dark = 0, pixels = 0;
        for (int y = cell.Y + inset; y < cell.Bottom - inset; y++)
        {
            for (int x = cell.X + inset; x < cell.Right - inset; x++)
            {
                int c = px[y * w + x];
                if (SolverOverlay.IsMark(c)) continue;
                pixels++;
                if (Near(c, 219, 219, 219, 14)) light++;
                else if (Near(c, 188, 188, 188, 8)) opened++;
                else if (Near(c, 255, 228, 147, 18)) gold++;
                else if (Near(c, 218, 195, 126, 14)) openedGold++;
                else if (Near(c, 255, 0, 0, 60)) red++;
                else if (Near(c, 0, 0, 0, 40)) black++;
                else if (Near(c, 88, 88, 88, 30)) dark++;
            }
        }
        if (pixels == 0) return MineLogic.Unread;

        bool flagOverNumber = red >= 2 && black + dark >= 2;

        if (red >= pixels / 2 || (red >= pixels / 5 && red + black >= pixels / 2)) return MineLogic.Blown;
        if (gold >= pixels / 5) return MineLogic.HintFlag;
        if (light >= pixels / 4)
        {
            if (opened > light) return flagOverNumber ? MineLogic.Unread : ReadNumber(px, w, cell, inset, 188, 188, 188);
            return red >= 2 ? MineLogic.Flag : MineLogic.Hidden;
        }
        if (openedGold >= pixels / 3) return ReadNumber(px, w, cell, inset, 218, 195, 126);
        if (opened >= pixels / 3) return flagOverNumber ? MineLogic.Unread : ReadNumber(px, w, cell, inset, 188, 188, 188);
        return MineLogic.Unread;
    }

    static int ReadNumber(int[] px, int w, Rectangle cell, int inset, int bgR, int bgG, int bgB)
    {
        int deepest = 0;
        for (int y = cell.Y + inset; y < cell.Bottom - inset; y++)
        {
            for (int x = cell.X + inset; x < cell.Right - inset; x++)
            {
                int c = px[y * w + x];
                if (SolverOverlay.IsMark(c)) continue;
                deepest = Math.Max(deepest, Math.Abs(((c >> 16) & 255) - bgR) + Math.Abs(((c >> 8) & 255) - bgG) + Math.Abs((c & 255) - bgB));
            }
        }
        if (deepest < 36) return 0;

        int cutoff = Math.Max(36, deepest * 6 / 10);
        var votes = new int[8];
        for (int y = cell.Y + inset; y < cell.Bottom - inset; y++)
        {
            for (int x = cell.X + inset; x < cell.Right - inset; x++)
            {
                int c = px[y * w + x];
                if (SolverOverlay.IsMark(c)) continue;
                int r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
                if (Math.Abs(r - bgR) + Math.Abs(g - bgG) + Math.Abs(b - bgB) < cutoff) continue;

                int ur = r - bgR, ug = g - bgG, ub = b - bgB;
                int best = -1;
                double bestScore = 75 * 75;
                for (int d = 0; d < 8; d++)
                {
                    int vr = Digits[d, 0] - bgR, vg = Digits[d, 1] - bgG, vb = Digits[d, 2] - bgB;
                    double mix = Math.Max(0, Math.Min(1, (double)(ur * vr + ug * vg + ub * vb) / (vr * vr + vg * vg + vb * vb)));
                    double er = ur - mix * vr, eg = ug - mix * vg, eb = ub - mix * vb;

                    double score = er * er + eg * eg + eb * eb + 200 * (1 - mix);
                    if (score < bestScore) { bestScore = score; best = d; }
                }
                if (best >= 0) votes[best]++;
            }
        }

        int top = 0;
        for (int d = 1; d < 8; d++) if (votes[d] > votes[top]) top = d;
        return votes[top] > 0 ? top + 1 : MineLogic.Unread;
    }
}

class SolverOverlay : Form
{
    const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);

    static readonly Color SeeThrough = Color.FromArgb(255, 0, 255);
    public static readonly Color SafeColor = Color.FromArgb(0, 230, 70);
    public static readonly Color MineColor = Color.FromArgb(255, 30, 30);
    public static readonly Color GuessColor = Color.FromArgb(255, 190, 0);
    static readonly int SafeArgb = SafeColor.ToArgb(), MineArgb = MineColor.ToArgb(), GuessArgb = GuessColor.ToArgb();

    public static bool IsMark(int argb)
    {
        return argb == SafeArgb || argb == MineArgb || argb == GuessArgb;
    }

    bool shownOnStream;

    public bool ShownOnStream
    {
        set
        {
            shownOnStream = value;
            if (IsHandleCreated) ApplyCaptureSetting();
        }
    }

    void ApplyCaptureSetting()
    {
        SetWindowDisplayAffinity(Handle, shownOnStream ? WDA_NONE : WDA_EXCLUDEFROMCAPTURE);
    }

    BoardReader board;
    int[] cells;
    SolveResult result;

    public SolverOverlay()
    {
        Text = "Sweeper overlay";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = SeeThrough;
        TransparencyKey = SeeThrough;
        TopMost = true;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation { get { return true; } }

    protected override CreateParams CreateParams
    {
        get
        {
            const int WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000, WS_EX_TOPMOST = 0x8;
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return parameters;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyCaptureSetting();
    }

    public void ShowMarks(Rectangle screenArea, BoardReader reader, int[] readCells, SolveResult solved)
    {
        bool same = Visible && board == reader && result == solved && cells != null && cells.SequenceEqual(readCells);
        board = reader;
        cells = readCells;
        result = solved;
        if (same) return;
        Bounds = screenArea;
        if (!Visible) Show();
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.Clear(SeeThrough);
        if (board == null || cells == null) return;

        if (result != null)
        {
            using (var safe = new Pen(SafeColor, 2))
            using (var mine = new Pen(MineColor, 2))
            using (var guess = new Pen(GuessColor, 2))
            {
                for (int i = 0; i < cells.Length; i++)
                {
                    bool isSafe = result.Safe[i] && (cells[i] == MineLogic.Hidden || cells[i] == MineLogic.Flag);
                    bool isMine = result.Mine[i] && cells[i] == MineLogic.Hidden;
                    bool isGuess = i == result.Guess;
                    if (!isSafe && !isMine && !isGuess) continue;

                    Rectangle cell = board.CellBounds(i % board.Size, i / board.Size);
                    var r = new Rectangle(cell.X - board.Inner.X + 1, cell.Y - board.Inner.Y + 1, cell.Width - 3, cell.Height - 3);
                    if (isMine)
                    {
                        int size = Math.Max(4, r.Width / 3);
                        using (var fill = new SolidBrush(MineColor))
                            g.FillRectangle(fill, r.X + (r.Width - size) / 2 + 1, r.Y + (r.Height - size) / 2 + 1, size, size);
                    }
                    else g.DrawRectangle(isGuess ? guess : safe, r);
                }
            }
        }
    }
}

class MineSolver
{
    const string GameProcessPrefix = "Roblox";
    const int WatchingMilliseconds = 50, SearchingMilliseconds = 1000, SettleMilliseconds = 100;
    int readChangedAt;
    const long SolveBudget = 3000000;

    [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr window, ref POINT point);

    const int ClickStepMilliseconds = 40, RetryMilliseconds = 350, MouseIdleMilliseconds = 1000, MaxClickAttempts = 3, ClickDwellSteps = 1, ClickHoldSteps = 1;
    int clickHold;
    const int INPUT_MOUSE = 0, SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79;
    const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4, MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

    readonly HashSet<int> stuckTiles = new HashSet<int>();

    bool outOfHints;

    bool opening, wasFresh;

    readonly HashSet<int> knownMines = new HashSet<int>();
    int hintsUsed;

    void ClearTileMemory()
    {
        stuckTiles.Clear();
    }

    void ForgetGame()
    {
        knownMines.Clear();
        hintsUsed = 0;
    }

    void NewGame()
    {
        outOfHints = false;
        opening = true;
        hintAttempts = 0;
        hintPending = false;
        clickAttempts.Clear();
        clickedAt.Clear();
        ClearTileMemory();
        ForgetGame();
    }

    int[] WithKnownMines(int[] cells)
    {
        knownMines.RemoveWhere(i => i >= cells.Length || cells[i] >= 0);
        if (knownMines.Count == 0) return (int[])cells.Clone();
        var view = (int[])cells.Clone();
        foreach (int i in knownMines)
            if (view[i] == MineLogic.Flag || view[i] == MineLogic.Hidden || view[i] == MineLogic.Unread) view[i] = MineLogic.HintFlag;
        return view;
    }

    int OpeningCell()
    {
        return MineLogic.OpeningCell(board.Size);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct MOUSE_EVENT { public int type; public MOUSEINPUT mi; }

    [DllImport("user32.dll")] static extern uint SendInput(uint count, MOUSE_EVENT[] inputs, int size);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int index);

    readonly System.Windows.Forms.Timer clickTimer = new System.Windows.Forms.Timer { Interval = ClickStepMilliseconds };
    readonly Dictionary<int, int> clickAttempts = new Dictionary<int, int>();
    readonly Dictionary<int, int> clickedAt = new Dictionary<int, int>();
    Point clickTarget, lastCursor;
    int clickStep = -1, cursorMovedAt;
    bool buttonDown;

    const int HintScanWidth = 400, HintWaitMilliseconds = 2000, MaxHintAttempts = 3, HintDwellSteps = 5, HintArmedWhitePixels = 100;
    int hintClickedAt, hintAttempts, hintTile, clickDwell;
    bool hintPending, hintTileClicked;

    public bool AutoSolve, AutoGuess;

    const int VK_H = 0x48, PauseKeyMilliseconds = 30;
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    readonly System.Windows.Forms.Timer pauseKeyTimer = new System.Windows.Forms.Timer { Interval = PauseKeyMilliseconds };
    bool autoPaused, pauseKeyDown;

    void CheckPauseKey()
    {
        bool down = GetAsyncKeyState(VK_H) < 0;
        bool pressed = down && !pauseKeyDown;
        pauseKeyDown = down;

        if (!AutoSolve)
        {
            autoPaused = false;
            return;
        }
        if (!pressed || !IsGameForeground()) return;

        autoPaused = !autoPaused;
        if (autoPaused) CancelClick();
        else hintClickedAt = Environment.TickCount;
    }

    readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
    SolverOverlay overlay;
    readonly Action<string> report;

    BoardReader board;
    Rectangle boardArea;
    int[] lastRead, solvedRead;
    SolveResult lastResult;
    string lastMessage = "";
    bool solving;

    public bool Running { get { return timer.Enabled; } }

    bool shownOnStream;

    public bool ShownOnStream
    {
        get { return shownOnStream; }
        set
        {
            shownOnStream = value;
            if (overlay != null) overlay.ShownOnStream = value;
        }
    }

    public MineSolver(Action<string> report)
    {
        this.report = report;
        timer.Tick += (s, e) => Tick();
        clickTimer.Tick += (s, e) => ClickTick();
        pauseKeyTimer.Tick += (s, e) => CheckPauseKey();
    }

    public void Start()
    {
        if (overlay == null)
        {
            overlay = new SolverOverlay { ShownOnStream = shownOnStream };
            IntPtr unused = overlay.Handle;
        }
        timer.Interval = SearchingMilliseconds;
        timer.Start();
        pauseKeyTimer.Start();
        Tick();
    }

    public void Stop()
    {
        timer.Stop();
        pauseKeyTimer.Stop();
        AutoSolve = false;
        autoPaused = false;
        Forget();
        if (overlay != null) overlay.Hide();
        report("");
    }

    void Forget()
    {
        board = null;
        lastRead = solvedRead = null;
        lastResult = null;
        lastMessage = "";
        CancelClick();
        clickAttempts.Clear();
        clickedAt.Clear();
        hintPending = false;
        hintAttempts = 0;
        outOfHints = false;
        opening = wasFresh = false;
        ClearTileMemory();
    }

    void AutoStep(int[] cells)
    {
        bool fresh = MineLogic.IsFresh(cells);
        if (fresh && !wasFresh)
        {
            if (AutoSolve) NewGame();
            else ForgetGame();
        }
        wasFresh = fresh;

        if (!AutoSolve || autoPaused || clickStep >= 0 || lastResult == null || solvedRead == null) return;
        if (hintPending && !ResolveHint(cells)) return;

        for (int i = 0; i < cells.Length; i++)
        {
            if (cells[i] != MineLogic.Hidden)
            {
                clickAttempts.Remove(i);
                clickedAt.Remove(i);
            }
            if (cells[i] != solvedRead[i] && solvedRead[i] != MineLogic.Hidden && solvedRead[i] != MineLogic.Unread && cells[i] != MineLogic.Unread)
            {
                hintPending = false;
                hintAttempts = 0;
                ClearTileMemory();
                return;
            }
        }

        Point cursor = Cursor.Position;
        int now = Environment.TickCount;
        if (cursor != lastCursor)
        {
            lastCursor = cursor;
            cursorMovedAt = now;
        }
        if (now - cursorMovedAt < MouseIdleMilliseconds) return;

        if (opening && !fresh) opening = false;
        if (opening)
        {
            int first = OpeningCell();
            int attempts;
            clickAttempts.TryGetValue(first, out attempts);
            if ((cells[first] == MineLogic.Hidden || cells[first] == MineLogic.Unread) && attempts < MaxClickAttempts)
            {
                TryClick(first, now);
                return;
            }
            opening = false;
        }

        for (int i = 0; i < cells.Length; i++)
        {
            if (lastResult.Safe[i] && cells[i] == MineLogic.Hidden && TryClick(i, now)) return;
        }

        if (stuckTiles.Count > 0 && !Enumerable.Range(0, cells.Length).Any(i => lastResult.Safe[i] && cells[i] == MineLogic.Hidden && !stuckTiles.Contains(i)))
        {
            Rescan();
            return;
        }

        int guess = lastResult.Guess;
        if (guess < 0 || cells[guess] != MineLogic.Hidden || !solvedRead.SequenceEqual(cells)) return;

        if (WantsHint(cells, lastResult.GuessRisk) && ClickHint(now)) return;

        if (AutoGuess || lastResult.GuessRisk == 0) TryClick(guess, now);
    }

    bool WantsHint(int[] cells, double risk)
    {
        if (outOfHints) return false;

        int left = Math.Max(1, MineLogic.HintsFor(board.Size) - hintsUsed);
        int safeTotal = cells.Length - MineLogic.MinesFor(board.Size);
        double cleared = safeTotal > 0 ? (double)cells.Count(c => c >= 0) / safeTotal : 0;
        return MineLogic.ShouldHint(risk, left, cleared);
    }

    bool ClickHint(int now)
    {
        var area = new Rectangle(boardArea.X + board.Inner.X - HintScanWidth, boardArea.Y + board.Inner.Y - 20, HintScanWidth - 6, 120);
        Point? button = BoardReader.FindHintButton(Capture(area), area.Width, area.Height);
        if (button == null) return false;

        hintAttempts++;
        hintPending = true;
        hintTileClicked = false;
        hintTile = lastResult.HintTarget >= 0 && lastRead != null && lastRead[lastResult.HintTarget] == MineLogic.Hidden ? lastResult.HintTarget : lastResult.Guess;
        hintClickedAt = now;
        lastMessage = "using a hint";

        if (hintAttempts > 1) SendMouse(area.X + button.Value.X + 60, area.Y + button.Value.Y + 60, 0);
        StartClick(new Point(area.X + button.Value.X, area.Y + button.Value.Y), HintDwellSteps + 3 * (hintAttempts - 1));
        return true;
    }

    void HintArmedStep()
    {
        report("hint's armed");
        if (!hintPending) DisarmIfLeftBehind();
        if (!AutoSolve || autoPaused || !hintPending || clickStep >= 0) return;

        int now = Environment.TickCount;
        if (hintTileClicked)
        {
            if (now - hintClickedAt < HintWaitMilliseconds) return;
        }

        Point cursor = Cursor.Position;
        if (Math.Abs(cursor.X - clickTarget.X) > 3 || Math.Abs(cursor.Y - clickTarget.Y) > 3) return;

        hintTileClicked = true;
        hintClickedAt = now;
        StartClick(CellCentre(hintTile), HintDwellSteps);
    }

    const int LeftBehindMilliseconds = 2000;
    Point armedCursor;
    int armedStillSince;

    void DisarmIfLeftBehind()
    {
        if (!AutoSolve || autoPaused || clickStep >= 0) return;
        int now = Environment.TickCount;
        Point cursor = Cursor.Position;
        if (cursor != armedCursor || now - armedStillSince > LeftBehindMilliseconds * 2)
        {
            armedCursor = cursor;
            armedStillSince = now;
            return;
        }
        if (now - armedStillSince < LeftBehindMilliseconds) return;

        var area = new Rectangle(boardArea.X + board.Inner.X - HintScanWidth, boardArea.Y + board.Inner.Y - 20, HintScanWidth - 6, 120);
        Point? button = BoardReader.FindHintButton(Capture(area), area.Width, area.Height);
        armedStillSince = now;
        if (button != null) StartClick(new Point(area.X + button.Value.X, area.Y + button.Value.Y), HintDwellSteps);
    }

    bool ResolveHint(int[] cells)
    {
        if (hintTileClicked && (cells[hintTile] >= 0 || cells[hintTile] == MineLogic.HintFlag || cells[hintTile] == MineLogic.Flag))
        {
            if (cells[hintTile] == MineLogic.Flag) knownMines.Add(hintTile);
            hintsUsed++;
            hintAttempts = 0;
            hintPending = false;
            return true;
        }

        if (Environment.TickCount - hintClickedAt < HintWaitMilliseconds) return false;
        hintPending = false;
        if (hintAttempts < MaxHintAttempts) return true;

        outOfHints = true;
        hintAttempts = 0;
        lastMessage = "outta hints";
        return true;
    }

    void Rescan()
    {
        Forget();
        timer.Interval = SearchingMilliseconds;
        report("rescanning");
    }

    bool HintArmed()
    {
        var area = new Rectangle(boardArea.X + board.Inner.X - 290, boardArea.Y + board.Inner.Y + 52, 284, 70);
        return BoardReader.CountWhite(Capture(area)) >= HintArmedWhitePixels;
    }

    Point CellCentre(int i)
    {
        Rectangle cell = board.CellBounds(i % board.Size, i / board.Size);
        return new Point(boardArea.X + cell.X + cell.Width / 2, boardArea.Y + cell.Y + cell.Height / 2);
    }

    void StartClick(Point target, int dwell)
    {
        clickTarget = target;
        clickDwell = dwell;
        clickStep = 0;

        int pitch = board.Inner.Width / board.Size;
        Point cursor = Cursor.Position;
        if (Math.Abs(cursor.X - target.X) <= pitch / 2 && Math.Abs(cursor.Y - target.Y) <= pitch / 2)
        {
            SendMouse(target.X + (target.X > boardArea.X + boardArea.Width / 2 ? -2 : 2) * pitch, target.Y, 0);
            clickStep = 5;
        }
        clickTimer.Start();
    }

    bool TryClick(int i, int now)
    {
        int clicked;
        if (clickedAt.TryGetValue(i, out clicked) && now - clicked < RetryMilliseconds) return false;

        int attempts;
        clickAttempts.TryGetValue(i, out attempts);
        if (attempts >= MaxClickAttempts)
        {
            if (!lastResult.Safe[i])
            {
                Rescan();
                return true;
            }

            stuckTiles.Add(i);
            return false;
        }

        clickAttempts[i] = attempts + 1;
        clickedAt[i] = now;
        StartClick(CellCentre(i), ClickDwellSteps);
        return true;
    }

    void ClickTick()
    {
        if (!IsGameForeground())
        {
            CancelClick();
            return;
        }

        switch (clickStep)
        {
            case 5:
                clickStep = 0;
                break;
            case 0:
                SendMouse(clickTarget.X + 1, clickTarget.Y, 0);
                clickStep = 1;
                break;
            case 1:
                SendMouse(clickTarget.X, clickTarget.Y, 0);
                if (clickDwell > 0) clickDwell--;
                else clickStep = 2;
                break;
            case 2:
                SendMouse(clickTarget.X, clickTarget.Y, MOUSEEVENTF_LEFTDOWN);
                buttonDown = true;
                clickHold = ClickHoldSteps;
                clickStep = 3;
                break;
            default:
                if (clickHold > 0)
                {
                    clickHold--;
                    break;
                }
                CancelClick();
                lastCursor = clickTarget;
                break;
        }
    }

    void CancelClick()
    {
        clickTimer.Stop();
        clickStep = -1;
        if (!buttonDown) return;
        buttonDown = false;
        SendButton(MOUSEEVENTF_LEFTUP);
    }

    static void SendMouse(int x, int y, uint button)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int width = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), height = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var input = new MOUSE_EVENT { type = INPUT_MOUSE };
        input.mi.dx = (int)Math.Round((x - left) * 65535.0 / (width - 1));
        input.mi.dy = (int)Math.Round((y - top) * 65535.0 / (height - 1));
        input.mi.dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | button;
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(MOUSE_EVENT)));
    }

    static void SendButton(uint button)
    {
        var input = new MOUSE_EVENT { type = INPUT_MOUSE };
        input.mi.dwFlags = button;
        SendInput(1, new[] { input }, Marshal.SizeOf(typeof(MOUSE_EVENT)));
    }

    static bool IsGameForeground()
    {
        return Processes.ProcessName(Processes.ForegroundProcess()).StartsWith(GameProcessPrefix, StringComparison.OrdinalIgnoreCase);
    }

    void Tick()
    {
        IntPtr window = GetForegroundWindow();
        if (!IsGameForeground())
        {
            Forget();
            overlay.Hide();
            timer.Interval = SearchingMilliseconds;
            report("waiting for roblox");
            return;
        }

        if (board == null)
        {
            Search(window);
            return;
        }

        if (HintArmed())
        {
            HintArmedStep();
            return;
        }

        int[] px = Capture(boardArea);
        if (!board.StillThere(px, boardArea.Width))
        {
            Forget();
            ForgetGame();
            overlay.Hide();
            timer.Interval = SearchingMilliseconds;
            report("looking for a board");
            return;
        }
        Read(px);
    }

    void Search(IntPtr window)
    {
        RECT client;
        var origin = new POINT();
        if (!GetClientRect(window, out client) || !ClientToScreen(window, ref origin) || client.Right < 100 || client.Bottom < 100)
        {
            report("looking for a board");
            return;
        }

        var area = new Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
        int[] px = Capture(area);
        BoardReader found = BoardReader.Find(px, area.Width, area.Height);
        if (found == null)
        {
            overlay.Hide();
            report("looking for a board");
            return;
        }

        const int Margin = 8;
        boardArea = new Rectangle(area.X + found.Inner.X - Margin, area.Y + found.Inner.Y - Margin, found.Inner.Width + 2 * Margin, found.Inner.Height + 2 * Margin);
        board = new BoardReader { Inner = new Rectangle(Margin, Margin, found.Inner.Width, found.Inner.Height), Size = found.Size };
        timer.Interval = WatchingMilliseconds;
        Read(Capture(boardArea));
    }

    void Read(int[] px)
    {
        int[] cells = board.ReadAll(px, boardArea.Width);
        if (cells.Contains(MineLogic.Blown))
        {
            CancelClick();
            Show(cells, null, "hit a mine, unlucky");
            return;
        }

        AutoStep(cells);

        if (lastRead == null || !lastRead.SequenceEqual(cells)) readChangedAt = Environment.TickCount;
        bool settled = Environment.TickCount - readChangedAt >= SettleMilliseconds;
        lastRead = cells;
        if (!settled || solving) return;
        if (solvedRead != null && solvedRead.SequenceEqual(cells))
        {
            Show(cells, lastResult, lastMessage);
            return;
        }

        solving = true;
        int size = board.Size, mines = MineLogic.MinesFor(size);
        var snapshot = (int[])cells.Clone();
        int[] view = WithKnownMines(cells);
        System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            SolveResult result = MineLogic.SolveAndPlan(view, size, mines, SolveBudget);
            try { overlay.BeginInvoke(new Action(() => Finished(snapshot, result))); }
            catch (InvalidOperationException) { }
        });
    }

    void Finished(int[] cells, SolveResult result)
    {
        solving = false;
        if (!Running || board == null) return;

        solvedRead = cells;
        lastResult = result.Contradiction ? null : result;

        if (result.Contradiction) lastMessage = "can't read the board";
        else
        {
            int safe = 0, mines = 0;
            for (int i = 0; i < cells.Length; i++)
            {
                if (result.Safe[i] && cells[i] == MineLogic.Hidden) safe++;
                if (result.Mine[i] && cells[i] == MineLogic.Hidden) mines++;
            }
            if (MineLogic.IsFresh(cells)) lastMessage = "ready, first click's free";
            else if (safe > 0) lastMessage = safe + " safe" + (mines > 0 ? ", " + mines + " mines" : "");
            else if (result.Guess >= 0) lastMessage = result.GuessRisk == 0 ? "first click" : "guess, " + Math.Round(result.GuessRisk * 100) + "% risk";
            else lastMessage = mines > 0 ? mines + " mines" : "done";

            int unread = cells.Count(c => c == MineLogic.Unread);
            if (unread > 0) lastMessage += " (" + unread + " unread)";
        }
        Show(cells, lastResult, lastMessage);
        if (result.Contradiction && AutoSolve) Rescan();
    }

    void Show(int[] cells, SolveResult result, string message)
    {
        var screen = new Rectangle(boardArea.X + board.Inner.X, boardArea.Y + board.Inner.Y, board.Inner.Width, board.Inner.Height);
        overlay.ShowMarks(screen, board, cells, result);
        report(autoPaused ? "paused, H to continue" : message.Length > 0 ? message : "reading the board");
    }

    Bitmap image;
    int[] pixels;

    int[] Capture(Rectangle area)
    {
        if (image == null || image.Width != area.Width || image.Height != area.Height)
        {
            if (image != null) image.Dispose();
            image = new Bitmap(area.Width, area.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            pixels = new int[area.Width * area.Height];
        }
        using (Graphics g = Graphics.FromImage(image))
            g.CopyFromScreen(area.X, area.Y, 0, 0, area.Size, CopyPixelOperation.SourceCopy);

        var data = image.LockBits(new Rectangle(0, 0, area.Width, area.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
        image.UnlockBits(data);
        return pixels;
    }
}
