using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private string SelectOption(ElementEntry target, string option)
    {
        var element = target.Element;
        if (element.Current.ControlType is var type && type != ControlType.ComboBox && type != ControlType.List && type != ControlType.Menu && type != ControlType.Tree) throw new DesktopError("INVALID_ARGUMENT", "option requires an observed list, combobox, menu or tree container.");
        if (element.TryGetCurrentPattern(SelectionPattern.Pattern, out var selected) && ((SelectionPattern)selected).Current.GetSelection().Any(item => item.Current.Name == option)) return "already_satisfied";
        if (element.Current.ControlType == ControlType.ComboBox && element.TryGetCurrentPattern(ValuePattern.Pattern, out var currentValue) && ((ValuePattern)currentValue).Current.Value == option) return "already_satisfied";
        AutomationElement[] Candidates() => element.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.NameProperty, option)).Where(item => item.Current.IsEnabled && (item.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _) || item.Current.ControlType == ControlType.MenuItem && item.TryGetCurrentPattern(InvokePattern.Pattern, out _))).ToArray();
        var candidates = Candidates();
        var expanded = false;
        if (candidates.Length == 0 && element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) && ((ExpandCollapsePattern)expand).Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
        {
            ControlPresentation.Check(); ((ExpandCollapsePattern)expand).Expand(); expanded = true;
            // Only observations are retried while the provider builds the popup.
            for (var attempt = 0; attempt < 12; attempt++)
            {
                candidates = Candidates(); if (candidates.Length > 0) break; ControlPresentation.Delay(40);
            }
        }
        if (candidates.Length != 1) throw new DesktopError(candidates.Length == 0 ? "OPTION_NOT_FOUND" : "AMBIGUOUS_TARGET", "Selection needs one exact accessible option in this container. Observe the expanded control.", new { matches = candidates.Length, delivery = expanded ? "sent" : "not_sent" });
        var chosen = candidates[0];
        Native.RequireInputPrivilege(chosen.Current.ProcessId);
        // Retain the same generation/runtime/ancestry checks used by all ref actions.
        var guarded = Resolve(AddRef(target.Window, chosen));
        ControlPresentation.Check();
        if (guarded.Element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection)) ((SelectionItemPattern)selection).Select();
        else if (!guarded.Element.Current.IsOffscreen && guarded.Element.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else throw new DesktopError("ACTION_UNSUPPORTED", "The selected option no longer exposes a selection action.");
        return "sent";
    }
}
