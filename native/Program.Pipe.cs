using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace AgyComputerUse;

internal static partial class Program
{
    private static void RunPipeClient(string[] args)
    {
        var index = Array.IndexOf(args, "--pipe-client");
        if (index < 0 || index + 1 >= args.Length) return;
        var name = args[index + 1];
        var sent = false;
        try
        {
            if (name.Length > 120 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) throw new ArgumentException();
            var line = Console.ReadLine() ?? throw new ArgumentException();
            if (line.Length > 131072) throw new ArgumentException();
            using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            pipe.Connect(500);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            sent = true;
            writer.WriteLine(line);
            var reply = reader.ReadLineAsync().WaitAsync(TimeSpan.FromMilliseconds(14000)).GetAwaiter().GetResult();
            if (reply == null || reply.Length > 48 * 1024 * 1024) throw new IOException();
            Console.WriteLine(reply);
        }
        catch (Exception error)
        {
            var code = error is UnauthorizedAccessException ? "PIPE_ACCESS_DENIED" : !sent && error is TimeoutException or IOException ? "PIPE_NOT_READY" : "PIPE_IO_UNKNOWN";
            Console.WriteLine(JsonSerializer.Serialize(new { id = 1, ok = false, error = new { code, message = sent ? "Connection interrupted; action delivery is unknown. Inspect current state." : "A same-user desktop daemon connection was not established.", delivery = sent ? "unknown" : "not_sent" } }, Json));
        }
    }
    private static void RunPipe(string[] args, DesktopService desktop)
    {
        var index = Array.IndexOf(args, "--pipe");
        if (index < 0 || index + 1 >= args.Length) return;
        var name = args[index + 1];
        if (name.Length > 120 || name.Any(character => !char.IsAsciiLetterOrDigit(character) && character != '-')) return;
        var idleIndex = Array.IndexOf(args, "--idle-seconds");
        var idle = idleIndex >= 0 && idleIndex + 1 < args.Length && int.TryParse(args[idleIndex + 1], out var seconds) ? Math.Clamp(seconds, 15, 900) : 300;
        while (true)
        {
            using var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(idle));
            try { pipe.WaitForConnectionAsync(deadline.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { return; }
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, true) { AutoFlush = true };
            try
            {
                var line = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                if (line == null) continue;
                using var request = JsonDocument.Parse(line);
                if (request.RootElement.TryGetProperty("method", out var method) && method.GetString() == "shutdown")
                {
                    writer.WriteLine(JsonSerializer.Serialize(new { id = request.RootElement.GetProperty("id"), ok = true, data = new { stopped = true } }, Json));
                    return;
                }
                using var watchdog = new System.Threading.Timer(_ => Environment.Exit(124), null, TimeSpan.FromSeconds(13), Timeout.InfiniteTimeSpan);
                writer.WriteLine(HandleLine(line, desktop));
            }
            catch (IOException) { }
            catch (TimeoutException) { }
            catch (JsonException) { }
        }
    }
}
