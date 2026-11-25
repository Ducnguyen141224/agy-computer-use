using System.Text.Json;

namespace AgyComputerUse;

internal sealed partial class DesktopService
{
    private string currentSession = "default";
    private sealed record SessionState(Dictionary<string, WindowEntry> Windows, Dictionary<string, ElementEntry> Refs, Dictionary<string, Observation> Observations, Dictionary<string, CaptureEntry> Captures, Dictionary<string, Native.Rect> Geometry, Dictionary<string, System.Windows.Forms.IDataObject> Clipboard, Dictionary<string, CursorOverlay.Profile> CursorProfiles, string CursorAgent, bool CursorRequireAgent);
    private readonly Dictionary<string, SessionState> sessions = new();
    private object Session(JsonElement args)
    {
        var operation = args.Text("operation") ?? "info";
        if (operation == "info") return new { session_id = currentSession, refs = references.Count, snapshots = observations.Count, memory_only = true };
        if (operation == "list") return new { current = currentSession, sessions = sessions.Keys.Append(currentSession).Distinct().ToArray(), memory_only = true };
        if (operation == "reset")
        {
            ClearDesktopState();
            return new { session_id = currentSession, reset = true };
        }
        if (operation == "end")
        {
            var id = args.Text("session_id") ?? currentSession;
            if (id != currentSession) { if (!sessions.Remove(id)) throw new DesktopError("SESSION_NOT_FOUND", "Session does not exist."); return new { ended = id }; }
            ClearDesktopState();
            sessions.Remove(id);
            currentSession = "default";
            if (sessions.Remove(currentSession, out var state)) RestoreState(state);
            return new { ended = id, session_id = currentSession };
        }
        if (operation is not ("start" or "switch")) throw new DesktopError("INVALID_ARGUMENT", "Unknown session operation.");
        if (sessions.Count >= 8 && operation == "start") throw new DesktopError("CACHE_LIMIT", "End an old session before starting another.");
        var next = operation == "start" ? $"session-{Guid.NewGuid():N}" : args.Text("session_id") ?? throw new DesktopError("INVALID_ARGUMENT", "session_id is required.");
        if (next == currentSession) return new { session_id = currentSession, memory_only = true };
        SessionState? saved = null;
        if (operation == "switch" && !sessions.TryGetValue(next, out saved)) throw new DesktopError("SESSION_NOT_FOUND", "Session does not exist in this server process.");
        sessions[currentSession] = new(new(windows), new(references), new(observations), new(captures), new(windowGeometry), new(clipboardSnapshots), new(cursorProfiles), cursorAgent, cursorRequireAgent);
        ClearDesktopState();
        if (saved != null) { RestoreState(saved); sessions.Remove(next); }
        currentSession = next;
        return new { session_id = currentSession, memory_only = true };
    }
    private void ClearDesktopState()
    {
        readers.Clear(); ControlPresentation.EndTask();
        windows.Clear(); references.Clear(); observations.Clear(); captures.Clear(); windowGeometry.Clear();
        clipboardSnapshots.Clear();
        cursorProfiles.Clear(); cursorAgent = "default"; cursorRequireAgent = false; CursorOverlay.Set(cursorProfiles, cursorAgent);
        notificationIdentities.Clear();
        ClearWatches();
    }
    private void RestoreState(SessionState state)
    {
        foreach (var pair in state.Windows) windows[pair.Key] = pair.Value;
        foreach (var pair in state.Refs) references[pair.Key] = pair.Value;
        foreach (var pair in state.Observations) observations[pair.Key] = pair.Value;
        foreach (var pair in state.Captures) captures[pair.Key] = pair.Value;
        foreach (var pair in state.Geometry) windowGeometry[pair.Key] = pair.Value;
        foreach (var pair in state.Clipboard) clipboardSnapshots[pair.Key] = pair.Value;
        foreach (var pair in state.CursorProfiles) cursorProfiles[pair.Key] = pair.Value;
        cursorAgent = state.CursorAgent; cursorRequireAgent = state.CursorRequireAgent; CursorOverlay.Set(cursorProfiles, cursorAgent);
    }
}
