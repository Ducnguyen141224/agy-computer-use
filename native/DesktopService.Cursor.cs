using System.Drawing;
using System.IO;
using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private readonly Dictionary<string, CursorOverlay.Profile> cursorProfiles = new();
    private string cursorAgent = "default";
    private bool cursorRequireAgent;
    private void SelectCursorAgent(JsonElement args)
    {
        var selected = args.Text("agent", 80);
        if (selected == null) { if (cursorRequireAgent) throw new DesktopError("AGENT_REQUIRED", "This cursor session requires an explicit agent profile on desktop actions."); return; }
        if (!cursorProfiles.ContainsKey(selected)) throw new DesktopError("AGENT_NOT_FOUND", "Configure this agent's cursor profile first.");
        cursorAgent = selected; CursorOverlay.SelectAgent(selected);
    }
    private object CursorOperation(JsonElement args, string operation)
    {
        var agent = args.Text("agent", 80) ?? cursorAgent;
        if (operation == "cursor_status") return CursorStatus();
        if (operation == "cursor_remove")
        {
            cursorProfiles.Remove(agent);
            if (cursorProfiles.Count == 0) cursorRequireAgent = false;
            if (cursorAgent == agent) cursorAgent = cursorProfiles.Keys.FirstOrDefault() ?? "default";
            CursorOverlay.Set(cursorProfiles, cursorAgent); return CursorStatus();
        }
        if (!cursorProfiles.ContainsKey(agent) && cursorProfiles.Count >= 8) throw new DesktopError("CACHE_LIMIT", "Remove an old cursor profile before creating another.");
        var prior = cursorProfiles.GetValueOrDefault(agent) ?? new(Enabled: true, Label: agent);
        var options = args.TryGetProperty("cursor", out var value) ? value : JsonSerializer.SerializeToElement(new { });
        string ColorOption(string key, string fallback)
        {
            var color = options.Text(key, 7) ?? fallback;
            if (!System.Text.RegularExpressions.Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$") || color.Equals("#FF00FF", StringComparison.OrdinalIgnoreCase)) throw new DesktopError("INVALID_ARGUMENT", "Cursor colors use #RRGGBB; magenta is reserved for transparency.");
            return color;
        }
        var profile = prior with
        {
            Enabled = options.Flag("enabled", prior.Enabled), Style = options.Text("style") ?? prior.Style,
            Accent = ColorOption("accent", prior.Accent), Fill = ColorOption("fill", prior.Fill), Rim = ColorOption("rim", prior.Rim),
            Size = options.Number("size", prior.Size, 12, 128), Label = options.Text("label", 160) ?? prior.Label,
            Labels = options.Flag("labels", prior.Labels), MaxWords = options.Number("max_words", prior.MaxWords, 1, 20),
            Ripple = options.Flag("ripple", prior.Ripple), Highlight = options.Flag("highlight", prior.Highlight), Caret = options.Flag("caret", prior.Caret),
            TravelMs = options.Number("travel_ms", prior.TravelMs, 0, 2000), DwellMs = options.Number("dwell_ms", prior.DwellMs, 100, 10000),
            HotspotX = options.Number("hotspot_x", prior.HotspotX, 0, 127), HotspotY = options.Number("hotspot_y", prior.HotspotY, 0, 127),
            TravelMinMs = options.TryGetProperty("travel_min_ms", out var min) ? min.GetInt32() : prior.TravelMinMs,
            TravelMaxMs = options.TryGetProperty("travel_max_ms", out var max) ? max.GetInt32() : prior.TravelMaxMs,
            Bow = options.TryGetProperty("bow", out var bow) ? bow.GetDouble() : prior.Bow,
            Overshoot = options.TryGetProperty("overshoot", out var overshoot) ? overshoot.GetDouble() : prior.Overshoot,
            Tremor = options.TryGetProperty("tremor", out var tremor) ? tremor.GetDouble() : prior.Tremor,
            MotionSeed = options.Number("motion_seed", prior.MotionSeed, 0, int.MaxValue), PauseMs = options.Number("pause_ms", prior.PauseMs, 0, 300),
            PointerHotspotX = options.Number("pointer_hotspot_x", prior.PointerHotspotX, 0, 127), PointerHotspotY = options.Number("pointer_hotspot_y", prior.PointerHotspotY, 0, 127)
        };
        if (options.TryGetProperty("travel_ms", out _)) profile = profile with { TravelMinMs = null, TravelMaxMs = null };
        if (profile.TravelMinMs.HasValue != profile.TravelMaxMs.HasValue || profile.TravelMinMs > profile.TravelMaxMs) throw new DesktopError("INVALID_ARGUMENT", "Cursor travel range needs both min and max with min <= max.");
        if (profile.PauseMs > 0 && (profile.TravelMaxMs ?? profile.TravelMs) + profile.PauseMs > 700) throw new DesktopError("INVALID_ARGUMENT", "Cursor travel plus pre-action pause cannot exceed 700 ms.");
        if (options.Flag("clear_image")) profile = profile with { Png = null };
        if (options.Flag("clear_pointer_image")) profile = profile with { PointerPng = null };
        byte[]? LoadPng(string? path, string? encoded)
        {
            if (path != null && encoded != null) throw new DesktopError("INVALID_ARGUMENT", "Choose an image path or inline PNG, not both.");
            if (path == null && encoded == null) return null;
            byte[] png;
            if (path != null)
            {
                if (!Path.IsPathFullyQualified(path)) throw new DesktopError("INVALID_ARGUMENT", "Custom cursor image needs an absolute PNG path.");
                using var file = File.OpenRead(path);
                if (file.Length > 2097152) throw new DesktopError("INVALID_ARGUMENT", "Cursor PNG cannot exceed 2 MiB.");
                using var bytes = new MemoryStream(); file.CopyTo(bytes); png = bytes.ToArray();
            }
            else { try { png = Convert.FromBase64String(encoded!); } catch (FormatException) { throw new DesktopError("INVALID_ARGUMENT", "Inline cursor PNG is not valid base64."); } }
            using var memory = new MemoryStream(png);
            using var image = Image.FromStream(memory);
            if (image.RawFormat.Guid != System.Drawing.Imaging.ImageFormat.Png.Guid || image.Width > 1024 || image.Height > 1024) throw new DesktopError("INVALID_ARGUMENT", "Custom cursor needs a PNG at most 1024 by 1024 pixels.");
            return png;
        }
        if (LoadPng(args.Text("image_path", 2048), options.Text("png_base64", 60000)) is byte[] normal) profile = profile with { Png = normal };
        if (LoadPng(options.Text("pointer_image_path", 2048), options.Text("pointer_png_base64", 60000)) is byte[] pointer) profile = profile with { PointerPng = pointer };
        if (profile.HotspotX >= profile.Size || profile.HotspotY >= profile.Size || profile.PointerHotspotX >= profile.Size || profile.PointerHotspotY >= profile.Size) throw new DesktopError("INVALID_ARGUMENT", "Cursor hotspots must be within their displayed size.");
        var next = new Dictionary<string, CursorOverlay.Profile>(cursorProfiles) { [agent] = profile };
        CursorOverlay.Set(next, agent);
        cursorProfiles[agent] = profile; cursorAgent = agent;
        cursorRequireAgent = options.Flag("multi_agent", cursorRequireAgent);
        return CursorStatus();
    }
    private object CursorStatus() => new { renderer = CursorOverlay.RenderState(), active_agent = cursorAgent, multi_agent = cursorRequireAgent, visible = cursorProfiles.Values.Any(item => item.Enabled), profiles = cursorProfiles.Select(pair => new { agent = pair.Key, enabled = pair.Value.Enabled, style = pair.Value.Style, ripple = pair.Value.Ripple, highlight = pair.Value.Highlight, caret = pair.Value.Caret, accent = pair.Value.Accent, fill = pair.Value.Fill, rim = pair.Value.Rim, size = pair.Value.Size, label = pair.Value.Label, labels = pair.Value.Labels, max_words = pair.Value.MaxWords, travel_ms = pair.Value.TravelMs, travel_min_ms = pair.Value.TravelMinMs, travel_max_ms = pair.Value.TravelMaxMs, bow = pair.Value.Bow, overshoot = pair.Value.Overshoot, tremor = pair.Value.Tremor, motion_seed = pair.Value.MotionSeed, pause_ms = pair.Value.PauseMs, pointer_png = pair.Value.PointerPng != null, pointer_hotspot_x = pair.Value.PointerHotspotX, pointer_hotspot_y = pair.Value.PointerHotspotY, dwell_ms = pair.Value.DwellMs, custom_png = pair.Value.Png != null, hotspot_x = pair.Value.HotspotX, hotspot_y = pair.Value.HotspotY }).ToArray(), scope = "current RAM session; presentation only, click-through, never takes focus", caret_marker = "actual Windows GUI caret when exposed; omitted when unavailable" };
}
