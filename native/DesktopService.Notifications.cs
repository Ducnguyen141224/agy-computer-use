using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private readonly Dictionary<string, string> notificationIdentities = new();
    private object WaitNotification(JsonElement args)
    {
        var timer = Stopwatch.StartNew();
        var timeout = args.Number("timeout_ms", 3000, 0, 10000);
        var present = args.Flag("present", true);
        var newOnly = args.Flag("new_only");
        HashSet<string>? baseline = null;
        JsonElement? list = null;
        string? unavailable = null;
        do
        {
            try
            {
                list = JsonSerializer.SerializeToElement(Notifications(JsonSerializer.SerializeToElement(new { window_id = args.Text("window_id"), app = args.Text("app", 2048), text = args.Text("text"), limit = args.Number("limit", 30, 1, 80) }, Program.Json), "list"), Program.Json);
                unavailable = null;
                var rows = list.Value.GetProperty("notifications").EnumerateArray().ToArray();
                string Key(JsonElement row) { var reference = row.GetProperty("ref").GetString()!; return Resolve(reference, false).RuntimeId + "|" + notificationIdentities.GetValueOrDefault(reference); }
                JsonElement[] matching = rows;
                if (newOnly)
                {
                    if (baseline == null) { if (list.Value.GetProperty("complete").GetBoolean()) baseline = rows.Select(Key).ToHashSet(); matching = []; }
                    else matching = rows.Where(row => !baseline.Contains(Key(row))).ToArray();
                }
                var exists = matching.Length > 0;
                if (exists == present && (exists || list.Value.GetProperty("complete").GetBoolean())) return new { verification = "passed", elapsed_ms = timer.ElapsedMilliseconds, new_only = newOnly, matching, state = list };
            }
            catch (DesktopError error) when (error.Code is "SURFACE_NOT_FOUND" or "SURFACE_NOT_SUPPORTED" or "WINDOW_NOT_FOUND" or "WINDOW_CHANGED") { unavailable = error.Code; }
            catch (ElementNotAvailableException) { unavailable = "SURFACE_CHANGED"; }
            if (timer.ElapsedMilliseconds >= timeout) break;
            Thread.Sleep(100);
        } while (true);
        return new { verification = "failed", elapsed_ms = timer.ElapsedMilliseconds, state = unavailable == null ? list : null, unavailable, note = "Matching notifications were not observed before timeout. A closed or changing center is not proof of absence." };
    }
    private WindowEntry NotificationWindow(string? id)
    {
        if (id == null)
        {
            var candidates = new List<WindowEntry>();
            var surfaces = JsonSerializer.SerializeToElement(ListSurfaces(JsonSerializer.SerializeToElement(new { })), Program.Json);
            foreach (var item in surfaces.GetProperty("surfaces").EnumerateArray())
            {
                if (item.GetProperty("kind").GetString() != "shell_panel") continue;
                var candidate = GetWindow(item.GetProperty("window").GetProperty("id").GetString());
                if (NotificationReader.Read(AutomationElement.FromHandle(candidate.Handle)).Recognized) candidates.Add(candidate);
            }
            if (candidates.Count != 1) throw new DesktopError(candidates.Count == 0 ? "SURFACE_NOT_FOUND" : "AMBIGUOUS_TARGET", "Choose one visible notification center window from list_surfaces.");
            return candidates[0];
        }
        var window = GetWindow(id);
        if (SurfaceKind(window.Handle, window.Pid) != "shell_panel" || !NotificationReader.Read(AutomationElement.FromHandle(window.Handle)).Recognized) throw new DesktopError("SURFACE_NOT_FOUND", "Open and observe the visible Windows notification center first.");
        return window;
    }
    private object Notifications(JsonElement args, string operation)
    {
        if (args.Flag("open")) _ = OpenSurface(JsonSerializer.SerializeToElement(new { surface = "action_center" }));
        if (operation == "list")
        {
            var window = NotificationWindow(args.Text("window_id"));
            var root = AutomationElement.FromHandle(window.Handle);
            var scan = NotificationReader.Read(root);
            if (!scan.Recognized) throw new DesktopError("SURFACE_NOT_SUPPORTED", "The active shell panel does not expose the recognized notification list. Use desktop_observe for this Windows build.");
            foreach (var key in notificationIdentities.Keys.ToArray()) if (!references.TryGetValue(key, out var cached) || cached.Expires < DateTime.UtcNow) notificationIdentities.Remove(key);
            if (notificationIdentities.Count > 160) notificationIdentities.Clear();
            var filtered = scan.Cards.Where(card => (args.Text("app", 2048) is not string app || card.App.Contains(app, StringComparison.OrdinalIgnoreCase)) && (args.Text("text") is not string text || (card.Title + "\n" + card.Body).Contains(text, StringComparison.OrdinalIgnoreCase))).ToArray();
            var limit = args.Number("limit", 30, 1, 80);
            var complete = scan.Complete && filtered.Length <= limit;
            var result = new List<object>();
            foreach (var card in filtered.Take(limit))
            {
                var generated = new List<string>();
                try
                {
                    string Reference(AutomationElement element) { var value = AddRef(window, element); generated.Add(value); return value; }
                    var reference = Reference(card.Element);
                    var expanded = card.Element.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var expand) ? ((ExpandCollapsePattern)expand).Current.ExpandCollapseState.ToString().ToLowerInvariant() : null;
                    var actions = card.Actions.Select(button => new { @ref = Reference(button), name = button.Current.Name }).ToArray();
                    notificationIdentities[reference] = card.Identity;
                    result.Add(new { @ref = reference, app = card.App, title = card.Title, body = card.Body.Length == 0 ? null : card.Body, dismissible = card.Dismiss != null, expanded, actions });
                }
                catch (Exception error) when (error is ElementNotAvailableException || error is DesktopError { Code: "STALE_REF" })
                {
                    complete = false;
                    foreach (var value in generated) { references.Remove(value); notificationIdentities.Remove(value); }
                }
            }
            var clear = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "ClearAllButton"));
            return new { window_id = window.Id, notifications = result, count = result.Count, complete, dismiss_all_ref = clear.Count == 1 ? AddRef(window, clear[0]) : null, scope = "materialized notification cards in the active shell accessibility tree; collapsed/virtualized groups may require expansion" };
        }
        if (operation == "dismiss_all" && args.Text("app", 2048) is string appFilter)
        {
            var window = NotificationWindow(args.Text("window_id"));
            var scan = NotificationReader.Read(AutomationElement.FromHandle(window.Handle));
            if (!scan.Recognized || !scan.Complete) throw new DesktopError("NOTIFICATION_SCAN_INCOMPLETE", "App groups could not be completely identified. No dismissal was sent.");
            var groups = scan.Groups.Where(group => group.App.Equals(appFilter, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (groups.Length == 0 && scan.Cards.Any(card => card.App.Equals(appFilter, StringComparison.OrdinalIgnoreCase))) throw new DesktopError("ACTION_UNSUPPORTED", "This Windows provider has no guarded group-dismiss control. Dismiss the returned cards individually.");
            var results = new List<object>();
            var failed = false;
            var delivered = 0;
            foreach (var group in groups)
            {
                // Recheck the exact group and button before each non-atomic mutation.
                var runtime = RuntimeId(group.Dismiss);
                var live = NotificationReader.Read(AutomationElement.FromHandle(window.Handle));
                if (!live.Complete || !live.Groups.Any(item => item.App == group.App && RuntimeId(item.Dismiss) == runtime))
                {
                    results.Add(new { app = group.App, delivery = "not_sent", verification = "failed" }); failed = true; break;
                }
                Native.RequireInputPrivilege(window.Pid);
                _ = Act(JsonSerializer.SerializeToElement(new { action = "click", @ref = AddRef(window, group.Dismiss) }));
                results.Add(new { app = group.App, delivery = "sent", verification = "not_requested" });
                delivered++;
            }
            return new { delivery = groups.Length == 0 ? "already_satisfied" : delivered > 0 ? "sent" : "not_sent", verification = failed ? "failed" : groups.Length == 0 ? "passed" : "not_requested", groups = results, note = "Only exact observed app-group dismiss controls were invoked. Re-list to verify; earlier group dismissals are not rolled back if a later group changes." };
        }
        var entry = Resolve(args.Text("ref"));
        _ = NotificationWindow(entry.Window.Id);
        Native.RequireInputPrivilege(entry.Window.Pid);
        if (operation == "dismiss_all")
        {
            if (entry.Element.Current.AutomationId != "ClearAllButton" || entry.Element.Current.ControlType != ControlType.Button) throw new DesktopError("INVALID_ARGUMENT", "dismiss_all requires the fresh dismiss_all_ref returned by list_notifications.");
            if (args.Text("app") != null || args.Text("text") != null) throw new DesktopError("INVALID_ARGUMENT", "Clear-all is unfiltered. Use the listed card refs to dismiss a filtered set individually.");
            _ = Act(JsonSerializer.SerializeToElement(new { action = "click", @ref = entry.Ref }));
            return new { delivery = "sent", verification = "not_requested", note = "Windows clear-all was invoked. Re-list the notification center to inspect the resulting state." };
        }
        if (!notificationIdentities.TryGetValue(entry.Ref, out var identity)) throw new DesktopError("NOTIFICATION_NOT_FOUND", "Use a card ref from a fresh list_notifications result, not a generic shell button.");
        var scanNow = NotificationReader.Read(AutomationElement.FromHandle(entry.Window.Handle));
        if (!scanNow.Recognized || !scanNow.Complete) throw new DesktopError("NOTIFICATION_SCAN_INCOMPLETE", "The notification identity could not be verified. No action was sent.");
        var cards = scanNow.Cards.Where(card => RuntimeId(card.Element) == entry.RuntimeId && card.Identity == identity).ToArray();
        if (cards.Length != 1) throw new DesktopError("NOTIFICATION_NOT_FOUND", "The observed notification changed or disappeared. No action was sent.");
        var selected = cards[0];
        if (args.Text("expected_app", 2000) is string expectedApp && selected.App != expectedApp || args.Text("expected_title", 12000) is string expectedTitle && selected.Title != expectedTitle) throw new DesktopError("NOTIFICATION_NOT_FOUND", "Notification does not match the expected app/title. No action was sent.");
        AutomationElement target;
        if (operation == "dismiss") target = selected.Dismiss ?? throw new DesktopError("ACTION_UNSUPPORTED", "No dismiss control is exposed for this notification.");
        else
        {
            var action = Resolve(args.Text("action_ref"));
            if (action.Window.Id != entry.Window.Id || !selected.Actions.Any(button => RuntimeId(button) == action.RuntimeId)) throw new DesktopError("INVALID_ARGUMENT", "action_ref must belong to this exact notification card.");
            target = action.Element;
        }
        _ = Act(JsonSerializer.SerializeToElement(new { action = "click", @ref = AddRef(entry.Window, target) }));
        if (operation != "dismiss") return new { delivery = "sent", verification = "not_requested", note = "Verify the action effect in its application." };
        notificationIdentities.Remove(entry.Ref);
        var timer = Stopwatch.StartNew();
        do
        {
            var after = NotificationReader.Read(AutomationElement.FromHandle(entry.Window.Handle));
            if (after.Recognized && after.Complete && !after.Cards.Any(card => RuntimeId(card.Element) == entry.RuntimeId)) return new { delivery = "sent", verification = "passed" };
            Thread.Sleep(60);
        } while (timer.ElapsedMilliseconds < 1500);
        return new { delivery = "sent", verification = "failed", note = "Card disappearance was not verified. Inspect before retrying." };
    }
}
