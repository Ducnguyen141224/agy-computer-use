using System.Runtime.InteropServices;
using System.Windows.Automation;
using UIA = Interop.UIAutomationClient;

namespace AgyComputerUse;

internal static class Automation
{
    private static readonly Dictionary<Delegate, object> handlers = new();
    internal static void AddAutomationEventHandler(AutomationEvent kind, AutomationElement root, TreeScope scope, AutomationEventHandler callback)
    {
        var handler = new EventHandler(callback);
        var cache = AutomationElement.Client.CreateCacheRequest();
        cache.TreeScope = UIA.TreeScope.TreeScope_Element;
        cache.AddProperty(30000); // RuntimeId remains available when the source closes.
        AutomationElement.Client.AddAutomationEventHandler(kind.Id, root.NativeElement, (UIA.TreeScope)scope, cache, handler);
        handlers.Add(callback, handler);
    }
    internal static void RemoveAutomationEventHandler(AutomationEvent kind, AutomationElement root, AutomationEventHandler callback)
    {
        if (handlers.Remove(callback, out var handler)) AutomationElement.Client.RemoveAutomationEventHandler(kind.Id, root.NativeElement, (UIA.IUIAutomationEventHandler)handler);
    }
    internal static void AddStructureChangedEventHandler(AutomationElement root, TreeScope scope, StructureChangedEventHandler callback)
    {
        var handler = new StructureHandler(callback);
        AutomationElement.Client.AddStructureChangedEventHandler(root.NativeElement, (UIA.TreeScope)scope, null, handler);
        handlers.Add(callback, handler);
    }
    internal static void RemoveStructureChangedEventHandler(AutomationElement root, StructureChangedEventHandler callback)
    {
        if (handlers.Remove(callback, out var handler)) AutomationElement.Client.RemoveStructureChangedEventHandler(root.NativeElement, (UIA.IUIAutomationStructureChangedEventHandler)handler);
    }
    internal static void AddAutomationFocusChangedEventHandler(AutomationFocusChangedEventHandler callback)
    {
        var handler = new FocusHandler(callback);
        AutomationElement.Client.AddFocusChangedEventHandler(null, handler);
        handlers.Add(callback, handler);
    }
    internal static void RemoveAutomationFocusChangedEventHandler(AutomationFocusChangedEventHandler callback)
    {
        if (handlers.Remove(callback, out var handler)) AutomationElement.Client.RemoveFocusChangedEventHandler((UIA.IUIAutomationFocusChangedEventHandler)handler);
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class EventHandler(AutomationEventHandler callback) : UIA.IUIAutomationEventHandler
    {
        public void HandleAutomationEvent(UIA.IUIAutomationElement sender, int eventId)
        {
            try
            {
                var kind = AutomationEvent.LookupById(eventId);
                AutomationEventArgs args = new(kind);
                if (eventId == WindowPattern.WindowClosedEvent.Id)
                {
                    int[]? ids = null;
                    try { ids = ((Array)sender.GetCachedPropertyValue(30000)).Cast<int>().ToArray(); } catch (COMException) { }
                    if (ids == null) { try { ids = sender.GetRuntimeId().Cast<int>().ToArray(); } catch (COMException) { return; } }
                    args = new WindowClosedEventArgs(ids);
                }
                callback(new AutomationElement(sender), args);
            }
            catch { /* Never let a removed provider unwind through a COM event callback. */ }
        }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class StructureHandler(StructureChangedEventHandler callback) : UIA.IUIAutomationStructureChangedEventHandler
    {
        public void HandleStructureChangedEvent(UIA.IUIAutomationElement sender, UIA.StructureChangeType kind, int[] ids)
        {
            try { callback(new AutomationElement(sender), new StructureChangedEventArgs((StructureChangeType)kind, ids)); } catch { }
        }
    }
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    private sealed class FocusHandler(AutomationFocusChangedEventHandler callback) : UIA.IUIAutomationFocusChangedEventHandler
    {
        public void HandleFocusChangedEvent(UIA.IUIAutomationElement sender)
        {
            try { callback(new AutomationElement(sender), new AutomationFocusChangedEventArgs(0, 0)); } catch { }
        }
    }
}
