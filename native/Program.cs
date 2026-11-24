using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgyComputerUse;

internal static partial class Program
{
    private static bool stdioEvents;
    internal static void ControlStopped() { if (stdioEvents) Console.WriteLine("{\"event\":\"control_stopped\"}"); }
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [MTAThread]
    private static void Main(string[] args)
    {
        Console.InputEncoding = System.Text.Encoding.UTF8;
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
        if (args.Contains("--pipe-client")) { RunPipeClient(args); return; }
        using var desktop = new DesktopService();
        if (args.Contains("--daemon")) { RunPipe(args, desktop); return; }
        stdioEvents = true;
        string? line;
        while ((line = Console.ReadLine()) != null)
        {
            Console.WriteLine(HandleLine(line, desktop));
        }
    }
    private static string HandleLine(string line, DesktopService desktop)
    {
            object? id = null;
            string? method = null;
            object reply;
            try
            {
                if (line.Length > 131072) throw new DesktopError("INVALID_ARGUMENT", "Request is too large.");
                using var document = JsonDocument.Parse(line);
                var request = document.RootElement;
                id = request.GetProperty("id").Clone();
                method = request.GetProperty("method").GetString() ?? "";
                var args = request.GetProperty("params");
                reply = new { id, ok = true, data = desktop.Dispatch(method, args) };
            }
            catch (DesktopError ex)
            {
                reply = new { id, ok = false, error = new { code = ex.Code, message = ex.Message, details = ex.Details, retry = method == "act" ? "observe_first" : "correct_request" } };
            }
            catch (System.Windows.Automation.ElementNotAvailableException)
            {
                reply = new { id, ok = false, error = new { code = "STALE_REF", message = "The control is no longer available. Observe again; do not repeat an action without checking its effect.", delivery = Mutating(method) ? "unknown" : "not_sent", retry = "observe_first" } };
            }
            catch (Exception ex)
            {
                var invalid = ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException;
                reply = new { id, ok = false, error = new { code = invalid ? "INVALID_ARGUMENT" : "DESKTOP_ERROR", message = invalid ? "Invalid request or unavailable control. Observe again." : "Windows could not complete the request. Observe before retrying.", category = ex.GetType().Name, hresult = ex.HResult.ToString("X8"), delivery = Mutating(method) ? "unknown" : "not_sent", retry = "observe_first" } };
            }
            return JsonSerializer.Serialize(reply, Json);
    }
    internal static bool Mutating(string? method) => method is "act" or "system" or "batch";
}

internal sealed class DesktopError(string code, string message, object? details = null) : Exception(message)
{
    public string Code { get; } = code;
    public object? Details { get; } = details;
}

internal static class Arguments
{
    public static string? Text(this JsonElement args, string key, int limit = 500)
    {
        if (!args.TryGetProperty(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new DesktopError("INVALID_ARGUMENT", $"{key} must be a string.");
        var text = value.GetString()!;
        if (text.Length > limit) throw new DesktopError("INVALID_ARGUMENT", $"{key} exceeds its length limit.");
        return text;
    }
    public static int Number(this JsonElement args, string key, int fallback, int min, int max)
    {
        if (!args.TryGetProperty(key, out var value)) return fallback;
        if (!value.TryGetInt32(out var number) || number < min || number > max)
            throw new DesktopError("INVALID_ARGUMENT", $"{key} is out of range.");
        return number;
    }
    public static bool Flag(this JsonElement args, string key, bool fallback = false)
    {
        if (!args.TryGetProperty(key, out var value)) return fallback;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new DesktopError("INVALID_ARGUMENT", $"{key} must be boolean.");
        return value.GetBoolean();
    }
}
