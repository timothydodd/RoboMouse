namespace RoboMouse.Contracts;

/// <summary>
/// Keys and mouse buttons that injection commands have pressed and not yet released. Both the service
/// (so a replacement helper can let go of what a dead one left down) and the helper (so it lets go when
/// its pipe closes) track what they relay or apply through this. Not thread-safe.
/// </summary>
public sealed class HeldPipeInput
{
    private readonly Dictionary<int, (uint Scan, bool Extended)> _keys = new();
    private readonly HashSet<int> _buttons = new();

    public bool IsEmpty => _keys.Count == 0 && _buttons.Count == 0;

    /// <summary>Records the effect of one well-formed injection command; other opcodes are ignored.</summary>
    public void Track(PipeMessage message)
    {
        switch (message.Opcode)
        {
            case PipeOpcode.InjectKey:
            {
                var (vk, scan, eventType, extended) = message.ReadKey();
                if (eventType is PipeInput.KeyUp or PipeInput.SysKeyUp)
                    _keys.Remove(vk);
                else
                    _keys[vk] = (scan, extended);
                break;
            }
            case PipeOpcode.InjectButton:
            {
                var eventType = message.ReadButton().eventType;
                if (PipeInput.ReleaseOf(eventType) != 0)
                    _buttons.Add(eventType);
                else if (PipeInput.PressOf(eventType) is var press and not 0)
                    _buttons.Remove(press);
                break;
            }
        }
    }

    /// <summary>The key-up and button-up commands that release everything held, then forgets it all.</summary>
    public List<PipeMessage> TakeReleases()
    {
        var releases = new List<PipeMessage>(_keys.Count + _buttons.Count);
        foreach (var (vk, (scan, extended)) in _keys)
            releases.Add(PipeMessage.Key(vk, scan, PipeInput.KeyUp, extended));
        foreach (var button in _buttons)
            releases.Add(PipeMessage.Button(PipeOpcode.InjectButton, PipeInput.ReleaseOf(button), 0));
        _keys.Clear();
        _buttons.Clear();
        return releases;
    }
}
