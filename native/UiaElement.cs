using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using UIA = Interop.UIAutomationClient;
using Point = System.Windows.Point;
using Condition = System.Windows.Automation.Condition;

namespace AgyComputerUse;

// The managed UIA client binds generic HWND proxies for some modern shell windows.
// Keep one native UIA3 connection on the helper's MTA; retain live COM elements in refs.
internal sealed class AutomationElement
{
    [ThreadStatic] private static UIA.IUIAutomation? client;
    internal static UIA.IUIAutomation Client => client ??= new UIA.CUIAutomation8();
    // UIA2's identifier registry is lazy. LookupById alone can return null until a
    // managed provider happens to initialize the corresponding control constant.
    private static readonly Dictionary<int, ControlType> ControlTypes = typeof(ControlType)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(field => field.FieldType == typeof(ControlType)).Select(field => (ControlType)field.GetValue(null)!)
        .Concat(typeof(ControlType).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(ControlType)).Select(property => (ControlType)property.GetValue(null)!))
        .DistinctBy(type => type.Id).ToDictionary(type => type.Id);
    internal readonly UIA.IUIAutomationElement NativeElement;
    internal AutomationElement(UIA.IUIAutomationElement element) { NativeElement = element; }
    internal static T Call<T>(Func<T> operation)
    {
        try { return operation(); }
        catch (COMException error) when (error.HResult == unchecked((int)0x80040201)) { throw new ElementNotAvailableException(); }
    }
    internal static void Call(Action operation) { Call(() => { operation(); return true; }); }
    internal static AutomationElement? Wrap(UIA.IUIAutomationElement? value) => value == null ? null : new(value);
    internal static AutomationElement RootElement => Call(() => new AutomationElement(Client.GetRootElement()));
    internal static AutomationElement? FocusedElement => Call(() => Wrap(Client.GetFocusedElement()));
    internal static AutomationElement FromHandle(IntPtr window) => Call(() => new AutomationElement(Client.ElementFromHandle(window)));
    internal static AutomationElement FromPoint(Point point) => Call(() => new AutomationElement(Client.ElementFromPoint(new UIA.tagPOINT { x = (int)point.X, y = (int)point.Y })));
    internal Information Current => new(NativeElement);
    internal int[] GetRuntimeId() => Call(() => NativeElement.GetRuntimeId().Cast<int>().ToArray());
    internal void SetFocus() => Call(NativeElement.SetFocus);
    internal static AutomationProperty AutomationIdProperty => System.Windows.Automation.AutomationElement.AutomationIdProperty;
    internal static AutomationProperty ProcessIdProperty => System.Windows.Automation.AutomationElement.ProcessIdProperty;
    internal static AutomationProperty IsOffscreenProperty => System.Windows.Automation.AutomationElement.IsOffscreenProperty;
    internal static AutomationProperty ClassNameProperty => System.Windows.Automation.AutomationElement.ClassNameProperty;
    internal static AutomationProperty NameProperty => System.Windows.Automation.AutomationElement.NameProperty;
    internal static UIA.IUIAutomationCondition ConvertCondition(Condition condition) => condition switch
    {
        PropertyCondition property => Client.CreatePropertyCondition(property.Property.Id, property.Value),
        AndCondition all => all.GetConditions().Select(ConvertCondition).Aggregate((first, second) => Client.CreateAndCondition(first, second)),
        _ when ReferenceEquals(condition, Condition.TrueCondition) => Client.CreateTrueCondition(),
        _ => throw new ArgumentException("Unsupported automation condition.")
    };
    internal AutomationElement? FindFirst(TreeScope scope, Condition condition) => Call(() => Wrap(NativeElement.FindFirst((UIA.TreeScope)scope, ConvertCondition(condition))));
    internal ElementCollection FindAll(TreeScope scope, Condition condition) => Call(() => new ElementCollection(NativeElement.FindAll((UIA.TreeScope)scope, ConvertCondition(condition))));
    internal bool TryGetCurrentPattern(AutomationPattern pattern, out object result)
    {
        object? raw;
        try { raw = Call(() => NativeElement.GetCurrentPattern(pattern.Id)); }
        catch (COMException error) when (error.HResult == unchecked((int)0x80040204) || error.HResult == unchecked((int)0x80004002)) { result = null!; return false; }
        result = raw == null ? null! : pattern.Id switch
        {
            10000 => new InvokePattern((UIA.IUIAutomationInvokePattern)raw),
            10001 => new SelectionPattern((UIA.IUIAutomationSelectionPattern)raw),
            10002 => new ValuePattern((UIA.IUIAutomationValuePattern)raw),
            10003 => new RangeValuePattern((UIA.IUIAutomationRangeValuePattern)raw),
            10004 => new ScrollPattern((UIA.IUIAutomationScrollPattern)raw),
            10005 => new ExpandCollapsePattern((UIA.IUIAutomationExpandCollapsePattern)raw),
            10009 => new WindowPattern((UIA.IUIAutomationWindowPattern)raw),
            10010 => new SelectionItemPattern((UIA.IUIAutomationSelectionItemPattern)raw),
            10014 => new TextPattern((UIA.IUIAutomationTextPattern)raw),
            10015 => new TogglePattern((UIA.IUIAutomationTogglePattern)raw),
            10017 => new ScrollItemPattern((UIA.IUIAutomationScrollItemPattern)raw),
            _ => raw
        };
        return result != null;
    }
    internal sealed record PatternName(string ProgrammaticName);
    internal PatternName[] GetSupportedPatterns()
    {
        var result = new List<PatternName>();
        var names = new[] { "Invoke", "Selection", "Value", "RangeValue", "Scroll", "ExpandCollapse", "Grid", "GridItem", "MultipleView", "Window", "SelectionItem", "Dock", "Table", "TableItem", "Text", "Toggle", "Transform", "ScrollItem", "LegacyIAccessible" };
        for (var index = 0; index < names.Length; index++)
        {
            try { if (Call(() => NativeElement.GetCurrentPattern(10000 + index)) != null) result.Add(new(names[index])); }
            catch (COMException error) when (error.HResult == unchecked((int)0x80040204) || error.HResult == unchecked((int)0x80004002)) { }
        }
        return result.ToArray();
    }
    internal sealed class Information(UIA.IUIAutomationElement element)
    {
        internal string Name => Call(() => element.CurrentName ?? "");
        internal string AutomationId => Call(() => element.CurrentAutomationId ?? "");
        internal string ClassName => Call(() => element.CurrentClassName ?? "");
        internal string HelpText => Call(() => element.CurrentHelpText ?? "");
        internal int ProcessId => Call(() => element.CurrentProcessId);
        internal int NativeWindowHandle => Call(() => unchecked((int)element.CurrentNativeWindowHandle.ToInt64()));
        internal ControlType ControlType => Call(() => ControlTypes.GetValueOrDefault(element.CurrentControlType, ControlType.Custom));
        internal bool IsEnabled => Call(() => element.CurrentIsEnabled != 0);
        internal bool IsOffscreen => Call(() => element.CurrentIsOffscreen != 0);
        internal bool IsPassword => Call(() => element.CurrentIsPassword != 0);
        internal Rect BoundingRectangle => Call(() => { var bounds = element.CurrentBoundingRectangle; return new Rect(bounds.left, bounds.top, Math.Max(0, bounds.right - bounds.left), Math.Max(0, bounds.bottom - bounds.top)); });
    }
}

internal sealed class ElementCollection(UIA.IUIAutomationElementArray elements) : IEnumerable<AutomationElement>
{
    internal int Count => elements.Length;
    internal AutomationElement this[int index] => new(elements.GetElement(index));
    public IEnumerator<AutomationElement> GetEnumerator() { for (var index = 0; index < Count; index++) yield return this[index]; }
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class TreeWalker
{
    internal static TreeWalker ControlViewWalker { get; } = new();
    internal AutomationElement? GetParent(AutomationElement element) => AutomationElement.Call(() => AutomationElement.Wrap(AutomationElement.Client.ControlViewWalker.GetParentElement(element.NativeElement)));
    internal AutomationElement? GetFirstChild(AutomationElement element) => AutomationElement.Call(() => AutomationElement.Wrap(AutomationElement.Client.ControlViewWalker.GetFirstChildElement(element.NativeElement)));
    internal AutomationElement? GetNextSibling(AutomationElement element) => AutomationElement.Call(() => AutomationElement.Wrap(AutomationElement.Client.ControlViewWalker.GetNextSiblingElement(element.NativeElement)));
}

internal sealed class InvokePattern(UIA.IUIAutomationInvokePattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.InvokePattern.Pattern;
    internal void Invoke() => AutomationElement.Call(pattern.Invoke);
}
internal sealed class ValuePattern(UIA.IUIAutomationValuePattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.ValuePattern.Pattern;
    internal ValuePattern Current => this;
    internal string Value => AutomationElement.Call(() => pattern.CurrentValue ?? "");
    internal bool IsReadOnly => AutomationElement.Call(() => pattern.CurrentIsReadOnly != 0);
    internal void SetValue(string value) => AutomationElement.Call(() => pattern.SetValue(value));
}
internal sealed class RangeValuePattern(UIA.IUIAutomationRangeValuePattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.RangeValuePattern.Pattern;
    internal RangeValuePattern Current => this;
    internal double Value => AutomationElement.Call(() => pattern.CurrentValue);
    internal double Minimum => AutomationElement.Call(() => pattern.CurrentMinimum);
    internal double Maximum => AutomationElement.Call(() => pattern.CurrentMaximum);
    internal bool IsReadOnly => AutomationElement.Call(() => pattern.CurrentIsReadOnly != 0);
    internal void SetValue(double value) => AutomationElement.Call(() => pattern.SetValue(value));
}
internal sealed class TogglePattern(UIA.IUIAutomationTogglePattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.TogglePattern.Pattern;
    internal TogglePattern Current => this;
    internal ToggleState ToggleState => AutomationElement.Call(() => (ToggleState)pattern.CurrentToggleState);
    internal void Toggle() => AutomationElement.Call(pattern.Toggle);
}
internal sealed class SelectionItemPattern(UIA.IUIAutomationSelectionItemPattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.SelectionItemPattern.Pattern;
    internal SelectionItemPattern Current => this;
    internal bool IsSelected => AutomationElement.Call(() => pattern.CurrentIsSelected != 0);
    internal void Select() => AutomationElement.Call(pattern.Select);
}
internal sealed class SelectionPattern(UIA.IUIAutomationSelectionPattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.SelectionPattern.Pattern;
    internal SelectionPattern Current => this;
    internal bool CanSelectMultiple => AutomationElement.Call(() => pattern.CurrentCanSelectMultiple != 0);
    internal IEnumerable<AutomationElement> GetSelection() => AutomationElement.Call(() => new ElementCollection(pattern.GetCurrentSelection()));
}
internal sealed class ExpandCollapsePattern(UIA.IUIAutomationExpandCollapsePattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.ExpandCollapsePattern.Pattern;
    internal ExpandCollapsePattern Current => this;
    internal ExpandCollapseState ExpandCollapseState => AutomationElement.Call(() => (ExpandCollapseState)pattern.CurrentExpandCollapseState);
    internal void Expand() => AutomationElement.Call(pattern.Expand);
    internal void Collapse() => AutomationElement.Call(pattern.Collapse);
}
internal sealed class ScrollPattern(UIA.IUIAutomationScrollPattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.ScrollPattern.Pattern;
    internal ScrollPattern Current => this;
    internal double VerticalScrollPercent => AutomationElement.Call(() => pattern.CurrentVerticalScrollPercent);
    internal double HorizontalScrollPercent => AutomationElement.Call(() => pattern.CurrentHorizontalScrollPercent);
    internal void Scroll(ScrollAmount horizontal, ScrollAmount vertical) => AutomationElement.Call(() => pattern.Scroll((UIA.ScrollAmount)horizontal, (UIA.ScrollAmount)vertical));
}
internal sealed class ScrollItemPattern(UIA.IUIAutomationScrollItemPattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.ScrollItemPattern.Pattern;
    internal void ScrollIntoView() => AutomationElement.Call(pattern.ScrollIntoView);
}
internal sealed class TextPattern(UIA.IUIAutomationTextPattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.TextPattern.Pattern;
    internal TextRange DocumentRange => new(pattern.DocumentRange);
    internal TextRange[] GetSelection() => AutomationElement.Call(() => { var ranges = pattern.GetSelection(); return Enumerable.Range(0, ranges.Length).Select(index => new TextRange(ranges.GetElement(index))).ToArray(); });
}
internal sealed class TextRange(UIA.IUIAutomationTextRange range) { internal string GetText(int limit) => AutomationElement.Call(() => range.GetText(limit)); }
internal sealed class WindowPattern(UIA.IUIAutomationWindowPattern pattern)
{
    internal static AutomationPattern Pattern => System.Windows.Automation.WindowPattern.Pattern;
    internal static AutomationEvent WindowOpenedEvent => System.Windows.Automation.WindowPattern.WindowOpenedEvent;
    internal static AutomationEvent WindowClosedEvent => System.Windows.Automation.WindowPattern.WindowClosedEvent;
    internal WindowPattern Current => this;
    internal bool IsModal => AutomationElement.Call(() => pattern.CurrentIsModal != 0);
}
