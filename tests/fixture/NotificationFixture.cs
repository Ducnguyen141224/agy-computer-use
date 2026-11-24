using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;

namespace DesktopFixture;

internal static class NotificationFixture
{
    internal static void Run(string name)
    {
        var app = new System.Windows.Application();
        var window = new Window { Title = name, Width = 420, Height = 420, ShowActivated = false, ShowInTaskbar = false };
        var panel = new StackPanel();
        var list = new ListBox(); AutomationProperties.SetAutomationId(list, "MainListView");
        System.Windows.Controls.VirtualizingStackPanel.SetIsVirtualizing(list, false);
        var card = new ListBoxItem();
        var contents = new StackPanel();
        TextBlock Text(string id, string text) { var element = new TextBlock { Text = text }; AutomationProperties.SetAutomationId(element, id); contents.Children.Add(element); return element; }
        var title = Text("Title", "Disposable notification");
        Text("Content", "Disposable body"); Text("TimeStamp", "Today");
        var settings = new Button { Content = "Notification settings" }; AutomationProperties.SetAutomationId(settings, "SettingsButton"); contents.Children.Add(settings);
        var dismiss = new Button { Content = "Dismiss test card" }; AutomationProperties.SetAutomationId(dismiss, "DismissButton");
        dismiss.Click += (_, _) => list.Items.Remove(card); contents.Children.Add(dismiss);
        var action = new Button { Content = "Test action" }; action.Click += (_, _) => title.Text = "Action delivered"; contents.Children.Add(action);
        card.Content = contents; list.Items.Add(card);
        list.Items.Add(new ListBoxItem { Content = "Calendar item; not a notification" });
        var appTitle = new TextBlock { Text = "AGY Test App" }; AutomationProperties.SetAutomationId(appTitle, "Title");
        panel.Children.Add(new System.Windows.Controls.GroupBox { Header = appTitle, Content = list });
        var clear = new Button { Content = "Clear disposable cards" }; AutomationProperties.SetAutomationId(clear, "ClearAllButton"); clear.Click += (_, _) => list.Items.Clear(); panel.Children.Add(clear);
        window.Content = panel;
        app.Run(window);
    }
}
