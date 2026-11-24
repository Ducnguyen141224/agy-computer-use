using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Interop.UIAutomationClient;

internal static class Program
{
    [MTAThread]
    private static void Main()
    {
        IUIAutomation automation = new CUIAutomation8();
        var root = automation.GetRootElement();
        var walker = automation.ControlViewWalker;
        var result = new List<object>();
        var child = walker.GetFirstChildElement(root);
        for (var index = 0; child != null && index < 60; index++)
        {
            var current = child;
            try
            {
                using var process = Process.GetProcessById(current.CurrentProcessId);
                var shell = new[] { "explorer", "StartMenuExperienceHost", "SearchHost", "ShellExperienceHost", "ShellHost" }.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase);
                var count = 0;
                var managedCount = 0;
                var ids = new HashSet<string>();
                if (shell)
                {
                    var queue = new Queue<IUIAutomationElement>(); queue.Enqueue(current);
                    while (queue.Count > 0 && count < 150)
                    {
                        var element = queue.Dequeue(); count++;
                        if (!string.IsNullOrEmpty(element.CurrentAutomationId)) ids.Add(element.CurrentAutomationId);
                        var node = walker.GetFirstChildElement(element);
                        while (node != null && count + queue.Count < 150) { queue.Enqueue(node); node = walker.GetNextSiblingElement(node); }
                    }
                    if (current.CurrentNativeWindowHandle != IntPtr.Zero)
                    {
                        var managed = new Queue<System.Windows.Automation.AutomationElement>();
                        managed.Enqueue(System.Windows.Automation.AutomationElement.FromHandle(current.CurrentNativeWindowHandle));
                        var managedWalker = System.Windows.Automation.TreeWalker.ControlViewWalker;
                        while (managed.Count > 0 && managedCount < 150)
                        {
                            var item = managed.Dequeue(); managedCount++;
                            var next = managedWalker.GetFirstChild(item);
                            while (next != null && managedCount + managed.Count < 150) { managed.Enqueue(next); next = managedWalker.GetNextSibling(next); }
                        }
                    }
                }
                result.Add(new { app = process.ProcessName, class_name = current.CurrentClassName, offscreen = current.CurrentIsOffscreen, hwnd = current.CurrentNativeWindowHandle.ToInt64(), shell_nodes = count, managed_nodes = managedCount, ids = shell ? ids.Take(25).ToArray() : null });
                child = walker.GetNextSiblingElement(current);
            }
            catch (COMException) { break; }
        }
        var focus = automation.GetFocusedElement();
        using var focusedProcess = Process.GetProcessById(focus.CurrentProcessId);
        var ancestors = new List<object>();
        for (var element = focus; element != null && ancestors.Count < 20; element = walker.GetParentElement(element)) ancestors.Add(new { pid = element.CurrentProcessId, id = element.CurrentAutomationId, class_name = element.CurrentClassName, hwnd = element.CurrentNativeWindowHandle.ToInt64() });
        Console.WriteLine(JsonSerializer.Serialize(new { backend = "CUIAutomation8", focused = new { app = focusedProcess.ProcessName, id = focus.CurrentAutomationId, class_name = focus.CurrentClassName, role = focus.CurrentControlType }, ancestors, windows = result }));
    }
}
