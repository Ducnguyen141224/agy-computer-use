using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private static string NormalizeRole(string role) => role.Trim().ToLowerInvariant() switch
    {
        "textbox" or "textfield" or "textarea" or "searchfield" => "edit",
        "link" => "hyperlink", "check_box" => "checkbox", "combo_box" => "combobox",
        "list_item" => "listitem", "menu_item" => "menuitem", "radio_button" => "radiobutton",
        "tab_item" => "tabitem", "tree_item" => "treeitem", var other => other
    };
    private static object? NativeProperty(AutomationElement element, int property) => AutomationElement.Call(() => element.NativeElement.GetCurrentPropertyValueEx(property, 1));
    private static bool? NativeBoolean(AutomationElement element, int property) => NativeProperty(element, property) is bool value ? value : null;
    private static bool? LegacyState(AutomationElement element, uint mask)
    {
        var value = NativeProperty(element, 30096);
        return value is int signed ? (unchecked((uint)signed) & mask) != 0 : value is uint unsigned ? (unsigned & mask) != 0 : null;
    }
    private static bool? FilterState(AutomationElement element, string state)
    {
        var current = element.Current;
        switch (state)
        {
            case "enabled": return current.IsEnabled;
            case "disabled": return !current.IsEnabled;
            case "secure": return current.IsPassword;
            case "offscreen": case "hidden": return current.IsOffscreen;
            case "focused": return NativeBoolean(element, 30008);
            case "required": return NativeBoolean(element, 30025);
            case "invalid": return NativeBoolean(element, 30103) is bool valid ? !valid : null;
            case "checked": case "indeterminate":
                if (element.TryGetCurrentPattern(TogglePattern.Pattern, out var toggle)) return ((TogglePattern)toggle).Current.ToggleState == (state == "checked" ? ToggleState.On : ToggleState.Indeterminate);
                if (state == "checked" && current.ControlType == ControlType.RadioButton && element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var radio)) return ((SelectionItemPattern)radio).Current.IsSelected;
                return false;
            case "selected": return element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection) && ((SelectionItemPattern)selection).Current.IsSelected;
            case "expanded": return element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState is ExpandCollapseState.Expanded or ExpandCollapseState.PartiallyExpanded;
            case "haspopup":
                if (element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out _)) return true;
                return LegacyState(element, 0x40000000);
            case "readonly":
                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) return ((ValuePattern)value).Current.IsReadOnly;
                if (element.TryGetCurrentPattern(RangeValuePattern.Pattern, out var range)) return ((RangeValuePattern)range).Current.IsReadOnly;
                return LegacyState(element, 0x40);
            case "multiselectable":
                if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var choices)) return ((SelectionPattern)choices).CanSelectMultiple;
                return false;
            case "modal": return element.TryGetCurrentPattern(WindowPattern.Pattern, out var window) && ((WindowPattern)window).Current.IsModal;
            case "busy": return LegacyState(element, 0x800);
            case "pressed": return LegacyState(element, 8);
            default: return null;
        }
    }
    private static bool ExtraFilters(AutomationElement element, JsonElement args, bool exact, ref bool complete)
    {
        bool Match(string value, string expected) => exact ? value.Equals(expected, StringComparison.OrdinalIgnoreCase) : value.Contains(expected, StringComparison.OrdinalIgnoreCase);
        if (args.Text("automation_id") is string id && !element.Current.AutomationId.Equals(id, StringComparison.Ordinal)) return false;
        if (args.Text("class_name") is string className && !element.Current.ClassName.Equals(className, StringComparison.OrdinalIgnoreCase)) return false;
        if (args.Text("description") is string description)
        {
            var help = element.Current.HelpText;
            if (help.Length == 0 && NativeProperty(element, 30094) is string legacy) help = legacy;
            if (!Match(help, description)) return false;
        }
        if (args.Text("value") is string expectedValue)
        {
            if (element.Current.IsPassword) return false;
            try { if (!Match(ReadElementValue(element), expectedValue)) return false; }
            catch (DesktopError error) when (error.Code == "ACTION_UNSUPPORTED") { return false; }
        }
        if (args.Text("text") is string text && !(args.Flag("text_any") && (Match(element.Current.Name, text) || Match(element.Current.HelpText, text))))
        {
            if (element.Current.IsPassword) return false;
            string content;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var document)) content = ((TextPattern)document).DocumentRange.GetText(12001);
            else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) content = ((ValuePattern)value).Current.Value;
            else content = element.Current.Name;
            if (!Match(content.Length > 12000 ? content[..12000] : content, text)) { if (!exact && content.Length > 12000) complete = false; return false; }
            if (exact && content.Length > 12000) return false;
        }
        if (args.TryGetProperty("states", out var states))
        {
            foreach (var filter in states.EnumerateObject())
            {
                var value = FilterState(element, filter.Name);
                if (value == null) { complete = false; return false; }
                if (value != filter.Value.GetBoolean()) return false;
            }
        }
        return true;
    }
}
