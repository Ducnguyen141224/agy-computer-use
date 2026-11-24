using System.Diagnostics;
using System.Security;
using System.Text.Json;
using AgyComputerUse;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using Notify = CommunityToolkit.WinUI.Notifications.ToastNotificationManagerCompat;

internal static class LiveNotificationSmoke
{
    internal static int Run(string[] args)
    {
        var nonce = Guid.NewGuid().ToString("N");
        var group = "agy" + nonce[..12];
        var registered = false;
        using var desktop = new DesktopService();
        JsonElement Call(string method, object args) => JsonSerializer.SerializeToElement(desktop.Dispatch(method, JsonSerializer.SerializeToElement(args)), AgyComputerUse.Program.Json);
        var checks = 0;
        void Check(bool value, string description) { if (!value) throw new Exception(description); checks++; Console.WriteLine("PASS " + description); }
        string? center = null;
        try
        {
            Console.WriteLine(JsonSerializer.Serialize(new { inherited_package = Native.PackageIdentity() }));
            if (Native.PackageIdentity() != null) throw new Exception("The test must run outside its host application's package identity so its app-group dismissal cannot affect the host's notifications.");
            registered = true;
            Notify.OnActivated += _ => { }; // Registers this test executable's own COM activator.
            var notifier = Notify.CreateToastNotifier();
            Call("system", new { operation = "open_surface", surface = "action_center" });
            var baselinePanel = Call("query", new { operation = "surface_wait", surface = "shell_panel", timeout_ms = 3000 });
            if (baselinePanel.GetProperty("verification").GetString() != "passed") throw new Exception("Baseline notification center did not open.");
            center = baselinePanel.GetProperty("surfaces")[0].GetProperty("window").GetProperty("id").GetString();
            var baseline = NotificationReader.Read(AgyComputerUse.AutomationElement.FromHandle(Native.GetForegroundWindow()));
            if (!baseline.Recognized || !baseline.Complete) throw new Exception("Cannot establish a complete app-group baseline; no bulk dismissal will be attempted.");
            var originalApps = baseline.Groups.Select(item => item.App).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Call("system", new { operation = "close_surface", window_id = center });
            var closed = Call("query", new { operation = "surface_wait", surface = "shell_panel", present = false, timeout_ms = 3000 });
            if (closed.GetProperty("verification").GetString() != "passed") throw new Exception("Baseline center did not close.");
            var firstTitle = "AGY disposable " + nonce;
            Post(firstTitle, "one", false);
            Console.WriteLine(JsonSerializer.Serialize(new { disposable_history_count = Notify.History.GetHistory().Count(item => item.Group == group) }));
            Call("system", new { operation = "open_surface", surface = "action_center" });
            var appeared = Call("query", new { operation = "surface_wait", surface = "shell_panel", timeout_ms = 3000 });
            if (appeared.GetProperty("verification").GetString() != "passed") throw new Exception("Notification center did not open.");
            center = appeared.GetProperty("surfaces")[0].GetProperty("window").GetProperty("id").GetString();
            JsonElement List() => Call("system", new { operation = "list_notifications", text = nonce, limit = 80 });
            JsonElement WaitCard(string title)
            {
                var timer = Stopwatch.StartNew();
                do
                {
                    var list = List(); center = list.GetProperty("window_id").GetString();
                    foreach (var item in list.GetProperty("notifications").EnumerateArray()) if (item.GetProperty("title").GetString() == title) return item.Clone();
                    Thread.Sleep(100);
                } while (timer.ElapsedMilliseconds < (args.Contains("--inspect-live") ? 30000 : 6000));
                var history = Notify.History.GetHistory();
                var root = AgyComputerUse.AutomationElement.FromHandle(Native.GetForegroundWindow());
                var main = root.FindFirst(System.Windows.Automation.TreeScope.Descendants, new System.Windows.Automation.PropertyCondition(AgyComputerUse.AutomationElement.AutomationIdProperty, "MainListView"));
                if (main != null && main.TryGetCurrentPattern(AgyComputerUse.ScrollPattern.Pattern, out var scroll)) Console.WriteLine(JsonSerializer.Serialize(new { center_scroll_percent = ((AgyComputerUse.ScrollPattern)scroll).Current.VerticalScrollPercent }));
                var queue = new Queue<AgyComputerUse.AutomationElement>(); queue.Enqueue(root);
                var tags = new Dictionary<string, int>(); var ownNodes = 0; var visited = 0;
                while (queue.Count > 0 && visited++ < 500)
                {
                    var element = queue.Dequeue(); var current = element.Current;
                    if (current.Name.Contains(nonce)) ownNodes++;
                    var tag = current.ControlType.ProgrammaticName + ":" + current.AutomationId;
                    tags[tag] = tags.GetValueOrDefault(tag) + 1;
                    var child = AgyComputerUse.TreeWalker.ControlViewWalker.GetFirstChild(element);
                    while (child != null && queue.Count < 500) { queue.Enqueue(child); child = AgyComputerUse.TreeWalker.ControlViewWalker.GetNextSibling(child); }
                }
                Console.WriteLine(JsonSerializer.Serialize(new { disposable_history_count = history.Count(item => item.Group == group), own_nodes = ownNodes, framework_tags = tags }));
                throw new Exception("Disposable toast was not recognized in the real notification center.");
            }
            void Post(string title, string tag, bool action)
            {
                var xml = new XmlDocument();
                xml.LoadXml("<toast" + (action ? " scenario=\"reminder\"" : "") + "><visual><binding template=\"ToastGeneric\"><text>" + SecurityElement.Escape(title) + "</text><text>Disposable computer-use test</text></binding></visual><audio silent=\"true\"/>" + (action ? "<actions><action content=\"Dismiss disposable test\" arguments=\"dismiss\" activationType=\"system\"/></actions>" : "") + "</toast>");
                notifier.Show(new ToastNotification(xml) { Tag = tag, Group = group, ExpirationTime = DateTimeOffset.Now.AddMinutes(2), SuppressPopup = false });
            }
            var card = WaitCard(firstTitle);
            var ownApp = card.GetProperty("app").GetString()!;
            Check(card.GetProperty("dismissible").GetBoolean(), "lists the actual disposable Windows toast with a dismiss target");
            var arrival = Call("query", new { operation = "notification_wait", text = nonce, timeout_ms = 2000 });
            Check(arrival.GetProperty("verification").GetString() == "passed", "notification wait observes the real disposable toast");
            Check(Call("query", new { operation = "notification_wait", text = nonce, new_only = true, timeout_ms = 0 }).GetProperty("verification").GetString() == "failed", "new-arrival wait does not misreport an existing toast as a new notification");
            var refused = false;
            try { Call("system", new { operation = "dismiss_notification", @ref = card.GetProperty("ref").GetString(), expected_title = "wrong disposable title" }); } catch (DesktopError error) when (error.Code == "NOTIFICATION_NOT_FOUND") { refused = true; }
            Check(refused && List().GetProperty("notifications").GetArrayLength() == 1, "wrong expected identity sends no dismissal and preserves the toast");
            var dismissed = Call("system", new { operation = "dismiss_notification", @ref = card.GetProperty("ref").GetString(), expected_title = firstTitle });
            Check(dismissed.GetProperty("verification").GetString() == "passed" && List().GetProperty("notifications").GetArrayLength() == 0, "dismisses only the identified disposable toast and verifies disappearance");
            var newTitle = "AGY new arrival " + nonce;
            var delayed = Task.Run(() => { Thread.Sleep(600); Post(newTitle, "five", false); });
            var fresh = Call("query", new { operation = "notification_wait", text = nonce, new_only = true, timeout_ms = 5000 });
            delayed.GetAwaiter().GetResult();
            Check(fresh.GetProperty("verification").GetString() == "passed" && fresh.GetProperty("matching").GetArrayLength() == 1, "new-arrival wait observes a real toast posted after its RAM baseline");
            var freshCard = WaitCard(newTitle); Call("system", new { operation = "dismiss_notification", @ref = freshCard.GetProperty("ref").GetString(), expected_title = newTitle });
            var actionTitle = "AGY action " + nonce;
            Post(actionTitle, "two", true);
            Call("system", new { operation = "open_surface", surface = "action_center" });
            var actionable = WaitCard(actionTitle);
            if (actionable.TryGetProperty("expanded", out var expanded) && expanded.GetString() == "collapsed")
            {
                Call("act", new { action = "expand", @ref = actionable.GetProperty("ref").GetString() });
                actionable = WaitCard(actionTitle);
            }
            var actions = actionable.GetProperty("actions").EnumerateArray().ToArray();
            Console.WriteLine(JsonSerializer.Serialize(new { disposable_action_names = actions.Select(item => item.GetProperty("name").GetString()).ToArray() }));
            Check(actions.Length == 1, "reads the real toast's named system action");
            Call("system", new { operation = "notification_action", @ref = actionable.GetProperty("ref").GetString(), action_ref = actions[0].GetProperty("ref").GetString(), expected_title = actionTitle });
            var effect = Stopwatch.StartNew();
            while (Notify.History.GetHistory().Any(item => item.Group == group) && effect.ElapsedMilliseconds < 4000) Thread.Sleep(100);
            Check(!Notify.History.GetHistory().Any(item => item.Group == group), "action operates on the exact disposable card and the OS history confirms removal");
            if (string.IsNullOrWhiteSpace(ownApp) || originalApps.Contains(ownApp)) throw new Exception("Disposable app label is ambiguous with an existing group; no bulk dismissal was attempted.");
            Post("AGY group one " + nonce, "three", false);
            Post("AGY group two " + nonce, "four", false);
            Call("system", new { operation = "open_surface", surface = "action_center" });
            _ = WaitCard("AGY group two " + nonce);
            Check(Notify.History.GetHistory().Count(item => item.Group == group) == 2, "stages two disposable notifications for the app-group dismissal");
            var bulk = Call("system", new { operation = "dismiss_all_notifications", app = ownApp });
            Check(bulk.GetProperty("delivery").GetString() == "sent", "filtered dismiss-all invokes the exact disposable app group's control");
            effect.Restart();
            while (Notify.History.GetHistory().Any(item => item.Group == group) && effect.ElapsedMilliseconds < 4000) Thread.Sleep(100);
            Check(!Notify.History.GetHistory().Any(item => item.Group == group), "the OS history confirms both grouped test notifications were removed");
            Call("system", new { operation = "open_surface", surface = "action_center" });
            var remaining = NotificationReader.Read(AgyComputerUse.AutomationElement.FromHandle(Native.GetForegroundWindow()));
            Check(remaining.Complete && originalApps.IsSubsetOf(remaining.Groups.Select(item => item.App).ToHashSet(StringComparer.OrdinalIgnoreCase)), "pre-existing user notification groups remain present");
            Console.WriteLine($"Live notification smoke passed: {checks} checks. No user notifications were dismissed; no evidence files saved.");
            return 0;
        }
        finally
        {
            if (registered)
            {
                try { foreach (var tag in new[] { "one", "two", "three", "four", "five" }) Notify.History.Remove(tag, group); }
                finally { Notify.Uninstall(); } // This test executable's own registration only.
            }
            if (center != null) { try { Call("system", new { operation = "close_surface", window_id = center }); } catch { } }
        }
    }
}
