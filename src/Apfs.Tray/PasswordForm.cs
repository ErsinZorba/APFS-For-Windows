using Apfs.Tray.Ui;

namespace Apfs.Tray;

/// <summary>Şifreli birim için parola penceresi. Parola yalnızca hizmete (yerel adlandırılmış kanal) gönderilir, saklanmaz.</summary>
internal sealed class PasswordForm : Form
{
    private readonly TextBox _password;
    private readonly Label _error;

    public PasswordForm(string volumeName, string? hint, string? error)
    {
        Text = "APFS for Windows: şifreli birim";
        Icon = Theme.AppIcon(32);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = Theme.Background;
        Font = Theme.Body;
        int width = this.S(340);

        Label Line(string t, Font f, Color c, int top = 0) => new()
        {
            Text = t, Font = f, ForeColor = c, AutoSize = true, MaximumSize = new Size(width, 0), UseMnemonic = false,
            Margin = new Padding(0, this.S(top), 0, 0),
        };

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false,
            Padding = new Padding(this.S(20), this.S(16), this.S(20), this.S(14)),
        };
        layout.Controls.Add(Line($"\"{volumeName}\" kilitli", Theme.Font(12f, FontStyle.Bold), Theme.Text));
        layout.Controls.Add(Line("Bu birim şifreli. Açmak için Mac'te kullandığınız parolayı girin.", Theme.Body, Theme.Muted, 4));
        if (!string.IsNullOrEmpty(hint))
            layout.Controls.Add(Line($"İpucu: {hint}", Theme.Body, Theme.Muted, 6));
        _password = new TextBox { UseSystemPasswordChar = true, Width = width, Font = Theme.Font(11f), Margin = new Padding(0, this.S(12), 0, 0) };
        layout.Controls.Add(_password);
        _error = Line(error ?? "", Theme.Body, Theme.Red, 6);
        _error.Visible = error is not null;
        layout.Controls.Add(_error);
        layout.Controls.Add(Line("Disk yalnızca okunur; parola hiçbir yere kaydedilmez.", Theme.Small, Theme.Muted, 10));

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Width = width, Margin = new Padding(0, this.S(12), 0, 0),
        };
        buttons.MinimumSize = new Size(width, 0);
        var ok = new FriendlyButton("Kilidi aç") { DialogResult = DialogResult.OK };
        var cancel = new FriendlyButton("İptal", FriendlyButton.Kind.Secondary) { DialogResult = DialogResult.Cancel };
        ok.Click += (_, _) => { DialogResult = DialogResult.OK; Close(); };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        buttons.Controls.Add(ok);
        buttons.Controls.Add(cancel);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
        Shown += (_, _) => { Activate(); _password.Focus(); };
    }

    public string Password => _password.Text;
}
