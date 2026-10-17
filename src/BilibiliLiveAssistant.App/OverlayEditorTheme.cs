using System.Drawing;
using Forms = System.Windows.Forms;

namespace BilibiliLiveAssistant.App;

internal static class OverlayEditorTheme
{
    private static readonly Color Surface = Color.FromArgb(23, 26, 33), Field = Color.FromArgb(38, 44, 56), Text = Color.FromArgb(235, 240, 250);
    internal static void Apply(Forms.Form form)
    {
        form.AutoScaleMode = Forms.AutoScaleMode.Dpi; form.BackColor = Surface; form.ForeColor = Text;
        var font = new Font("Microsoft YaHei UI", 9, FontStyle.Regular, GraphicsUnit.Point);
        form.Font = font; form.Disposed += (_, _) => font.Dispose();
        void Style(Forms.Control control)
        {
            control.ForeColor = Text; control.BackColor = control is Forms.NumericUpDown or Forms.TextBox or Forms.ComboBox ? Field : Surface;
            if (control is Forms.Button button)
            {
                button.FlatStyle = Forms.FlatStyle.Flat; button.FlatAppearance.BorderColor = Color.FromArgb(72, 86, 110);
                button.BackColor = button.Text.StartsWith("保存") ? Color.FromArgb(46, 91, 155) : Field;
                button.Padding = new(6, 3, 6, 3); button.MinimumSize = new(70, 32);
            }
            if (control is Forms.NumericUpDown number) number.BorderStyle = Forms.BorderStyle.FixedSingle;
            if (control is Forms.CheckBox check) check.Padding = new(0, 3, 0, 3);
            foreach (Forms.Control child in control.Controls) Style(child);
        }
        foreach (Forms.Control child in form.Controls) Style(child);
    }
    internal static Forms.Form Details(string title, string content)
    {
        var form = new Forms.Form { Text = title, Width = 520, Height = 420, MinimumSize = new(340, 240),
            StartPosition = Forms.FormStartPosition.CenterParent, ShowInTaskbar = false, TopMost = true, Padding = new(16) };
        form.Controls.Add(new Forms.TextBox { Multiline = true, ReadOnly = true, Dock = Forms.DockStyle.Fill, ScrollBars = Forms.ScrollBars.Vertical,
            Text = content, WordWrap = true, BorderStyle = Forms.BorderStyle.None });
        Apply(form); return form;
    }
}
