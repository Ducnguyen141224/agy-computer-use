namespace DesktopFixture;

internal static class WpfFixture
{
    public static void Run(string title)
    {
        var app = new System.Windows.Application();
        var window = new System.Windows.Window { Title = title, Width = 750, Height = 600, WindowStartupLocation = System.Windows.WindowStartupLocation.CenterScreen };
        var panel = new System.Windows.Controls.StackPanel { Margin = new System.Windows.Thickness(20) };
        var choices = new System.Windows.Controls.ListBox { Height = 200, Width = 500, HorizontalAlignment = System.Windows.HorizontalAlignment.Left };
        System.Windows.Automation.AutomationProperties.SetName(choices, "WPF Choices");
        System.Windows.Controls.VirtualizingStackPanel.SetIsVirtualizing(choices, false);
        for (var index = 1; index <= 60; index++) choices.Items.Add(new System.Windows.Controls.ListBoxItem { Content = $"Item {index:00}", Height = 26 });
        panel.Children.Add(choices);
        var tree = new System.Windows.Controls.TreeView { Height = 110, Margin = new System.Windows.Thickness(0, 12, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(tree, "WPF Topics");
        var parent = new System.Windows.Controls.TreeViewItem { Header = "Parent" };
        parent.Items.Add(new System.Windows.Controls.TreeViewItem { Header = "Child" });
        tree.Items.Add(parent); panel.Children.Add(tree);
        var edit = new System.Windows.Controls.TextBox { Text = "Provider text", Height = 35, Margin = new System.Windows.Thickness(0, 12, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(edit, "WPF Message"); panel.Children.Add(edit);
        System.Windows.Automation.AutomationProperties.SetAutomationId(edit, "fixture-message");
        System.Windows.Automation.AutomationProperties.SetHelpText(edit, "Disposable fixture message");
        var check = new System.Windows.Controls.CheckBox { Content = "WPF Toggle", Margin = new System.Windows.Thickness(0, 12, 0, 0) };
        panel.Children.Add(check);
        var combo = new System.Windows.Controls.ComboBox { Width = 180, Height = 30, HorizontalAlignment = System.Windows.HorizontalAlignment.Left, Margin = new System.Windows.Thickness(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetName(combo, "WPF Size");
        foreach (var size in new[] { "Small", "Medium", "Large" }) combo.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = size });
        combo.SelectedIndex = 0; panel.Children.Add(combo);
        panel.Children.Add(new System.Windows.Controls.CheckBox { Content = "WPF Tri-state", IsThreeState = true, IsChecked = null, Margin = new System.Windows.Thickness(0, 8, 0, 0) });
        var slider = new System.Windows.Controls.Slider { Minimum = 0, Maximum = 100, Value = 25, Height = 20, Width = 200 };
        System.Windows.Automation.AutomationProperties.SetName(slider, "WPF Level"); panel.Children.Add(slider);
        window.Content = panel;
        app.Run(window);
    }
}
