namespace DesktopFixture;

internal static class LaunchFixture
{
    internal static void Run(string title)
    {
        Console.WriteLine("Disposable fixture stdout; must never enter the MCP transport.");
        Console.Error.WriteLine("Disposable fixture stderr; must not be logged by the plugin.");
        ApplicationConfiguration.Initialize();
        var form = new Form { Text = title, Width = 520, Height = 220 };
        form.Controls.Add(new TextBox { AccessibleName = "Launch environment", ReadOnly = true, Text = Environment.GetEnvironmentVariable("AGY_FIXTURE_VALUE") ?? "", Left = 20, Top = 20, Width = 450 });
        form.Controls.Add(new TextBox { AccessibleName = "Launch directory", ReadOnly = true, Text = Environment.CurrentDirectory, Left = 20, Top = 70, Width = 450 });
        Application.Run(form);
    }
}
