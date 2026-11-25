namespace DesktopFixture;

internal static class ReaderFixture
{
    internal static void Run(string title)
    {
        var app = new System.Windows.Application();
        var window = new System.Windows.Window { Title = title, Width = 650, Height = 500, ShowActivated = false };
        var panel = new System.Windows.Controls.StackPanel();
        for (var index = 0; index < 120; index++)
        {
            var line = new System.Windows.Controls.TextBlock { Text = index is 40 or 41 ? "Dòng lặp hợp lệ" : $"Dòng {index:000} — nội dung thử nghiệm 🙂", Height = 36 };
            panel.Children.Add(line);
        }
        var password = new System.Windows.Controls.PasswordBox { Password = "reader-secret-sentinel", Height = 36 };
        panel.Children.Add(password);
        var scroll = new System.Windows.Controls.ScrollViewer { Content = panel, VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto };
        System.Windows.Automation.AutomationProperties.SetName(scroll, "Reader log");
        window.Content = scroll; app.Run(window);
    }
}
