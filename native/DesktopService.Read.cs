using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    // Continuations hold only bounded text and guarded refs, never screenshots or disk state.
    private sealed class TextReaderState(string reference, string direction)
    {
        internal string Reference = reference, Direction = direction, Pending = "";
        internal string[] Previous = [];
        internal bool NeedsScroll, Boundary, Partial;
        internal DateTime Expires = DateTime.UtcNow.AddMinutes(2);
    }
    private readonly Dictionary<string, TextReaderState> readers = new();
    private sealed record TextPage(string[] Lines, bool Complete, int Nodes);

    private static int SafeTextLength(string text, int limit)
    {
        var take = Math.Min(limit, text.Length);
        if (take > 0 && take < text.Length && char.IsHighSurrogate(text[take - 1]) && char.IsLowSurrogate(text[take])) take--;
        return take;
    }

    // Linear suffix/prefix matching also bounds work for thousands of identical text lines.
    private static int PageOverlap(string[] source, string[] prefix)
    {
        if (prefix.Length == 0) return 0;
        var fallback = new int[prefix.Length];
        for (var i = 1; i < prefix.Length; i++)
        {
            var matched = fallback[i - 1];
            while (matched > 0 && prefix[i] != prefix[matched]) matched = fallback[matched - 1];
            if (prefix[i] == prefix[matched]) matched++;
            fallback[i] = matched;
        }
        var overlap = 0;
        foreach (var line in source)
        {
            while (overlap > 0 && (overlap == prefix.Length || line != prefix[overlap])) overlap = fallback[overlap - 1];
            if (line == prefix[overlap]) overlap++;
        }
        return overlap;
    }

    private static TextPage ReadPage(AutomationElement root, int maxChars, Stopwatch clock, int budget)
    {
        var lines = new List<string>(); var characters = 0; var nodes = 0; var complete = true;
        var walker = TreeWalker.ControlViewWalker;
        var stack = new Stack<(AutomationElement Element, int Depth)>(); stack.Push((root, 0));
        while (stack.Count > 0)
        {
            if (nodes >= 1200 || clock.ElapsedMilliseconds >= budget) { complete = false; break; }
            var (element, depth) = stack.Pop(); nodes++;
            var current = element.Current;
            if (current.IsPassword || current.IsOffscreen) continue;
            var children = new List<AutomationElement>();
            var child = walker.GetFirstChild(element);
            while (child != null)
            {
                if (nodes + stack.Count + children.Count >= 1200 || clock.ElapsedMilliseconds >= budget) { complete = false; break; }
                children.Add(child); child = walker.GetNextSibling(child);
            }
            if (children.Count > 0)
            {
                if (depth >= 40) { complete = false; continue; }
                var scrollbarsOnly = (current.ControlType == ControlType.Document || current.ControlType == ControlType.Edit)
                    && children.All(c => c.Current.ControlType == ControlType.ScrollBar);
                if (!scrollbarsOnly)
                {
                    for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], depth + 1));
                    continue;
                }
            }
            string? text = null;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var document)) text = ((TextPattern)document).DocumentRange.GetText(maxChars + 1);
            else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var value)) text = ((ValuePattern)value).Current.Value;
            else if (current.ControlType == ControlType.Text || current.ControlType == ControlType.Document) text = current.Name;
            if (string.IsNullOrWhiteSpace(text)) continue;
            // Preserve repeated lines; only page overlap is removed later.
            foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (line.Length == 0) continue;
                if (characters + line.Length + 1 > maxChars)
                {
                    var remaining = maxChars - characters;
                    if (remaining > 0) lines.Add(line[..SafeTextLength(line, remaining)]);
                    return new(lines.ToArray(), false, nodes);
                }
                lines.Add(line); characters += line.Length + 1;
            }
        }
        return new(lines.ToArray(), complete, nodes);
    }

    private object ReadText(JsonElement args)
    {
        var operation = args.Text("operation") ?? "snapshot";
        var maxChars = args.Number("max_chars", 16000, 100, 50000);
        var budget = args.Number("budget_ms", 5000, 200, 8000);
        var clock = Stopwatch.StartNew();
        if (operation == "snapshot")
        {
            var entry = args.Text("root_ref") is string rootRef ? Resolve(rootRef) : null;
            var window = GetWindow(args.Text("window_id") ?? entry?.Window.Id);
            if (entry != null && entry.Window.Id != window.Id) throw new DesktopError("INVALID_ARGUMENT", "root_ref belongs to another window.");
            if (Native.IsIconic(window.Handle)) throw new DesktopError("WINDOW_MINIMIZED", "Restore the chosen window before reading.");
            var root = entry?.Element ?? AutomationElement.FromHandle(window.Handle);
            var page = ReadPage(root, maxChars, clock, budget);
            return new { text = string.Join("\n", page.Lines), complete = page.Complete, source = "uia_text", image_count = 0, page.Nodes, elapsed_ms = clock.ElapsedMilliseconds, scope = "currently exposed visible UIA text; not proof of full document/history" };
        }
        foreach (var expired in readers.Where(pair => pair.Value.Expires < DateTime.UtcNow).Select(pair => pair.Key).ToArray()) readers.Remove(expired);
        var id = args.Text("reader_id");
        TextReaderState reader;
        if (id != null)
        {
            if (!readers.TryGetValue(id, out reader!)) throw new DesktopError("STALE_READER", "Reader expired or its desktop session ended. Observe before starting a new reader.");
        }
        else
        {
            if (readers.Count >= 8) throw new DesktopError("CACHE_LIMIT", "Finish a reader or end the control task before starting another.");
            var entry = Resolve(args.Text("root_ref"));
            _ = RequirePattern<ScrollPattern>(entry, ScrollPattern.Pattern);
            reader = new(entry.Ref, args.Text("direction") ?? "down");
            id = "reader-" + Guid.NewGuid().ToString("N"); readers[id] = reader;
        }
        var output = new StringBuilder(); var pages = 0; var nodes = 0; var scrolled = false;
        var reason = "page_limit";
        var pageLimit = args.Number("max_pages", 8, 1, 30);
        while (true)
        {
            ControlPresentation.Check();
            if (reader.Pending.Length > 0)
            {
                var take = SafeTextLength(reader.Pending, maxChars - output.Length);
                output.Append(reader.Pending.AsSpan(0, take)); reader.Pending = reader.Pending[take..];
                if (reader.Pending.Length > 0) { reason = "character_limit"; break; }
            }
            if (reader.Partial) { reason = "partial_uia_page"; break; }
            if (reader.Boundary) { reason = "scroll_boundary"; break; }
            if (pages >= pageLimit) break;
            if (clock.ElapsedMilliseconds >= budget) { reason = "time_limit"; break; }
            var entry = Resolve(reader.Reference);
            if (!entry.Element.Current.IsEnabled || entry.Element.Current.IsOffscreen) throw new DesktopError("NOT_ACTIONABLE", "Reader scroll container is disabled or offscreen.");
            if (Native.IsIconic(entry.Window.Handle)) throw new DesktopError("WINDOW_MINIMIZED", "Restore the reader window before continuing.");
            var scrolling = RequirePattern<ScrollPattern>(entry, ScrollPattern.Pattern);
            Native.RequireInputPrivilege(entry.Window.Pid); Native.RequireInputPrivilege(entry.ElementPid);
            if (reader.NeedsScroll)
            {
                var before = scrolling.Current.VerticalScrollPercent;
                // UIA percentages cover the scrollable range, while ViewSize covers the whole content.
                // Move 65% of a viewport to keep overlap without hundreds of tiny scroll/read cycles.
                var view = scrolling.Current.VerticalViewSize;
                ControlPresentation.Check();
                if (before >= 0 && view > 0 && view < 100)
                {
                    var step = 65 * view / (100 - view);
                    scrolling.SetScrollPercent(-1, Math.Clamp(before + (reader.Direction == "down" ? step : -step), 0, 100));
                }
                else scrolling.Scroll(ScrollAmount.NoAmount, reader.Direction == "down" ? ScrollAmount.SmallIncrement : ScrollAmount.SmallDecrement);
                scrolled = true;
                reader.NeedsScroll = false;
                ControlPresentation.Delay(args.Number("pause_ms", 100, 50, 500));
                if (before >= 0 && scrolling.Current.VerticalScrollPercent == before) { reason = "scroll_stalled"; break; }
            }
            var page = ReadPage(entry.Element, 50000, clock, budget); pages++; nodes += page.Nodes;
            var overlap = reader.Direction == "down" ? PageOverlap(reader.Previous, page.Lines) : PageOverlap(page.Lines, reader.Previous);
            var delta = reader.Direction == "down" ? page.Lines.Skip(overlap) : page.Lines.Take(page.Lines.Length - overlap);
            var text = string.Join("\n", delta);
            reader.Pending = text.Length == 0 ? "" : text + "\n";
            reader.Previous = page.Lines; reader.Partial = !page.Complete;
            var percent = scrolling.Current.VerticalScrollPercent;
            reader.Boundary = percent < 0 || (reader.Direction == "down" ? percent >= 100 : percent <= 0);
            reader.NeedsScroll = true;
            reader.Reference = AddRef(entry.Window, entry.Element);
            if (page.Lines.Length == 0 && !reader.Boundary) { reader.Partial = true; }
        }
        var complete = reader.Boundary && !reader.Partial && reader.Pending.Length == 0;
        var recoverable = !reader.Partial;
        reader.Expires = DateTime.UtcNow.AddMinutes(2);
        if (complete || !recoverable) readers.Remove(id);
        return new { text = output.ToString(), complete, reader_id = complete || !recoverable ? null : id, root_ref = reader.Reference, stop_reason = reason, pages, nodes, direction = reader.Direction, order = reader.Direction == "down" ? "forward" : "backward pages; lines within each page remain forward", delivery = scrolled ? "sent" : "not_sent", source = "uia_text", image_count = 0, elapsed_ms = clock.ElapsedMilliseconds, scope = "exposed scroll container boundary only; virtualized or remotely loaded history may be incomplete", next = complete ? "verify the requested task result" : recoverable ? "continue with reader_id; a chunk limit is not task completion" : "partial UIA page: narrow root_ref or use browser DOM/capture fallback; do not claim full coverage" };
    }
}
