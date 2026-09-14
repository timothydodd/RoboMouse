using System.Drawing.Drawing2D;

namespace RoboMouse.App;

/// <summary>
/// Shared look for the app's windows: one font, one palette (taken from the app icon) and small
/// factory helpers so every form builds the same kinds of controls the same way.
/// </summary>
internal static class Ui
{
    // Palette
    public static readonly Color Accent = Color.FromArgb(30, 100, 230);
    public static readonly Color AccentDark = Color.FromArgb(22, 78, 190);
    public static readonly Color AccentSoft = Color.FromArgb(232, 240, 255);
    public static readonly Color Window = Color.FromArgb(249, 250, 252);
    public static readonly Color Card = Color.White;
    public static readonly Color Border = Color.FromArgb(226, 230, 236);
    public static readonly Color Text = Color.FromArgb(28, 32, 40);
    public static readonly Color Muted = Color.FromArgb(108, 116, 130);
    public static readonly Color Sidebar = Color.FromArgb(240, 243, 248);

    public static readonly Color Green = Color.FromArgb(34, 170, 90);
    public static readonly Color Orange = Color.FromArgb(235, 130, 30);
    public static readonly Color Red = Color.FromArgb(214, 60, 60);
    public static readonly Color Grey = Color.FromArgb(150, 158, 170);

    // Type
    public static readonly Font Body = new("Segoe UI", 9.75f);
    public static readonly Font Small = new("Segoe UI", 8.75f);
    public static readonly Font Strong = new("Segoe UI", 9.75f, FontStyle.Bold);
    public static readonly Font Title = new("Segoe UI Semibold", 15f);
    public static readonly Font Heading = new("Segoe UI Semibold", 13f);
    public static readonly Font Mono = new("Consolas", 11f);

    public const int Gap = 8;
    public const int Pad = 20;

    /// <summary>Applies the shared font and colours to a form.</summary>
    public static void Style(Form form)
    {
        form.Font = Body;
        form.BackColor = Window;
        form.ForeColor = Text;
        form.AutoScaleMode = AutoScaleMode.Dpi;
    }

    public static Label Label(string text, int top = 0) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Text,
        Margin = new Padding(0, top + 5, Gap, 0)
    };

    /// <summary>Muted explanatory text under a control.</summary>
    public static Label Hint(string text, int maxWidth = 0) => new()
    {
        Text = text,
        AutoSize = true,
        Font = Small,
        ForeColor = Muted,
        MaximumSize = new Size(maxWidth, 0),
        Margin = new Padding(0, 2, 0, Gap)
    };

    /// <summary>
    /// Section title. Fixed single-line height rather than AutoSize: a docked AutoSize label wraps
    /// when its width is briefly constrained during layout and then paints over the control below.
    /// </summary>
    public static Label SectionHeading(string text, bool first = false) => new()
    {
        Text = text,
        AutoSize = false,
        AutoEllipsis = true,
        Dock = DockStyle.Top,
        Height = Heading.Height + 4,
        TextAlign = ContentAlignment.BottomLeft,
        Font = Heading,
        ForeColor = Text,
        Margin = new Padding(0, first ? 0 : Pad, 0, Gap)
    };

    public static CheckBox Check(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = Text,
        Margin = new Padding(0, 3, 0, 3)
    };

    public static TextBox TextBox(int width = 0)
    {
        var box = new TextBox { BorderStyle = BorderStyle.FixedSingle, Margin = new Padding(0, 3, 0, 3) };
        if (width > 0)
            box.Width = width;
        else
            box.Dock = DockStyle.Fill;
        return box;
    }

    public static NumericUpDown Number(int min, int max, int width = 110) => new()
    {
        Minimum = min,
        Maximum = max,
        Width = width,
        BorderStyle = BorderStyle.FixedSingle,
        Margin = new Padding(0, 3, 0, 3)
    };

    public static ComboBox Combo(int width = 160) => new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        FlatStyle = FlatStyle.Flat,
        Width = width,
        Margin = new Padding(0, 3, 0, 3)
    };

    /// <summary>A flat secondary button.</summary>
    public static Button Button(string text, int width = 0)
    {
        var b = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Card,
            ForeColor = Text,
            Height = 32,
            UseVisualStyleBackColor = false,
            Margin = new Padding(0, 3, Gap, 3),
            Cursor = Cursors.Hand
        };
        b.FlatAppearance.BorderColor = Border;
        b.FlatAppearance.BorderSize = 1;
        b.FlatAppearance.MouseOverBackColor = AccentSoft;
        b.FlatAppearance.MouseDownBackColor = Color.FromArgb(214, 226, 250);
        if (width > 0) b.Width = width; else { b.AutoSize = true; b.Padding = new Padding(10, 0, 10, 0); }
        return b;
    }

    /// <summary>The one filled call-to-action button in a window.</summary>
    public static Button PrimaryButton(string text, int width = 0)
    {
        var b = Button(text, width);
        b.BackColor = Accent;
        b.ForeColor = Color.White;
        b.Font = Strong;
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = AccentDark;
        b.FlatAppearance.MouseDownBackColor = AccentDark;
        return b;
    }

    /// <summary>A "danger" text button for destructive actions such as Remove.</summary>
    public static Button DangerButton(string text, int width = 0)
    {
        var b = Button(text, width);
        b.ForeColor = Red;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(253, 235, 235);
        return b;
    }

    /// <summary>A clean details list: no grid, flat headers, full-row select, shared font.</summary>
    public static ListView List()
    {
        var list = new ListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            MultiSelect = false,
            HideSelection = false,
            BorderStyle = BorderStyle.FixedSingle,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BackColor = Card,
            ForeColor = Text,
            Margin = new Padding(0)
        };
        // Enable the Explorer look (hover highlight, no dotted focus rectangle).
        list.HandleCreated += (s, e) => NativeTheme.Apply(list);
        return list;
    }

    /// <summary>Two-column label/control form layout with a fixed label gutter.</summary>
    public static TableLayoutPanel FormGrid(int labelWidth = 140)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 2,
            Margin = new Padding(0)
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, labelWidth));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return grid;
    }

    /// <summary>Adds a labelled row to a <see cref="FormGrid"/> and returns the row index.</summary>
    public static int Row(TableLayoutPanel grid, string label, Control control)
    {
        var row = grid.RowCount++;
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        if (!string.IsNullOrEmpty(label))
            grid.Controls.Add(Label(label), 0, row);
        grid.Controls.Add(control, 1, row);
        return row;
    }

    /// <summary>A vertical stack of controls.</summary>
    public static FlowLayoutPanel Stack() => new()
    {
        FlowDirection = FlowDirection.TopDown,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        WrapContents = false,
        Dock = DockStyle.Top,
        Margin = new Padding(0)
    };

    /// <summary>A horizontal row of controls.</summary>
    public static FlowLayoutPanel Inline() => new()
    {
        FlowDirection = FlowDirection.LeftToRight,
        AutoSize = true,
        WrapContents = false,
        Margin = new Padding(0)
    };

    /// <summary>A 1px horizontal rule.</summary>
    public static Control Rule() => new Panel { Height = 1, Dock = DockStyle.Top, BackColor = Border, Margin = new Padding(0) };

    /// <summary>Bottom action bar with buttons right-aligned.</summary>
    public static Panel ActionBar(params Button[] buttons)
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 60, BackColor = Card, Padding = new Padding(Pad, 0, Pad, 0) };
        bar.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Border });
        var flow = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, Padding = new Padding(0, 13, 0, 0) };
        foreach (var b in buttons)
        {
            b.Margin = new Padding(Gap, 0, 0, 0);
            flow.Controls.Add(b);
        }
        bar.Controls.Add(flow);
        flow.BringToFront();
        return bar;
    }

    /// <summary>Small filled circle used as a status/legend swatch.</summary>
    public static Control Dot(Color color, int size = 10)
    {
        var p = new Panel { Width = size, Height = size, Margin = new Padding(0, 6, 6, 0), BackColor = Color.Transparent };
        p.Paint += (s, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, 0, 0, size - 1, size - 1);
        };
        return p;
    }

    /// <summary>Loads the app icon as a bitmap at the requested size.</summary>
    public static Bitmap AppImage(int size)
    {
        using var stream = typeof(Ui).Assembly.GetManifestResourceStream("RoboMouse.App.Assets.controlling.ico")
            ?? throw new InvalidOperationException("Missing embedded app icon.");
        using var icon = new Icon(stream, size, size);
        return icon.ToBitmap();
    }

    public static Icon AppIcon()
    {
        using var stream = typeof(Ui).Assembly.GetManifestResourceStream("RoboMouse.App.Assets.controlling.ico")
            ?? throw new InvalidOperationException("Missing embedded app icon.");
        return new Icon(stream);
    }
}

/// <summary>Explorer-style theming for list views.</summary>
internal static class NativeTheme
{
    [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string pszSubAppName, string? pszSubIdList);

    public static void Apply(ListView list)
    {
        try { SetWindowTheme(list.Handle, "Explorer", null); } catch { }
    }
}
