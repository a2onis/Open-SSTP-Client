namespace OpenSstpWin.UI;

/// <summary>Small factory helpers that keep the dialogs readable.</summary>
internal static class Ui
{
    public static Label Label(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 8, 8, 3),
    };

    public static TextBox Text(string value = "") => new()
    {
        Text = value,
        Dock = DockStyle.Fill,
    };

    public static NumericUpDown Number(int minimum, int maximum, int value) => new()
    {
        Minimum = minimum,
        Maximum = maximum,
        Value = Math.Clamp(value, minimum, maximum),
        Dock = DockStyle.Fill,
        Width = 120,
    };

    public static CheckBox Check(string text, bool value) => new()
    {
        Text = text,
        Checked = value,
        AutoSize = true,
        Margin = new Padding(3, 6, 3, 3),
    };

    public static Button Button(string text, EventHandler handler)
    {
        var button = new Button { Text = text, AutoSize = true, Margin = new Padding(4) };
        button.Click += handler;
        return button;
    }

    public static FlowLayoutPanel ButtonRow(params Control[] controls)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Padding = new Padding(0, 4, 0, 0),
        };
        row.Controls.AddRange(controls);
        return row;
    }

    /// <summary>A two-column table: labels in column 0, editors in column 1.</summary>
    public static TableLayoutPanel Form()
    {
        var table = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            Dock = DockStyle.Top,
            Padding = new Padding(12),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        return table;
    }

    public static void Row(TableLayoutPanel table, string label, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        table.Controls.Add(Label(label), 0, row);
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 1, row);
    }

    public static void Wide(TableLayoutPanel table, Control control)
    {
        var row = table.RowCount;
        table.RowCount = row + 1;
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Fill;
        table.Controls.Add(control, 0, row);
        table.SetColumnSpan(control, 2);
    }

    public static TabPage Tab(string title, Control content)
    {
        var page = new TabPage(title) { AutoScroll = true, Padding = new Padding(4) };
        content.Dock = DockStyle.Top;
        page.Controls.Add(content);
        return page;
    }

    public static string[] SplitList(string? text) => (text ?? string.Empty).Split(
        new[] { ',', ';', ' ', '\t', '\r', '\n' },
        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
