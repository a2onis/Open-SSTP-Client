namespace OpenSstpWin.UI;

/// <summary>A tiny single-line input dialog (WinForms has no InputBox in .NET).</summary>
internal sealed class PromptForm : Form
{
    private readonly TextBox _input;

    private PromptForm(string title, string label, string initial, string theme)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(500, 130);

        _input = Ui.Text(initial);
        _input.SelectAll();

        var layout = Ui.Form();
        layout.Dock = DockStyle.Fill;
        Ui.Row(layout, label, _input);

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(12, 4, 12, 12),
        };

        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);

        Controls.Add(layout);
        Controls.Add(buttons);
        AcceptButton = ok;
        CancelButton = cancel;
        ActiveControl = _input;

        Theme.Apply(this, theme);
    }

    public static string? Show(IWin32Window owner, string title, string label, string initial, string theme)
    {
        using var dialog = new PromptForm(title, label, initial, theme);
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog._input.Text.Trim() : null;
    }
}
