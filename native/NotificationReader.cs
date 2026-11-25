using System.Diagnostics;
using System.Windows.Automation;

namespace AgyComputerUse;

// Recognize framework identifiers, never localized button labels or row indices.
internal static class NotificationReader
{
    internal sealed record Card(AutomationElement Element, string App, string Title, string Body, AutomationElement? Dismiss, AutomationElement[] Actions)
    {
        internal string Identity => string.Join("\u001f", App, Title, Body);
    }
    internal sealed record AppGroup(string App, AutomationElement Dismiss);
    internal sealed record Scan(Card[] Cards, AppGroup[] Groups, bool Complete, bool Recognized);
    internal sealed record WinRtNotification(uint Id, string App, string Title, string Body);

    internal static (string Status, WinRtNotification[] Items) TryReadWinRt()
    {
        try
        {
            var listener = Windows.UI.Notifications.Management.UserNotificationListener.Current;
            var access = listener.GetAccessStatus();
            var statusName = access.ToString().ToLowerInvariant();
            if (access != Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus.Allowed)
                return (statusName, []);
            var list = listener.GetNotificationsAsync(Windows.UI.Notifications.NotificationKinds.Toast)
                .AsTask().WaitAsync(TimeSpan.FromMilliseconds(1500)).GetAwaiter().GetResult();
            var result = new List<WinRtNotification>();
            foreach (var note in list)
            {
                var app = "";
                try { app = note.AppInfo?.DisplayInfo?.DisplayName ?? ""; } catch { }
                var toast = note.Notification?.Visual?.GetBinding(Windows.UI.Notifications.KnownNotificationBindings.ToastGeneric);
                var texts = toast?.GetTextElements().Select(t => t.Text).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray() ?? [];
                var title = texts.Length > 0 ? texts[0] : "";
                var body = texts.Length > 1 ? string.Join("\n", texts.Skip(1)) : "";
                result.Add(new(note.Id, app, title, body));
            }
            return (statusName, result.ToArray());
        }
        catch
        {
            return ("unavailable", []);
        }
    }

    internal static Scan Read(AutomationElement root)
    {
        var timer = Stopwatch.StartNew();
        var queue = new Queue<(AutomationElement Element, int Depth, string? Card, bool InList, bool InButton, string[] Groups)>();
        var nodes = new List<(AutomationElement Element, string Id, string Name, string? Card, bool InButton, string[] Groups, int Type, bool Enabled, string? DismissRuntime)>();
        var rows = new Dictionary<string, AutomationElement>();
        var rowGroups = new Dictionary<string, string[]>();
        var walker = TreeWalker.ControlViewWalker;
        var complete = true;
        var recognized = false;
        var cachedRoot = root.BuildUpdatedCache();
        queue.Enqueue((cachedRoot, 0, null, false, false, []));
        while (queue.Count > 0)
        {
            if (nodes.Count >= 600 || timer.ElapsedMilliseconds >= 2000) { complete = false; break; }
            var (element, depth, row, inList, inButton, groups) = queue.Dequeue();
            try
            {
                var snap = element.ReadCached();
                if (snap.AutomationId == "MainListView") { recognized = true; inList = true; }
                if (row == null && snap.ControlType == ControlType.Group) groups = [..groups, snap.RuntimeId];
                if (inList && snap.ControlType == ControlType.ListItem)
                {
                    row = snap.RuntimeId;
                    rows[row] = element;
                    rowGroups[row] = groups;
                }
                inButton |= snap.ControlType == ControlType.Button;
                var id = snap.AutomationId;
                nodes.Add((element, id, snap.Name, row, inButton, groups, snap.ControlTypeId, snap.IsEnabled, id == "DismissButton" ? snap.RuntimeId : null));
                var child = walker.GetFirstChildCached(element);
                if (depth >= 20) { if (child != null) complete = false; continue; }
                for (var count = 0; child != null; count++)
                {
                    if (count >= 600 || nodes.Count + queue.Count >= 600 || timer.ElapsedMilliseconds >= 2000) { complete = false; break; }
                    queue.Enqueue((child, depth + 1, row, inList, inButton, groups));
                    child = walker.GetNextSiblingCached(child);
                }
            }
            catch (ElementNotAvailableException) { complete = false; }
            catch (System.Runtime.InteropServices.COMException) { complete = false; }
        }
        var cards = new List<Card>();
        foreach (var (key, row) in rows)
        {
            var descendants = nodes.Where(node => node.Card == key).ToArray();
            var title = descendants.Where(node => node.Id == "Title").ToArray();
            var dismiss = descendants.Where(node => node.Id == "DismissButton").ToArray();
            // A generic calendar/list item in the same panel is not a notification.
            if (title.Length != 1 || dismiss.Length != 1) continue;
            var attribution = descendants.FirstOrDefault(node => node.Id == "Attribution").Name ?? "";
            foreach (var group in rowGroups[key].Reverse())
            {
                var labels = nodes.Where(node => node.Card == null && node.Id == "Title" && node.Groups.Contains(group)).Select(node => node.Name).Distinct().ToArray();
                if (labels.Length == 1) { attribution = labels[0]; break; }
            }
            var body = string.Join("\n", descendants.Where(node => !node.InButton && node.Id is not ("Title" or "Attribution" or "DismissButton" or "TimeStamp") && node.Type == ControlType.Text.Id).Select(node => node.Name).Where(name => !string.IsNullOrWhiteSpace(name)).Distinct());
            if (title[0].Name.Length > 12000 || attribution.Length > 2000 || body.Length > 12000) { complete = false; continue; }
            var actions = descendants.Where(node => node.Id is not ("DismissButton" or "SettingsButton" or "ExpandButton" or "SeeMoreLessButton") && node.Type == ControlType.Button.Id && node.Enabled).Select(node => node.Element).ToArray();
            cards.Add(new(row, attribution, title[0].Name, body, dismiss[0].Element, actions));
        }
        var appGroups = new List<AppGroup>();
        var groupKeys = nodes.Where(node => node.Card == null).SelectMany(node => node.Groups).Distinct().ToArray();
        var knownDismiss = new HashSet<string>();
        foreach (var group in groupKeys.OrderByDescending(key => nodes.First(node => node.Groups.Contains(key)).Groups.Length))
        {
            var groupNodes = nodes.Where(node => node.Card == null && node.Groups.Contains(group)).ToArray();
            var titles = groupNodes.Where(node => node.Id == "Title").Select(node => node.Name).Distinct().ToArray();
            var dismisses = groupNodes.Where(node => node.Id == "DismissButton" && node.Type == ControlType.Button.Id).ToArray();
            if (titles.Length != 1 || dismisses.Length != 1) continue;
            if (titles[0].Length > 2000) { complete = false; continue; }
            var runtime = dismisses[0].DismissRuntime!;
            if (knownDismiss.Add(runtime)) appGroups.Add(new(titles[0], dismisses[0].Element));
        }
        return new(cards.ToArray(), appGroups.ToArray(), complete, recognized);
    }
}
