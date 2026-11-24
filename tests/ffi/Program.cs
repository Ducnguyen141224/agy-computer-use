using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init(uint version);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Create([MarshalAs(UnmanagedType.LPWStr)] string helper, [MarshalAs(UnmanagedType.LPWStr)] string pipe, out IntPtr client);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Call(IntPtr client, [MarshalAs(UnmanagedType.LPUTF8Str)] string method, [MarshalAs(UnmanagedType.LPUTF8Str)] string parameters, out IntPtr response);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Free(IntPtr response);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Destroy(IntPtr client);
    private static int checks;
    private static void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
    private static void Main(string[] args)
    {
        var root = Path.GetFullPath(args.FirstOrDefault() ?? ".");
        var helper = Path.Combine(root, "native", "bin", "Release", "net10.0-windows", "AgyComputerUse.exe");
        var fixturePath = Path.Combine(root, "tests", "fixture", "bin", "Release", "net10.0-windows", "DesktopFixture.exe");
        var library = NativeLibrary.Load(Path.Combine(root, "ffi", "bin", "agy_desktop.dll"));
        var init = Marshal.GetDelegateForFunctionPointer<Init>(NativeLibrary.GetExport(library, "agy_init"));
        var create = Marshal.GetDelegateForFunctionPointer<Create>(NativeLibrary.GetExport(library, "agy_client_create"));
        var call = Marshal.GetDelegateForFunctionPointer<Call>(NativeLibrary.GetExport(library, "agy_call"));
        var free = Marshal.GetDelegateForFunctionPointer<Free>(NativeLibrary.GetExport(library, "agy_free"));
        var destroy = Marshal.GetDelegateForFunctionPointer<Destroy>(NativeLibrary.GetExport(library, "agy_client_destroy"));
        var pipe = "agy-ffi-test-" + Guid.NewGuid().ToString("N");
        IntPtr client = IntPtr.Zero;
        Process? fixture = null;
        JsonElement Request(string method, object parameters)
        {
            var status = call(client, method, JsonSerializer.Serialize(parameters, new JsonSerializerOptions { WriteIndented = true }), out var response);
            if (status != 0) throw new Exception("C ABI transport returned status " + status);
            try
            {
                using var document = JsonDocument.Parse(Marshal.PtrToStringUTF8(response)!);
                var reply = document.RootElement;
                if (!reply.GetProperty("ok").GetBoolean()) throw new Exception(reply.GetProperty("error").GetRawText());
                return reply.GetProperty("data").Clone();
            }
            finally { free(response); }
        }
        try
        {
            Check(init(999) == 1, "C ABI rejects an incompatible ABI version");
            Check(create(helper, pipe, out client) == 1 && client == IntPtr.Zero, "client creation requires successful ABI initialization");
            Check(init(1) == 0 && create(helper, pipe, out client) == 0 && client != IntPtr.Zero, "creates a native C ABI client");
            Check(call(client, "bad\"method", "{}", out var invalid) == 1 && invalid == IntPtr.Zero, "C ABI rejects invalid method syntax without dispatch");
            Check(call(client, "system", "{invalid-json}", out invalid) == 1 && invalid == IntPtr.Zero, "C ABI validates malformed JSON before connecting");
            var status = Request("system", new { operation = "status" });
            using var engine = Process.GetProcessById(status.GetProperty("process_id").GetInt32());
            _ = engine.Handle;
            Check(status.GetProperty("platform").GetString() == "windows", "C ABI launches and calls the same Windows engine");
            var title = "AGY FFI " + Guid.NewGuid().ToString("N")[..10];
            var start = new ProcessStartInfo(fixturePath) { UseShellExecute = false };
            start.ArgumentList.Add(title);
            fixture = Process.Start(start)!;
            var opened = Request("query", new { operation = "window_wait", query = title, timeout_ms = 5000 });
            var windowId = opened.GetProperty("windows").EnumerateArray().Single(window => window.GetProperty("pid").GetInt32() == fixture.Id).GetProperty("id").GetString()!;
            var found = Request("find", new { window_id = windowId, name = "Message", role = "edit" });
            var reference = found.GetProperty("items")[0].GetProperty("ref").GetString()!;
            var changed = Request("act", new { action = "set_value", @ref = reference, text = "C ABI tiếng Việt", expect = new { property = "value", equals = "C ABI tiếng Việt" } });
            Check(changed.GetProperty("verification").GetString() == "passed", "C ABI drives and verifies a real Unicode UI edit");
            var badBatch = JsonSerializer.Serialize(new { commands = new object[] {
                new { method = "act", @params = new { action = "set_value", @ref = reference, text = "must-not-write" } },
                new { method = "act", @params = new { action = "press_key", @ref = reference, key = "NotAKey" } }
            } });
            Check(call(client, "batch", badBatch, out var failedBatch) == 0, "C ABI receives native batch validation result");
            try { using var error = JsonDocument.Parse(Marshal.PtrToStringUTF8(failedBatch)!); Check(!error.RootElement.GetProperty("ok").GetBoolean(), "native batch preflight rejects an invalid later key"); }
            finally { free(failedBatch); }
            var read = Request("query", new { operation = "get", @ref = reference, property = "value" });
            Check(read.GetProperty("value").GetString() == "C ABI tiếng Việt", "C ABI preserves refs across calls and parses pretty JSON");
            var capture = Request("capture", new { window_id = windowId, max_dimension = 300 });
            var png = Convert.FromBase64String(capture.GetProperty("image").GetString()!);
            Check(png.Length > 8 && png[1] == 'P' && png[2] == 'N' && png[3] == 'G', "C ABI returns a real in-memory window capture");
            var reads = Enumerable.Range(0, 4).Select(_ => Task.Run(() => Request("query", new { operation = "get", @ref = reference, property = "value" }).GetProperty("value").GetString())).ToArray();
            Task.WaitAll(reads);
            Check(reads.All(task => task.Result == "C ABI tiếng Việt"), "calls on one C client serialize without mixing responses");
            Check(Request("shutdown", new { }).GetProperty("stopped").GetBoolean(), "C ABI acknowledges daemon shutdown");
            Check(engine.WaitForExit(3000), "daemon process exits and releases its executable after shutdown");
            Console.WriteLine($"C ABI desktop smoke passed: {checks} checks. No evidence files saved.");
        }
        finally
        {
            if (client != IntPtr.Zero) { try { _ = Request("shutdown", new { }); } catch { } destroy(client); }
            if (fixture != null && !fixture.HasExited) fixture.Kill();
            fixture?.Dispose(); NativeLibrary.Free(library);
        }
    }
}
