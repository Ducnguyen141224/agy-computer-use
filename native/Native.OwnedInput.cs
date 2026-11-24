using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AgyComputerUse;

internal static partial class Native
{
    internal static readonly UIntPtr InputTag = new(0x41475943u);
    private static readonly ConcurrentDictionary<string, Input> HeldInput = new();
    internal static void ReleaseOwnedInput()
    {
        foreach (var pair in HeldInput.ToArray())
            if (HeldInput.TryRemove(pair.Key, out var release)) SendInputNative(1, [release], Marshal.SizeOf<Input>());
    }
    internal static uint SendInput(uint count, Input[] events, int size)
    {
        var presses = events.Any(e => e.Type == 1 ? (e.Data.Keyboard.Flags & 2) == 0 : (e.Data.Mouse.Flags & (2 | 8 | 32 | 0x0800 | 0x1000 | 1)) != 0);
        if (presses) ControlPresentation.Check();
        for (var i = 0; i < events.Length; i++)
        {
            if (events[i].Type == 1)
            {
                events[i].Data.Keyboard.ExtraInfo = InputTag;
                var key = events[i].Data.Keyboard;
                var identity = $"key:{key.Vk}:{key.Scan}:{key.Flags & ~2u}";
                if ((key.Flags & 2) == 0) { var release = events[i]; release.Data.Keyboard.Flags |= 2; HeldInput[identity] = release; }
            }
            else
            {
                events[i].Data.Mouse.ExtraInfo = InputTag;
                foreach (var down in new uint[] { 2, 8, 32 })
                    if ((events[i].Data.Mouse.Flags & down) != 0) { var release = events[i]; release.Data.Mouse.Flags = down * 2; HeldInput[$"mouse:{down}"] = release; }
            }
        }
        var sent = SendInputNative(count, events, size);
        foreach (var e in events.Take((int)sent))
        {
            if (e.Type == 1 && (e.Data.Keyboard.Flags & 2) != 0) { var key = e.Data.Keyboard; HeldInput.TryRemove($"key:{key.Vk}:{key.Scan}:{key.Flags & ~2u}", out _); }
            if (e.Type == 0) foreach (var down in new uint[] { 2, 8, 32 }) if ((e.Data.Mouse.Flags & (down * 2)) != 0) HeldInput.TryRemove($"mouse:{down}", out _);
        }
        return sent;
    }
}
