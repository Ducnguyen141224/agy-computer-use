namespace DesktopFixture;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--notifications")) { NotificationFixture.Run(args[0]); return; }
        if (args.Contains("--drag-source") || args.Contains("--drag-target")) { DragFixture.Run(args[0], args.Contains("--drag-source"), args[^1]); return; }
        if (args.Contains("--launch-metadata")) { LaunchFixture.Run(args[0]); return; }
        if (args.Contains("--wpf")) { WpfFixture.Run(args.FirstOrDefault() ?? "AGY WPF Fixture"); return; }
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = args.FirstOrDefault() ?? "AGY Computer Use Fixture", Width = 900, Height = 720, StartPosition = FormStartPosition.CenterScreen };
        if (args.Contains("--overlay"))
        {
            form.BackColor = Color.Magenta;
            var taps = new TextBox { AccessibleName = "Overlay taps", Text = "0", ReadOnly = true, Left = 20, Top = 20, Width = 100 };
            var total = 0; form.MouseDown += (_, _) => taps.Text = (++total).ToString();
            form.Controls.Add(taps); Application.Run(form); return;
        }
        var name = new Label { Text = "Message", Left = 20, Top = 20, AutoSize = true };
        var message = new TextBox { Name = "fixture-message", AccessibleName = "Message", AccessibleDescription = "Disposable fixture message", Left = 20, Top = 45, Width = 400, Text = "initial" };
        var option = new CheckBox { AccessibleName = "Enabled option", Text = "Enabled option", Left = 20, Top = 85, Width = 180 };
        var status = new TextBox { AccessibleName = "Status", Left = 20, Top = 125, Width = 400, ReadOnly = true, Text = "Ready" };
        var save = new Button { AccessibleName = "Save", Text = "Save", Left = 20, Top = 170, Width = 110 };
        var timer = new System.Windows.Forms.Timer { Interval = 150 };
        timer.Tick += (_, _) => { timer.Stop(); status.Text = "Saved: " + message.Text; };
        save.Click += (_, _) => { status.Text = "Saving"; timer.Start(); };
        var remove = new Button { AccessibleName = "Remove field", Text = "Remove field", Left = 150, Top = 170, Width = 120 };
        remove.Click += (_, _) => { form.Controls.Remove(message); message.Dispose(); status.Text = "Removed"; };
        var password = new TextBox { AccessibleName = "Secret", UseSystemPasswordChar = true, Text = "fixture-secret", Left = 20, Top = 215, Width = 180 };
        var groupA = new GroupBox { Text = "Group A", AccessibleName = "Group A", Left = 20, Top = 260, Width = 200, Height = 100 };
        var groupB = new GroupBox { Text = "Group B", AccessibleName = "Group B", Left = 250, Top = 260, Width = 200, Height = 100 };
        groupA.Controls.Add(new Button { Text = "Duplicate", AccessibleName = "Duplicate", Left = 15, Top = 30, Width = 130 });
        groupB.Controls.Add(new Button { Text = "Duplicate", AccessibleName = "Duplicate", Left = 15, Top = 30, Width = 130 });
        var reorder = new Button { AccessibleName = "Reorder", Text = "Reorder", Left = 300, Top = 170, Width = 110 };
        reorder.Click += (_, _) => { form.Controls.SetChildIndex(groupB, form.Controls.GetChildIndex(groupA)); };
        var gestures = new TextBox { AccessibleName = "Gesture log", Left = 460, Top = 380, Width = 360, Height = 70, Multiline = true, ReadOnly = true };
        var pad = new GesturePad { AccessibleName = "Gesture pad", Text = "Mouse and drag test", Left = 460, Top = 45, Width = 330, Height = 180, BackColor = Color.AliceBlue, TabStop = true };
        var drop = new Label { AccessibleName = "Drop zone", Text = "Drop zone", Left = 460, Top = 260, Width = 330, Height = 70, BackColor = Color.Honeydew, BorderStyle = BorderStyle.FixedSingle };
        void Log(string message) { gestures.AppendText(message + ";"); }
        pad.MouseDown += (_, e) => { pad.Focus(); pad.Capture = true; Log($"down:{e.Button}:clicks={e.Clicks}"); if (Control.ModifierKeys != Keys.None) Log("modifiers:" + Control.ModifierKeys); };
        pad.MouseUp += (_, e) => { pad.Capture = false; Log($"up:{e.Button}"); };
        pad.MouseMove += (_, e) => { if (e.Button == MouseButtons.Left && !gestures.Text.Contains("dragging")) Log("dragging"); };
        pad.MouseHover += (_, _) => Log("hover");
        pad.MouseWheel += (_, e) => Log($"wheel:{e.Delta}");
        var menu = new ContextMenuStrip();
        menu.Items.Add("Fixture menu action", null, (_, _) => Log("menu-action"));
        pad.ContextMenuStrip = menu;
        var reset = new Button { AccessibleName = "Reset gestures", Text = "Reset gestures", Left = 460, Top = 340, Width = 130 };
        reset.Click += (_, _) => gestures.Clear();
        var copyImage = new Button { AccessibleName = "Copy test image", Text = "Copy test image", Left = 610, Top = 340, Width = 150 };
        copyImage.Click += (_, _) => { using var image = new Bitmap(12, 12); using (var graphics = Graphics.FromImage(image)) graphics.Clear(Color.CornflowerBlue); Clipboard.SetImage(image); };
        var choices = new ListBox { AccessibleName = "Choices", Left = 20, Top = 400, Width = 390, Height = 150 };
        var combo = new ComboBox { AccessibleName = "Size", DropDownStyle = ComboBoxStyle.DropDownList, Left = 460, Top = 420, Width = 170 };
        combo.Items.AddRange(["Small", "Medium", "Large"]); combo.SelectedIndex = 0;
        for (var i = 1; i <= 25; i++) choices.Items.Add($"Choice {i:00}");
        var tree = new TreeView { AccessibleName = "Topics", Left = 460, Top = 470, Width = 330, Height = 140 };
        var parent = tree.Nodes.Add("Parent topic"); parent.Nodes.Add("Child topic");
        var openChild = new Button { AccessibleName = "Open child", Text = "Open child", Left = 20, Top = 580, Width = 130 };
        openChild.Click += (_, _) => { var child = new Form { Text = form.Text + " Child", Width = 300, Height = 180, StartPosition = FormStartPosition.CenterParent }; child.Controls.Add(new Label { Text = "Child opened", AccessibleName = "Child opened", AutoSize = true }); child.Show(form); };
        form.Controls.AddRange([name, message, option, status, save, remove, password, groupA, groupB, reorder, gestures, pad, drop, reset, copyImage, choices, combo, tree, openChild]);
        Application.Run(form);
        timer.Dispose();
    }
}

internal sealed class GesturePad : Control
{
    public GesturePad() { SetStyle(ControlStyles.StandardClick | ControlStyles.StandardDoubleClick | ControlStyles.Selectable, true); }
    protected override void OnPaint(PaintEventArgs e) { base.OnPaint(e); e.Graphics.DrawString(Text, Font, Brushes.Black, new PointF(12, 12)); }
}
