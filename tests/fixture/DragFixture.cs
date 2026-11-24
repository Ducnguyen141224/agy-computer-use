namespace DesktopFixture;

internal static class DragFixture
{
    internal static void Run(string title, bool source, string payload)
    {
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = title, Width = 420, Height = 300, StartPosition = FormStartPosition.Manual, Left = source ? 30 : 480, Top = 100 };
        var status = new TextBox { AccessibleName = "Drop status", ReadOnly = true, Text = "Waiting", Left = 20, Top = 210, Width = 350 };
        var counter = new TextBox { AccessibleName = "Drop count", ReadOnly = true, Text = "0", Left = 20, Top = 240, Width = 350 };
        var drops = 0;
        var pad = new Label { AccessibleName = source ? "Drag token" : "Drop target", Text = source ? "Drag this disposable text token" : "Drop the disposable token here", Left = 20, Top = 25, Width = 350, Height = 150, BackColor = source ? Color.CornflowerBlue : Color.LightGreen, AllowDrop = !source };
        Point start = default;
        pad.MouseDown += (_, args) => { if (args.Button == MouseButtons.Left) start = args.Location; };
        pad.MouseMove += (_, args) =>
        {
            if (source && args.Button == MouseButtons.Left && (Math.Abs(args.X - start.X) > 8 || Math.Abs(args.Y - start.Y) > 8)) pad.DoDragDrop(payload, DragDropEffects.Copy);
        };
        pad.DragEnter += (_, args) => { if (args.Data?.GetDataPresent(DataFormats.UnicodeText) == true) args.Effect = DragDropEffects.Copy; };
        pad.DragDrop += (_, args) => { status.Text = args.Data?.GetData(DataFormats.UnicodeText) as string ?? "Wrong data"; counter.Text = (++drops).ToString(); };
        form.Controls.AddRange([pad, status, counter]); Application.Run(form);
    }
}
