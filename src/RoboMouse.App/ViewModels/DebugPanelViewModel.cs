using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// Data structure for debug panel updates.
/// </summary>
public class MouseDebugData
{
    public bool IsControlling { get; set; }
    public string? PeerName { get; set; }
    public string? PeerPosition { get; set; }
    public int DeltaX { get; set; }
    public int DeltaY { get; set; }
    public int RoundTripMs { get; set; }
}

/// <summary>
/// Motion and latency readout while controlling a remote. Samples arrive from any thread at up to
/// 1000 Hz; <see cref="Flush"/> publishes them to the bound properties on the UI thread on a timer.
/// </summary>
public sealed partial class DebugPanelViewModel : ObservableObject
{
    private readonly object _lock = new();
    private readonly List<string> _history = new(12);
    private readonly IDialogService? _dialogs;

    private MouseDebugData? _latest;
    private int _samplesSinceTick;
    private int _ticksSinceRateUpdate;
    private long _totalDx, _totalDy;
    private int _lastDx, _lastDy;
    private bool _dirty;

    [ObservableProperty] private string _status = "Idle";
    [ObservableProperty] private bool _isControlling;
    [ObservableProperty] private string _peer = "None";
    [ObservableProperty] private string _position = "-";
    [ObservableProperty] private string _roundTrip = "-";
    [ObservableProperty] private StatusTone _roundTripTone = StatusTone.Idle;
    [ObservableProperty] private string _rate = "0 /s";
    [ObservableProperty] private string _lastDelta = "(0, 0)";
    [ObservableProperty] private string _totalDelta = "(0, 0)";
    [ObservableProperty] private int _directionX;
    [ObservableProperty] private int _directionY;

    public ObservableCollection<string> History { get; } = new();

    public DebugPanelViewModel(IDialogService? dialogs = null) => _dialogs = dialogs;

    /// <summary>Records a motion sample. Safe to call from any thread at any rate.</summary>
    public void Record(MouseDebugData data)
    {
        lock (_lock)
        {
            _latest = data;
            _dirty = true;

            if (data.DeltaX != 0 || data.DeltaY != 0)
            {
                _samplesSinceTick++;
                _totalDx += data.DeltaX;
                _totalDy += data.DeltaY;
                _lastDx = data.DeltaX;
                _lastDy = data.DeltaY;

                _history.Insert(0, $"{Arrow(data.DeltaX, data.DeltaY)} ({data.DeltaX,4:+0;-0;0},{data.DeltaY,4:+0;-0;0})  rtt {FormatRtt(data.RoundTripMs)}");
                if (_history.Count > 12)
                    _history.RemoveAt(12);
            }
        }
    }

    /// <summary>Pushes pending samples into the bound properties. Call on the UI thread, about 10x a second.</summary>
    public void Flush()
    {
        MouseDebugData? data;
        List<string> history;
        int rate;
        lock (_lock)
        {
            _ticksSinceRateUpdate++;
            if (_ticksSinceRateUpdate >= 10)
            {
                rate = _samplesSinceTick;
                _samplesSinceTick = 0;
                _ticksSinceRateUpdate = 0;
                Rate = $"{rate} /s";
                _dirty = true;
            }
            if (!_dirty)
                return;
            _dirty = false;

            data = _latest;
            history = new List<string>(_history);
            LastDelta = $"({_lastDx:+0;-0;0}, {_lastDy:+0;-0;0})";
            TotalDelta = $"({_totalDx}, {_totalDy})";
            DirectionX = _lastDx;
            DirectionY = _lastDy;
        }

        IsControlling = data?.IsControlling ?? false;
        Status = IsControlling ? "Controlling" : "Idle";
        Peer = data?.PeerName ?? "None";
        Position = data?.PeerPosition ?? "-";
        var rtt = data?.RoundTripMs ?? -1;
        RoundTrip = FormatRtt(rtt);
        RoundTripTone = rtt switch { < 0 => StatusTone.Idle, < 5 => StatusTone.Ok, < 20 => StatusTone.Warning, _ => StatusTone.Error };

        History.Clear();
        foreach (var line in history)
            History.Add(line);
    }

    [RelayCommand]
    private async Task CopyAsync()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("=== RoboMouse Debug ===");
        sb.AppendLine($"Status: {Status}");
        sb.AppendLine($"Peer: {Peer}");
        sb.AppendLine($"Position: {Position}");
        sb.AppendLine($"Round trip: {RoundTrip}");
        sb.AppendLine($"Motion rate: {Rate}");
        sb.AppendLine($"Last: {LastDelta}");
        sb.AppendLine($"Total: {TotalDelta}");
        sb.AppendLine();
        sb.AppendLine("=== Recent samples ===");
        lock (_lock)
        {
            foreach (var entry in _history)
                sb.AppendLine(entry);
        }
        if (_dialogs != null)
            await _dialogs.CopyTextAsync(sb.ToString());
    }

    private static string FormatRtt(int rtt) => rtt < 0 ? "-" : $"{rtt} ms";

    private static string Arrow(int dx, int dy)
    {
        if (dx == 0 && dy == 0)
            return "·";
        var angle = Math.Atan2(dy, dx) * 180 / Math.PI;
        return angle switch
        {
            >= -22.5 and < 22.5 => "→",
            >= 22.5 and < 67.5 => "↘",
            >= 67.5 and < 112.5 => "↓",
            >= 112.5 and < 157.5 => "↙",
            >= 157.5 or < -157.5 => "←",
            >= -157.5 and < -112.5 => "↖",
            >= -112.5 and < -67.5 => "↑",
            >= -67.5 and < -22.5 => "↗",
            _ => "?"
        };
    }
}
