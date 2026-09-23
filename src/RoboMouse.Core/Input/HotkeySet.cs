using RoboMouse.Core.Configuration;

namespace RoboMouse.Core.Input;

/// <summary>What a global hotkey does.</summary>
public enum HotkeyAction
{
    /// <summary>Releases control of another screen, or turns sharing on or off.</summary>
    ToggleSharing,

    /// <summary>Locks the cursor to the screen it is on, or unlocks it.</summary>
    LockCursor,

    /// <summary>Locks this PC and asks the connected peers to lock too.</summary>
    LockAll,

    /// <summary>Moves the cursor onto one peer's screen (<see cref="HotkeyBinding.PeerId"/>).</summary>
    JumpToPeer
}

/// <summary>One global hotkey and what it does.</summary>
public sealed record HotkeyBinding(Hotkey Hotkey, HotkeyAction Action, string? PeerId = null);

/// <summary>
/// Every global hotkey, parsed once from the settings so the keyboard hook only compares. Immutable:
/// the service swaps in a new set when the settings change. When two bindings share a chord the first
/// wins, and the toggle hotkey is always first, so the way back from a stuck screen cannot be shadowed.
/// </summary>
public sealed class HotkeySet
{
    /// <summary>How many peers get a jump hotkey by default (Ctrl+Alt+F1 to F4).</summary>
    public const int DefaultJumpCount = 4;

    public static readonly HotkeySet Empty = new(Array.Empty<HotkeyBinding>());

    public IReadOnlyList<HotkeyBinding> Bindings { get; }

    public HotkeySet(IReadOnlyList<HotkeyBinding> bindings)
    {
        Bindings = bindings;
    }

    /// <summary>Builds the set from the settings. Invalid or empty chords are left out.</summary>
    public static HotkeySet From(AppSettings settings)
    {
        var bindings = new List<HotkeyBinding>();
        void Add(string? text, HotkeyAction action, string? peerId = null)
        {
            if (Hotkey.Parse(text) is { } hotkey)
                bindings.Add(new HotkeyBinding(hotkey, action, peerId));
        }

        Add(settings.ToggleHotkey, HotkeyAction.ToggleSharing);
        Add(settings.LockCursorHotkey, HotkeyAction.LockCursor);
        Add(settings.LockAllHotkey, HotkeyAction.LockAll);
        var peers = settings.Peers.ToList();
        for (var i = 0; i < peers.Count; i++)
        {
            if (peers[i].Enabled)
                Add(EffectiveJumpHotkey(peers[i], i), HotkeyAction.JumpToPeer, peers[i].Id);
        }
        return new HotkeySet(bindings);
    }

    /// <summary>The default jump hotkey for the peer at this place in the list, or null past the first four.</summary>
    public static string? DefaultJumpHotkey(int index) =>
        index is >= 0 and < DefaultJumpCount ? $"Ctrl+Alt+F{index + 1}" : null;

    /// <summary>The jump hotkey a peer uses: its own, or the default for its place when it never set one.</summary>
    public static string? EffectiveJumpHotkey(PeerConfig peer, int index) =>
        peer.JumpHotkey ?? DefaultJumpHotkey(index);

    /// <summary>The jump hotkey a configured peer uses (see <see cref="EffectiveJumpHotkey(PeerConfig, int)"/>).</summary>
    public static string? EffectiveJumpHotkey(AppSettings settings, PeerConfig peer)
    {
        var index = settings.Peers.ToList().IndexOf(peer);
        return peer.JumpHotkey ?? DefaultJumpHotkey(index < 0 ? settings.Peers.Count : index);
    }

    /// <summary>True when some binding uses this key, so a key press that is not one can skip the rest.</summary>
    public bool UsesKey(Keys key)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Hotkey.Key == key)
                return true;
        }
        return false;
    }

    /// <summary>The binding for a key press with these modifiers, or null.</summary>
    public HotkeyBinding? Find(Keys key, ModifierState modifiers)
    {
        foreach (var binding in Bindings)
        {
            if (binding.Hotkey.Matches(key, modifiers))
                return binding;
        }
        return null;
    }

    /// <summary>
    /// The first pair of named hotkeys that are the same chord, as a sentence for the user, or null
    /// when there is none. Empty and invalid entries are skipped.
    /// </summary>
    public static string? FindConflict(IEnumerable<(string Name, string? Hotkey)> hotkeys)
    {
        var seen = new Dictionary<Hotkey, string>();
        foreach (var (name, text) in hotkeys)
        {
            if (Hotkey.Parse(text) is not { } hotkey)
                continue;
            if (seen.TryGetValue(hotkey, out var other))
                return $"{other} and {name} both use {hotkey}. Give one of them another hotkey.";
            seen[hotkey] = name;
        }
        return null;
    }
}
