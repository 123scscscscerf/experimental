using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Reflection;
using System.Runtime.InteropServices;

public static class Theme
{
    private static readonly PrivateFontCollection Fonts = new();
    private static FontFamily? _familyRegular;
    private static FontFamily? _familyBold;

    public static Font FontRegular { get; private set; } = new("Segoe UI", 11f, FontStyle.Regular);
    public static Font FontBold { get; private set; } = new("Segoe UI", 11f, FontStyle.Bold);
    public static Font FontTitle { get; private set; } = new("Segoe UI", 26f, FontStyle.Bold);

    public static readonly Color GradientTop = Color.White;
    public static readonly Color GradientMid = Color.White;
    public static readonly Color GradientBottom = Color.White;

    public static void Init()
    {
        TryLoadFont("Inter-Regular.ttf", false);
        TryLoadFont("Inter-SemiBold.ttf", true);

        var regularFamily = _familyRegular ?? new FontFamily("Segoe UI");
        var boldFamily = _familyBold ?? regularFamily;

        FontRegular = new Font(regularFamily, 11f, FontStyle.Regular, GraphicsUnit.Point);
        FontBold = new Font(boldFamily, 11f, FontStyle.Bold, GraphicsUnit.Point);
        FontTitle = new Font(boldFamily, 30f, FontStyle.Bold, GraphicsUnit.Point);
    }

    private static void TryLoadFont(string endsWith, bool bold)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(endsWith, StringComparison.OrdinalIgnoreCase));
            if (name is null) return;
            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var data = ms.ToArray();
            var ptr = Marshal.AllocCoTaskMem(data.Length);
            Marshal.Copy(data, 0, ptr, data.Length);
            Fonts.AddMemoryFont(ptr, data.Length);
            Marshal.FreeCoTaskMem(ptr);
            var family = Fonts.Families.LastOrDefault();
            if (family is null) return;
            if (bold) _familyBold = family;
            else _familyRegular = family;
        }
        catch
        {
            // graceful fallback to Segoe UI
        }
    }

    public static void EnableDoubleBuffer(Form form)
    {
        typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(form, true, null);

        var setStyle = typeof(Control).GetMethod("SetStyle", BindingFlags.Instance | BindingFlags.NonPublic);
        setStyle?.Invoke(form, [ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true]);

        var updateStyles = typeof(Control).GetMethod("UpdateStyles", BindingFlags.Instance | BindingFlags.NonPublic);
        updateStyles?.Invoke(form, null);
    }

    public static void Apply(Control root)
    {
        foreach (Control c in root.Controls)
        {
            switch (c)
            {
                case Button:
                    c.Font = FontBold;
                    break;
                case Label lbl when lbl.Font.Size >= 24:
                    c.Font = FontTitle;
                    break;
                default:
                    c.Font = FontRegular;
                    break;
            }
            Apply(c);
        }
    }

    public static Color CardColor => Color.White;
}

public class BackgroundGradientPanel : Panel
{
    private Bitmap? _cache;
    private Size _cacheSize;

    public BackgroundGradientPanel()
    {
        Dock = DockStyle.Fill;
        DoubleBuffered = true;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (_cacheSize != Size)
        {
            _cache?.Dispose();
            _cache = null;
            _cacheSize = Size;
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        if (_cache is null)
        {
            _cache = new Bitmap(Width, Height);
            using var g = Graphics.FromImage(_cache);
            g.Clear(Color.White);
        }

        e.Graphics.DrawImageUnscaled(_cache, Point.Empty);
    }
}

public class TransparentPanel : Panel
{
    public TransparentPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }
}

public class TransparentTableLayoutPanel : TableLayoutPanel
{
    public TransparentTableLayoutPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }
}

public class TransparentFlowLayoutPanel : FlowLayoutPanel
{
    public TransparentFlowLayoutPanel()
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer, true);
        BackColor = Color.Transparent;
        DoubleBuffered = true;
    }
}
