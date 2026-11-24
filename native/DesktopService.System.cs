using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private readonly Dictionary<string, IDataObject> clipboardSnapshots = new();
    private object SystemOperation(JsonElement args)
    {
        var operation = args.Text("operation") ?? "status";
        if (operation.StartsWith("clipboard_", StringComparison.Ordinal) && !StaDispatcher.IsCurrent) return StaDispatcher.Run(() => SystemOperation(args));
        switch (operation)
        {
            case "status":
            case "permissions":
            case "version":
                var taskbar = Native.FindWindow("Shell_TrayWnd", null);
                Native.GetWindowThreadProcessId(taskbar, out var taskbarPid);
                Native.GetCursorPos(out var logical); Native.GetPhysicalCursorPos(out var physical); Native.GetClipCursor(out var clip);
                var foreground = Native.GetForegroundWindow(); Native.GetWindowThreadProcessId(foreground, out var foregroundPid);
                var foregroundClass = new StringBuilder(256); Native.GetClassName(foreground, foregroundClass, foregroundClass.Capacity);
                return new { platform = "windows", version = "0.4.0", os_build = Environment.OSVersion.Version.ToString(), interactive_desktop = Native.InteractiveDesktop(), elevated = IsElevated(), integrity = Native.Integrity(Environment.ProcessId), memory_only = true, process_id = Environment.ProcessId, coordinate_system = "physical_pixels", taskbar = new { exists = taskbar != IntPtr.Zero, visible = taskbar != IntPtr.Zero && Native.IsWindowVisible(taskbar), cloaked = taskbar != IntPtr.Zero && Native.IsCloaked(taskbar), integrity = Native.Integrity((int)taskbarPid) }, held_input = "not_supported; every gesture releases its keys/buttons", window_capture = WindowCapture.Supported, screen_capture = true, accessibility = "UIA3 (CUIAutomation8)", session_id = currentSession, diagnostic = args.Flag("diagnostic") ? new { dpi_awareness = Native.GetAwarenessFromDpiAwarenessContext(Native.GetThreadDpiAwarenessContext()), logical_cursor = new { x = logical.X, y = logical.Y }, physical_cursor = new { x = physical.X, y = physical.Y }, clip = new { x = clip.Left, y = clip.Top, width = clip.Width, height = clip.Height }, foreground_app = ProcessName((int)foregroundPid), foreground_class = foregroundClass.ToString(), helper_token = Native.TokenFlags(Environment.ProcessId), shell_token = Native.TokenFlags((int)taskbarPid), job_ui_limits = Native.JobUiLimits() } : null };
            case "cursor_config": case "cursor_status": case "cursor_remove": return CursorOperation(args, operation);
            case "list_displays":
                return new { displays = Screen.AllScreens.Select(screen => new { id = screen.DeviceName, primary = screen.Primary, bounds = RectangleData(screen.Bounds), work_area = RectangleData(screen.WorkingArea) }).ToArray() };
            case "list_apps":
                _ = ListWindows(JsonSerializer.SerializeToElement(new { limit = 60 }));
                return new { apps = windows.Values.Where(entry => ValidWindow(entry) && Native.IsWindowVisible(entry.Handle) && !Native.IsCloaked(entry.Handle) && entry.Handle != CursorOverlay.WindowHandle).GroupBy(entry => entry.Pid).Select(group => new { pid = group.Key, app = ProcessName(group.Key), windows = group.Select(entry => entry.Id).ToArray() }).ToArray(), scope = "running apps with visible top-level windows" };
            case "launch": return Launch(args);
            case "clipboard_get": return ClipboardGet(args);
            case "clipboard_set": return ClipboardSet(args);
            case "clipboard_clear": return new { delivery = "sent", empty = true, sequence = Native.WriteClipboardText("", StaDispatcher.ClipboardOwner) };
            case "clipboard_save": return ClipboardSave();
            case "clipboard_restore": return ClipboardRestore(args);
            case "list_surfaces": return ListSurfaces(args);
            case "open_surface": return OpenSurface(args);
            case "close_surface":
                var surfaceWindow = GetWindow(args.Text("window_id"));
                if (args.Text("ref") is string surfaceRef)
                {
                    var scoped = Resolve(surfaceRef);
                    if (scoped.Window.Id != surfaceWindow.Id || EmbeddedSurfaceKind(scoped.Element) == null) throw new DesktopError("INVALID_ARGUMENT", "Ref must identify a visible menu, sheet, dialog or alert in the selected window.");
                }
                else if (SurfaceKind(surfaceWindow.Handle, surfaceWindow.Pid) == null) throw new DesktopError("INVALID_ARGUMENT", "This window is not an observed system/menu/dialog surface. In-window surfaces need their root_ref as ref.");
                if (!IsActiveSurface(surfaceWindow) && !Native.ActivateWindow(surfaceWindow.Handle)) throw new DesktopError("WINDOW_NOT_FOREGROUND", "Selected surface is not active in the foreground input context.");
                Native.KeyChord("Escape");
                return new { delivery = "sent", verification = "not_requested" };
            case "list_notifications": return Notifications(args, "list");
            case "dismiss_notification": return Notifications(args, "dismiss");
            case "dismiss_all_notifications": return Notifications(args, "dismiss_all");
            case "notification_action": return Notifications(args, "action");
        }
        var window = GetWindow(args.Text("window_id"));
        switch (operation)
        {
            case "window_info": return WindowInfo(window);
            case "focus_window":
                if (!Native.ActivateWindow(window.Handle)) throw new DesktopError("FOCUS_DENIED", "Windows did not grant foreground focus.");
                break;
            case "move":
            case "resize":
                if (!Native.GetWindowRect(window.Handle, out var rect)) throw new DesktopError("WINDOW_NOT_FOUND", "Window geometry is unavailable.");
                var x = operation == "move" ? args.Number("x", rect.Left, -32768, 32767) : rect.Left;
                var y = operation == "move" ? args.Number("y", rect.Top, -32768, 32767) : rect.Top;
                var width = operation == "resize" ? args.Number("width", rect.Width, 100, 8192) : rect.Width;
                var height = operation == "resize" ? args.Number("height", rect.Height, 80, 8192) : rect.Height;
                if (operation == "move" && !Screen.AllScreens.Any(screen => screen.WorkingArea.IntersectsWith(new Rectangle(x, y, width, height)))) throw new DesktopError("INVALID_ARGUMENT", "The moved window must intersect a display work area.");
                if (!Native.SetWindowPos(window.Handle, IntPtr.Zero, x, y, width, height, 0x0004 | 0x0010)) throw new DesktopError("WINDOW_OPERATION_FAILED", "Windows rejected the geometry change.");
                break;
            case "minimize": Native.ShowWindow(window.Handle, 6); break;
            case "maximize": Native.ShowWindow(window.Handle, 3); break;
            case "restore": Native.ShowWindow(window.Handle, 9); break;
            case "close_window":
            case "close_app":
                var protectedNames = new[] { "explorer", "sihost", "csrss", "winlogon", "dwm", "lsass", "services", "svchost", "startmenuexperiencehost", "shellexperiencehost", "shellhost", "searchhost" };
                if (protectedNames.Contains(ProcessName(window.Pid).ToLowerInvariant()) || window.Pid == Environment.ProcessId) throw new DesktopError("PROTECTED_PROCESS", "This system process cannot be closed by the plugin.");
                if (operation == "close_app" && args.Flag("force"))
                {
                    Native.RequireInputPrivilege(window.Pid);
                    using var closing = Process.GetProcessById(window.Pid); _ = closing.Handle;
                    if (closing.StartTime.ToUniversalTime().Ticks != window.Started) throw new DesktopError("WINDOW_CHANGED", "Process generation changed before force close. No termination was sent.");
                    closing.Kill(false); // The explicit selected process only; never terminate unrelated descendants.
                    var exited = closing.WaitForExit(1500);
                    return new { delivery = "sent", verification = exited ? "passed" : "failed", pid = window.Pid, forced = true, scope = "selected process generation only" };
                }
                if (operation == "close_app") _ = ListWindows(JsonSerializer.SerializeToElement(new { limit = 60 }));
                var targets = operation == "close_app" ? windows.Values.Where(entry => entry.Pid == window.Pid && ValidWindow(entry)).ToArray() : [window];
                foreach (var target in targets) GetWindow(target.Id);
                foreach (var target in targets)
                {
                    GetWindow(target.Id);
                    if (!Native.PostMessage(target.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero)) throw new DesktopError("WINDOW_OPERATION_FAILED", "Close request could not be posted.");
                }
                return new { delivery = "sent", verification = "not_requested", note = "A polite close request was posted. Unsaved-document dialogs may keep the app open; inspect before proceeding." };
            default: throw new DesktopError("INVALID_ARGUMENT", "Unknown system operation.");
        }
        Thread.Sleep(50);
        return new { delivery = "sent", window = WindowInfo(window) };
    }
    private static bool IsElevated()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
    private static object RectangleData(Rectangle rect) => new { x = rect.X, y = rect.Y, width = rect.Width, height = rect.Height };
    private static string ProcessName(int pid) { try { using var process = Process.GetProcessById(pid); return process.ProcessName; } catch { return "unavailable"; } }
    private static object WindowInfo(WindowEntry window)
    {
        var title = new StringBuilder(1024);
        Native.GetWindowText(window.Handle, title, title.Capacity);
        if (!Native.GetWindowRect(window.Handle, out var rect)) throw new DesktopError("WINDOW_NOT_FOUND", "Window disappeared while reading its geometry.");
        return new { id = window.Id, pid = window.Pid, title = Trim(title.ToString(), 200), bounds = new { x = rect.Left, y = rect.Top, width = rect.Width, height = rect.Height }, minimized = Native.IsIconic(window.Handle), maximized = Native.IsZoomed(window.Handle), foreground = Native.GetForegroundWindow() == window.Handle };
    }
    private object Launch(JsonElement args)
    {
        var app = args.Text("app", 2048) ?? throw new DesktopError("INVALID_ARGUMENT", "launch needs an executable name or absolute .exe path.");
        if (!app.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) app += ".exe";
        string? executable;
        if (Path.IsPathFullyQualified(app)) executable = File.Exists(app) ? app : null;
        else if (app.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0) throw new DesktopError("INVALID_ARGUMENT", "Use a full executable path, not a relative path.");
        else executable = new[] { Environment.SystemDirectory, Environment.GetFolderPath(Environment.SpecialFolder.Windows) }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)).Where(path => !string.IsNullOrWhiteSpace(path)).Select(path => Path.Combine(path.Trim('"'), app)).FirstOrDefault(File.Exists);
        if (executable == null) throw new DesktopError("APP_NOT_FOUND", "The executable was not found. Use an absolute .exe path.");
        if (args.Flag("attach"))
        {
            _ = ListWindows(JsonSerializer.SerializeToElement(new { query = Path.GetFileNameWithoutExtension(executable), limit = 60 }));
            var running = windows.Values.Where(entry => ValidWindow(entry) && ExecutablePath(entry.Pid)?.Equals(Path.GetFullPath(executable), StringComparison.OrdinalIgnoreCase) == true && Native.IsWindowVisible(entry.Handle) && !Native.IsCloaked(entry.Handle)).ToArray();
            if (running.Length > 1) throw new DesktopError("AMBIGUOUS_TARGET", "Several matching windows are already running. Choose a returned window_id explicitly.");
            if (running.Length == 1)
            {
                if (args.Flag("activate") && !Native.ActivateWindow(running[0].Handle)) throw new DesktopError("FOCUS_DENIED", "The existing app did not receive foreground focus.");
                return new { delivery = "already_satisfied", pid = running[0].Pid, attached = true, window = WindowInfo(running[0]) };
            }
        }
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = false };
        if (args.Text("cwd", 2048) is string directory) start.WorkingDirectory = directory;
        if (args.TryGetProperty("env", out var environment)) foreach (var variable in environment.EnumerateObject()) start.Environment[variable.Name] = variable.Value.GetString()!;
        if (args.TryGetProperty("args", out var arguments))
        {
            if (arguments.ValueKind != JsonValueKind.Array || arguments.GetArrayLength() > 30) throw new DesktopError("INVALID_ARGUMENT", "args must be an array with at most 30 strings.");
            foreach (var argument in arguments.EnumerateArray())
            {
                if (argument.ValueKind != JsonValueKind.String || argument.GetString()!.Length > 2048) throw new DesktopError("INVALID_ARGUMENT", "Invalid app argument.");
                start.ArgumentList.Add(argument.GetString()!);
            }
        }
        var debugPort = args.Flag("cdp") ? ChooseCdpPort(args.Number("cdp_port", 0, 0, 65535)) : (int?)null;
        if (debugPort != null)
        {
            var profile = args.Text("profile_dir", 2048)!;
            if (Directory.Exists(profile) && Directory.EnumerateFileSystemEntries(profile).Any()) throw new DesktopError("INVALID_ARGUMENT", "The debug profile directory is no longer empty. No process was launched.");
            start.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
            start.ArgumentList.Add("--remote-debugging-port=" + debugPort);
            start.ArgumentList.Add("--user-data-dir=" + args.Text("profile_dir", 2048));
        }
        using var process = Native.LaunchApplication(start);
        if (debugPort != null) return VerifyCdp(process, debugPort.Value, args);
        if (args.Flag("activate"))
        {
            var timer = Stopwatch.StartNew();
            do
            {
                _ = ListWindows(JsonSerializer.SerializeToElement(new { query = Path.GetFileNameWithoutExtension(executable), limit = 60 }));
                var candidate = windows.Values.FirstOrDefault(entry => entry.Pid == process.Id && ValidWindow(entry) && Native.IsWindowVisible(entry.Handle));
                if (candidate != null && Native.ActivateWindow(candidate.Handle)) return new { delivery = "sent", pid = process.Id, verification = "passed", window = WindowInfo(candidate) };
                if (process.HasExited) break;
                Thread.Sleep(60);
            } while (timer.ElapsedMilliseconds < args.Number("timeout_ms", 6000, 100, 8000));
            return new { delivery = "sent", pid = process.Id, verification = "failed", reason = "A foreground window of the launched process was not observed. Inspect before launching again." };
        }
        return new { delivery = "sent", pid = process.Id, verification = "not_requested", note = "Use desktop_windows or wait for a matching window. A launcher process can exit before its application opens." };
    }
    private static string? ExecutablePath(int pid) { try { using var process = Process.GetProcessById(pid); return process.MainModule?.FileName; } catch { return null; } }
    private bool ValidWindow(WindowEntry entry) { try { _ = GetWindow(entry.Id); return true; } catch (DesktopError) { return false; } }

    private static object ClipboardGet(JsonElement args)
    {
        var limit = args.Number("limit", 1000, 1, 12000);
        var format = args.Text("format") ?? (args.Flag("image") ? "image" : "text");
        if (format == "files") return Native.ReadClipboardFiles();
        if (format == "auto")
        {
            var files = JsonSerializer.SerializeToElement(Native.ReadClipboardFiles(), Program.Json);
            if (files.GetProperty("file_paths").GetArrayLength() > 0 || !files.GetProperty("complete").GetBoolean()) return files;
            var metadata = JsonSerializer.SerializeToElement(Native.ReadClipboardText(limit), Program.Json);
            if (!metadata.GetProperty("has_image").GetBoolean()) return metadata;
        }
        if (format is "image" or "auto")
        {
            System.Windows.Media.Imaging.BitmapSource? image = null;
            for (var attempt = 0; attempt < 5 && image == null; attempt++)
            {
                try { image = System.Windows.Clipboard.GetImage(); }
                catch (System.Runtime.InteropServices.ExternalException) when (attempt < 4) { }
                if (image == null) Thread.Sleep(60);
            }
            if (image == null) throw new DesktopError("CLIPBOARD_FORMAT", "No readable clipboard image was available. The producer may still be updating it.");
            var scale = Math.Min(1.0, 1200.0 / Math.Max(image.PixelWidth, image.PixelHeight));
            var resized = scale < 1 ? new System.Windows.Media.Imaging.TransformedBitmap(image, new System.Windows.Media.ScaleTransform(scale, scale)) : image;
            using var stream = new MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(resized));
            encoder.Save(stream);
            return new { image = Convert.ToBase64String(stream.ToArray()), width = resized.PixelWidth, height = resized.PixelHeight, source = "clipboard" };
        }
        return Native.ReadClipboardText(limit);
    }
    private static object ClipboardSet(JsonElement args)
    {
        if (args.TryGetProperty("file_paths", out var files))
        {
            var paths = files.EnumerateArray().Select(item => item.GetString()!).ToArray();
            foreach (var filePath in paths) if (filePath.Contains('\0') || !Path.IsPathFullyQualified(filePath) || !File.Exists(filePath) && !Directory.Exists(filePath)) throw new DesktopError("INVALID_ARGUMENT", "Every file reference must be an existing absolute file or directory. Clipboard was not changed.");
            var sequence = Native.WriteClipboardFiles(paths, StaDispatcher.ClipboardOwner);
            return new { delivery = "sent", verification = "passed", format = "files", count = paths.Length, sequence, verification_scope = "locked file-reference readback; no file copied or moved" };
        }
        var text = args.Text("text", 60000);
        var path = args.Text("image_path", 2048);
        if ((text == null) == (path == null)) throw new DesktopError("INVALID_ARGUMENT", "Specify exactly one of text or image_path.");
        if (text != null)
        {
            var sequence = Native.WriteClipboardText(text, StaDispatcher.ClipboardOwner);
            return new { delivery = "sent", verification = "passed", characters = text.Length, sequence, verification_scope = "locked clipboard text readback" };
        }
        if (!Path.IsPathFullyQualified(path!) || !File.Exists(path)) throw new DesktopError("INVALID_ARGUMENT", "image_path must be an existing absolute image file.");
        if (new FileInfo(path!).Length > 20 * 1024 * 1024) throw new DesktopError("INVALID_ARGUMENT", "Image file is too large.");
        using var image = Image.FromFile(path!);
        if ((long)image.Width * image.Height > 16000000) throw new DesktopError("INVALID_ARGUMENT", "Image dimensions exceed the clipboard budget.");
        Clipboard.SetImage(image);
        return new { delivery = "sent", verification = Clipboard.ContainsImage() ? "passed" : "failed", width = image.Width, height = image.Height };
    }
    private object ClipboardSave()
    {
        if (clipboardSnapshots.Count >= 3) throw new DesktopError("CACHE_LIMIT", "Restore an earlier clipboard snapshot before saving another.");
        var original = Clipboard.GetDataObject();
        var snapshot = new DataObject();
        if (original != null)
            foreach (var format in original.GetFormats(false))
            {
                var value = original.GetData(format, false);
                if (value is Image image) value = new Bitmap(image);
                else if (value is MemoryStream stream) value = new MemoryStream(stream.ToArray());
                else if (value is byte[] bytes) value = bytes.ToArray();
                if (value != null) snapshot.SetData(format, false, value);
            }
        var token = $"clip-{epoch}-{++sequence}";
        clipboardSnapshots[token] = snapshot;
        return new { token, memory_only = true };
    }
    private object ClipboardRestore(JsonElement args)
    {
        var token = args.Text("token") ?? "";
        if (!clipboardSnapshots.TryGetValue(token, out var snapshot)) throw new DesktopError("STALE_TOKEN", "Clipboard snapshot is unavailable in this session.");
        if (snapshot.GetFormats(false).Length == 0) Clipboard.Clear(); else Clipboard.SetDataObject(snapshot, true);
        clipboardSnapshots.Remove(token);
        return new { delivery = "sent", restored = true };
    }
}
