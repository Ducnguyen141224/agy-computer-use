using System.Diagnostics;
using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private object WaitText(JsonElement args)
    {
        var timeout = args.Number("timeout_ms", 3000, 0, 10000);
        var clock = Stopwatch.StartNew();
        JsonElement found;
        var requested = args.TryGetProperty("count", out var count) ? count.GetInt32() : (int?)null;
        var present = args.Flag("present", true);
        do
        {
            var remaining = Math.Max(200, timeout - (int)clock.ElapsedMilliseconds);
            found = JsonSerializer.SerializeToElement(Observe(JsonSerializer.SerializeToElement(new
            {
                window_id = args.Text("window_id"), root_ref = args.Text("root_ref"), text = args.Text("text"),
                text_any = true, exact = args.Flag("exact"), interactive_only = false, limit = 80,
                depth = args.Number("depth", 12, 1, 50), budget_ms = Math.Min(args.Number("budget_ms", 1000, 200, 5000), remaining)
            }, Program.Json), true), Program.Json);
            var actual = found.GetProperty("items").GetArrayLength();
            var complete = found.GetProperty("complete").GetBoolean();
            var matched = requested.HasValue ? complete && actual == requested.Value : present ? actual > 0 : complete && actual == 0;
            if (matched) return new { verification = "passed", count = actual, complete, items = found.GetProperty("items"), elapsed_ms = clock.ElapsedMilliseconds };
            if (clock.ElapsedMilliseconds >= timeout) break;
            ControlPresentation.Delay(60);
        } while (true);
        return new { verification = "failed", count = found.GetProperty("items").GetArrayLength(), complete = found.GetProperty("complete"), items = found.GetProperty("items"), elapsed_ms = clock.ElapsedMilliseconds, reason = "Text condition was not proved within the bounded UIA scope. Partial reads cannot prove absence or an exact count." };
    }
}
