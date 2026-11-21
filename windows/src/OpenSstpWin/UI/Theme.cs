using Microsoft.Win32;

namespace OpenSstpWin.UI;

/// <summary>
/// Applies the light/dark/system theme to a whole form. WinForms has no built-in
/// dark mode, so every control is recoloured recursively, tab headers are custom
/// drawn and menus get a renderer that keeps their text readable. Anything that
/// cannot be reached through the normal control tree (drop-down menus) is themed
/// through its owning item as well, so no label is ever drawn white-on-white.
/// </summary>
internal static class Theme
{
    private static readonly Color DarkBackground = Color.FromArgb(24, 26, 30);
    private static readonly Color DarkSurface = Color.FromArgb(34, 37, 43);
    private static readonly Color DarkSurfaceAlt = Color.FromArgb(43, 47, 55);
    private static readonly Color DarkBorder = Color.FromArgb(70, 76, 88);
    private static readonly Color DarkText = Color.FromArgb(238, 240, 245);
    private static readonly Color DarkMuted = Color.FromArgb(166, 173, 186);
    private static readonly Color Accent = Color.FromArgb(64, 132, 230);

    private static readonly Color LightBackground = Color.FromArgb(246, 247, 249);
    private static readonly Color LightSurface = Color.White;
    private static readonly Color LightBorder = Color.FromArgb(200, 204, 212);
    private static readonly Color LightText = Color.FromArgb(28, 31, 36);
    private static readonly Color LightMuted = Color.FromArgb(92, 99, 110);

    public static bool IsDark(string? theme) => AppThemes.Normalize(theme) switch
    {
        AppThemes.Dark => true,
        AppThemes.Light => false,
        _ => SystemIsDark(),
    };

    public static Color Background(bool dark) => dark ? DarkBackground : LightBackground;

    public static Color Surface(bool dark) => dark ? DarkSurface : LightSurface;

    public static Color TextColor(bool dark) => dark ? DarkText : LightText;

    public static Color Muted(bool dark) => dark ? DarkMuted : LightMuted;

    public static Color Border(bool dark) => dark ? DarkBorder : LightBorder;

    public static Color AccentColor => Accent;

    public static void Apply(Form form, string? theme)
    {
        var dark = IsDark(theme);
        form.BackColor = Background(dark);
        form.ForeColor = TextColor(dark);

        foreach (Control child in form.Controls)
        {
            ApplyControl(child, dark);
        }

        form.Invalidate(true);
    }

    private static void ApplyControl(Control control, bool dark)
    {
        var background = Background(dark);
        var surface = Surface(dark);
        var text = TextColor(dark);

        switch (control)
        {
            case ThemedCheckBox or ThemedRadioButton:
                control.BackColor = background;
                control.ForeColor = text;
                break;

            case ComboBox combo:
                combo.BackColor = surface;
                combo.ForeColor = text;
                combo.FlatStyle = FlatStyle.Flat;
                break;

            case TextBoxBase or ListBox or CheckedListBox or NumericUpDown or ListView or TreeView or DataGridView:
                control.BackColor = surface;
                control.ForeColor = text;
                break;

            case DateTimePicker picker:
                picker.CalendarMonthBackground = surface;
                picker.CalendarForeColor = text;
                picker.CalendarTitleBackColor = dark ? DarkSurfaceAlt : SystemColors.ActiveCaption;
                picker.CalendarTitleForeColor = dark ? DarkText : SystemColors.ActiveCaptionText;
                break;

            case Button button when string.Equals(button.Tag as string, "primary", StringComparison.Ordinal):
                button.BackColor = Accent;
                button.ForeColor = Color.White;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 0;
                button.FlatAppearance.MouseOverBackColor = Color.FromArgb(86, 152, 240);
                button.FlatAppearance.MouseDownBackColor = Color.FromArgb(46, 108, 196);
                button.UseVisualStyleBackColor = false;
                break;

            case Button button:
                button.BackColor = dark ? DarkSurfaceAlt : SystemColors.Control;
                button.ForeColor = text;
                button.FlatStyle = FlatStyle.Flat;
                button.FlatAppearance.BorderSize = 1;
                button.FlatAppearance.BorderColor = Border(dark);
                button.FlatAppearance.MouseOverBackColor = dark
                    ? Color.FromArgb(58, 63, 73)
                    : Color.FromArgb(229, 232, 237);
                button.UseVisualStyleBackColor = false;
                break;

            case MenuStrip or StatusStrip or ToolStrip:
                control.BackColor = surface;
                control.ForeColor = text;
                break;

            case TabControl tabControl:
                tabControl.BackColor = background;
                tabControl.ForeColor = text;
                tabControl.DrawMode = TabDrawMode.OwnerDrawFixed;
                tabControl.SizeMode = TabSizeMode.Fixed;
                tabControl.ItemSize = new Size(132, 30);
                tabControl.DrawItem -= DrawTab;
                tabControl.DrawItem += DrawTab;
                break;

            case TabPage page:
                page.UseVisualStyleBackColor = false;
                page.BackColor = surface;
                page.ForeColor = text;
                break;

            case GroupBox:
                control.BackColor = background;
                control.ForeColor = dark ? DarkMuted : LightMuted;
                break;

            case LinkLabel link:
                link.LinkColor = dark ? Color.FromArgb(120, 175, 255) : Color.FromArgb(0, 84, 180);
                link.ActiveLinkColor = Color.FromArgb(220, 120, 60);
                link.VisitedLinkColor = link.LinkColor;
                link.BackColor = background;
                link.ForeColor = text;
                break;

            case Label label when string.Equals(label.Tag as string, "muted", StringComparison.Ordinal):
                label.BackColor = background;
                label.ForeColor = Muted(dark);
                break;

            case Label label when string.Equals(label.Tag as string, "value", StringComparison.Ordinal):
                label.BackColor = background;
                label.ForeColor = text;
                break;

            case Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer or Label:
                control.BackColor = background;
                control.ForeColor = text;
                break;

            case ProgressBar:
                break;

            default:
                control.BackColor = background;
                control.ForeColor = text;
                break;
        }

        if (control is ToolStrip strip)
        {
            ThemeToolStrip(strip, dark);
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, dark);
        }
    }

    private static void ThemeToolStrip(ToolStrip strip, bool dark)
    {
        strip.RenderMode = ToolStripRenderMode.Professional;
        strip.Renderer = new ThemedRenderer(dark);
        strip.BackColor = dark ? DarkSurface : SystemColors.Control;
        strip.ForeColor = dark ? DarkText : SystemColors.ControlText;

        foreach (ToolStripItem item in strip.Items)
        {
            ApplyItem(item, dark);
        }
    }

    private static void ApplyItem(ToolStripItem item, bool dark)
    {
        var surface = dark ? DarkSurface : SystemColors.Control;
        item.BackColor = surface;
        item.ForeColor = dark ? DarkText : SystemColors.ControlText;

        if (item is ToolStripStatusLabel)
        {
            item.ForeColor = dark ? DarkMuted : LightMuted;
        }

        if (item is ToolStripDropDownItem dropDown)
        {
            dropDown.DropDown.BackColor = dark ? DarkSurfaceAlt : LightSurface;
            dropDown.DropDown.ForeColor = dark ? DarkText : LightText;
            dropDown.DropDown.RenderMode = ToolStripRenderMode.Professional;
            dropDown.DropDown.Renderer = new ThemedRenderer(dark);
            foreach (ToolStripItem child in dropDown.DropDownItems)
            {
                ApplyItem(child, dark);
            }
        }
    }

    private static void DrawTab(object? sender, DrawItemEventArgs e)
    {
        if (sender is not TabControl tabs || e.Index < 0 || e.Index >= tabs.TabPages.Count)
        {
            return;
        }

        var dark = tabs.BackColor.GetBrightness() < 0.5f;
        var selected = e.Index == tabs.SelectedIndex;
        var page = tabs.TabPages[e.Index];
        var back = selected
            ? page.BackColor
            : (dark ? DarkSurface : Color.FromArgb(228, 231, 236));
        var fore = selected
            ? page.ForeColor
            : (dark ? DarkMuted : LightMuted);

        using (var brush = new SolidBrush(back))
        {
            e.Graphics.FillRectangle(brush, e.Bounds);
        }

        if (selected)
        {
            using var accent = new SolidBrush(Accent);
            e.Graphics.FillRectangle(accent, e.Bounds.Left, e.Bounds.Bottom - 3, e.Bounds.Width, 3);
        }

        if (e.State.HasFlag(DrawItemState.Focus))
        {
            ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, fore, back);
        }

        TextRenderer.DrawText(
            e.Graphics,
            page.Text,
            tabs.Font,
            e.Bounds,
            fore,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }

    private static bool SystemIsDark()
    {
        try
        {
            var value = Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "AppsUseLightTheme",
                1);

            return value is int light && light == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// ToolStrip renderer with explicit colours. The stock system renderer ignores
    /// ForeColor for many elements, which is exactly how menu text became invisible.
    /// </summary>
    private sealed class ThemedRenderer : ToolStripProfessionalRenderer
    {
        private readonly bool _dark;

        public ThemedRenderer(bool dark) : base(new ThemedColorTable(dark))
        {
            _dark = dark;
            RoundedEdges = false;
        }

        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.ToolStrip.BackColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(Border(_dark));
            var rect = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
            e.Graphics.DrawRectangle(pen, rect);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected && !e.Item.Pressed)
            {
                var stripBackground = e.ToolStrip?.BackColor ?? (_dark ? DarkSurface : SystemColors.Control);
                using var clear = new SolidBrush(stripBackground);
                e.Graphics.FillRectangle(clear, new Rectangle(Point.Empty, e.Item.Size));
                return;
            }

            var back = _dark ? Color.FromArgb(56, 62, 73) : Color.FromArgb(222, 229, 240);
            using var brush = new SolidBrush(back);
            e.Graphics.FillRectangle(brush, new Rectangle(Point.Empty, e.Item.Size));
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            if (e.Item.Enabled)
            {
                e.TextColor = e.Item.ForeColor;
            }
            else
            {
                e.TextColor = _dark ? Color.FromArgb(120, 126, 138) : Color.FromArgb(145, 150, 160);
            }

            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = _dark ? DarkText : LightText;
            base.OnRenderArrow(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Border(_dark));
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 6, y, e.Item.Width - 6, y);
        }

        protected override void OnRenderImageMargin(ToolStripRenderEventArgs e)
        {
            using var brush = new SolidBrush(e.ToolStrip.BackColor);
            e.Graphics.FillRectangle(brush, e.AffectedBounds);
        }
    }

    private sealed class ThemedColorTable : ProfessionalColorTable
    {
        private readonly bool _dark;

        public ThemedColorTable(bool dark) => _dark = dark;

        private Color Back => _dark ? DarkSurface : SystemColors.Control;

        private Color SurfaceColor => _dark ? DarkSurfaceAlt : LightSurface;

        public override Color ToolStripDropDownBackground => SurfaceColor;

        public override Color ImageMarginGradientBegin => SurfaceColor;

        public override Color ImageMarginGradientMiddle => SurfaceColor;

        public override Color ImageMarginGradientEnd => SurfaceColor;

        public override Color MenuStripGradientBegin => Back;

        public override Color MenuStripGradientEnd => Back;

        public override Color ToolStripGradientBegin => Back;

        public override Color ToolStripGradientMiddle => Back;

        public override Color ToolStripGradientEnd => Back;

        public override Color MenuItemSelected => _dark
            ? Color.FromArgb(56, 62, 73)
            : Color.FromArgb(222, 229, 240);

        public override Color MenuItemSelectedGradientBegin => MenuItemSelected;

        public override Color MenuItemSelectedGradientEnd => MenuItemSelected;

        public override Color MenuItemPressedGradientBegin => MenuItemSelected;

        public override Color MenuItemPressedGradientEnd => MenuItemSelected;

        public override Color MenuItemBorder => Accent;

        public override Color MenuBorder => Border(_dark);

        public override Color SeparatorDark => Border(_dark);

        public override Color SeparatorLight => _dark ? DarkSurface : SystemColors.ControlLightLight;

        public override Color StatusStripGradientBegin => Back;

        public override Color StatusStripGradientEnd => Back;

        public override Color ButtonSelectedHighlight => MenuItemSelected;

        public override Color ButtonSelectedBorder => Border(_dark);

        public override Color ButtonPressedHighlight => MenuItemSelected;
    }
}

/// <summary>Check box drawn by hand so the glyph stays visible on dark backgrounds.</summary>
internal sealed class ThemedCheckBox : CheckBox
{
    public ThemedCheckBox()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.Clear(BackColor);

        const int boxSize = 15;
        var top = Math.Max(0, (Height - boxSize) / 2);
        var box = new Rectangle(1, top, boxSize, boxSize);
        var border = Enabled
            ? ControlPaint.Dark(ForeColor, 0.25f)
            : ControlPaint.Light(ForeColor, 0.5f);

        using (var fill = new SolidBrush(BackColor))
        {
            graphics.FillRectangle(fill, box);
        }

        using (var pen = new Pen(border))
        {
            graphics.DrawRectangle(pen, box);
        }

        if (Checked || CheckState == CheckState.Indeterminate)
        {
            using var mark = new Pen(Enabled ? Theme.AccentColor : border, 2f);
            if (CheckState == CheckState.Indeterminate)
            {
                graphics.DrawLine(mark, box.Left + 4, box.Top + 7, box.Right - 4, box.Top + 7);
            }
            else
            {
                graphics.DrawLines(mark, new[]
                {
                    new Point(box.Left + 4, box.Top + 8),
                    new Point(box.Left + 7, box.Top + 11),
                    new Point(box.Right - 3, box.Top + 4),
                });
            }
        }

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, ClientRectangle, ForeColor, BackColor);
        }

        var textRect = new Rectangle(box.Right + 6, 0, Math.Max(0, Width - box.Right - 6), Height);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textRect,
            Enabled ? ForeColor : ControlPaint.Light(ForeColor, 0.5f),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }

    protected override void OnEnabledChanged(EventArgs e)
    {
        base.OnEnabledChanged(e);
        Invalidate();
    }
}

/// <summary>Radio button counterpart of <see cref="ThemedCheckBox"/>.</summary>
internal sealed class ThemedRadioButton : RadioButton
{
    public ThemedRadioButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.Clear(BackColor);

        const int size = 15;
        var top = Math.Max(0, (Height - size) / 2);
        var circle = new Rectangle(1, top, size, size);
        var border = Enabled ? ControlPaint.Dark(ForeColor, 0.25f) : ControlPaint.Light(ForeColor, 0.5f);

        using (var pen = new Pen(border))
        {
            graphics.DrawEllipse(pen, circle);
        }

        if (Checked)
        {
            using var mark = new SolidBrush(Enabled ? Theme.AccentColor : border);
            graphics.FillEllipse(mark, circle.Left + 4, circle.Top + 4, circle.Width - 8, circle.Height - 8);
        }

        var textRect = new Rectangle(circle.Right + 6, 0, Math.Max(0, Width - circle.Right - 6), Height);
        TextRenderer.DrawText(
            graphics,
            Text,
            Font,
            textRect,
            Enabled ? ForeColor : ControlPaint.Light(ForeColor, 0.5f),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis |
            TextFormatFlags.NoPrefix);
    }

    protected override void OnCheckedChanged(EventArgs e)
    {
        base.OnCheckedChanged(e);
        Invalidate();
    }
}
