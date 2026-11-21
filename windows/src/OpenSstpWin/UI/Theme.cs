using Microsoft.Win32;

namespace OpenSstpWin.UI;

/// <summary>
/// Applies the light/dark/system theme selected in the menu to a whole form.
/// WinForms has no built-in dark mode, so controls are recoloured recursively.
/// </summary>
internal static class Theme
{
    private static readonly Color DarkBackground = Color.FromArgb(32, 32, 32);
    private static readonly Color DarkSurface = Color.FromArgb(45, 45, 48);
    private static readonly Color DarkButton = Color.FromArgb(62, 62, 66);
    private static readonly Color DarkBorder = Color.FromArgb(90, 90, 96);
    private static readonly Color DarkText = Color.FromArgb(240, 240, 240);
    private static readonly Color DarkMuted = Color.FromArgb(190, 190, 195);

    public static bool IsDark(string? theme) => AppThemes.Normalize(theme) switch
    {
        AppThemes.Dark => true,
        AppThemes.Light => false,
        _ => SystemIsDark(),
    };

    public static void Apply(Form form, string? theme)
    {
        var dark = IsDark(theme);
        form.BackColor = dark ? DarkBackground : SystemColors.Control;
        form.ForeColor = dark ? DarkText : SystemColors.ControlText;

        foreach (Control child in form.Controls)
        {
            ApplyControl(child, dark);
        }
    }

    private static void ApplyControl(Control control, bool dark)
    {
        var background = dark ? DarkBackground : SystemColors.Control;
        var surface = dark ? DarkSurface : SystemColors.Window;
        var text = dark ? DarkText : SystemColors.ControlText;

        switch (control)
        {
            case TextBoxBase or ListBox or ComboBox or NumericUpDown or ListView or TreeView or DataGridView:
                control.BackColor = surface;
                control.ForeColor = text;
                break;

            case Button button:
                button.BackColor = dark ? DarkButton : SystemColors.Control;
                button.ForeColor = text;
                button.FlatStyle = dark ? FlatStyle.Flat : FlatStyle.Standard;
                if (dark)
                {
                    button.FlatAppearance.BorderColor = DarkBorder;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(75, 75, 80);
                }

                break;

            case MenuStrip or StatusStrip or ToolStrip:
                control.BackColor = dark ? DarkSurface : SystemColors.Control;
                control.ForeColor = text;
                break;

            case TabControl tabControl:
                tabControl.BackColor = background;
                tabControl.ForeColor = text;
                break;

            case LinkLabel link:
                link.LinkColor = dark ? Color.FromArgb(120, 170, 255) : Color.Blue;
                link.BackColor = background;
                break;

            case TabPage or GroupBox or Panel or TableLayoutPanel or FlowLayoutPanel or SplitContainer or Label
                or CheckBox or RadioButton:
                control.BackColor = background;
                control.ForeColor = text;
                break;

            default:
                control.ForeColor = text;
                break;
        }

        if (control is ToolStrip strip)
        {
            strip.RenderMode = ToolStripRenderMode.System;
            foreach (ToolStripItem item in strip.Items)
            {
                ApplyItem(item, dark);
            }
        }

        foreach (Control child in control.Controls)
        {
            ApplyControl(child, dark);
        }
    }

    private static void ApplyItem(ToolStripItem item, bool dark)
    {
        item.BackColor = dark ? DarkSurface : SystemColors.Control;
        item.ForeColor = dark ? DarkText : SystemColors.ControlText;

        if (item is ToolStripStatusLabel)
        {
            item.ForeColor = dark ? DarkMuted : SystemColors.ControlText;
        }

        if (item is ToolStripMenuItem menu)
        {
            foreach (ToolStripItem child in menu.DropDownItems)
            {
                ApplyItem(child, dark);
            }
        }
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
}
