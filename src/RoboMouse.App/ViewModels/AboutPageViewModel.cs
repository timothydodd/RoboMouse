using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RoboMouse.App.Services;
using RoboMouse.Core.Logging;

namespace RoboMouse.App.ViewModels;

/// <summary>About page: version, update check, and the log folder / diagnostics export for bug reports.</summary>
public sealed partial class AboutPageViewModel : PageViewModel
{
    private readonly IDialogService _dialogs;
    private readonly AppState _state;
    private readonly UpdateChecker? _updates;
    private readonly Func<DiagnosticsSources> _diagnostics;
    private readonly Version _version;

    public string Version { get; }

    /// <summary>False in the Store build, which the Store keeps up to date: the update card is hidden.</summary>
    public bool CanCheckForUpdates => _updates != null;

    [ObservableProperty] private bool _checkForUpdates;
    [ObservableProperty] private string _updateStatus;
    [ObservableProperty] private bool _isChecking;

    /// <summary>The release page of a newer version found by "Check now", or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate))]
    private string? _updatePageUrl;

    public bool HasUpdate => UpdatePageUrl != null;

    public AboutPageViewModel(IDialogService dialogs, AppState state, UpdateChecker? updates, Func<DiagnosticsSources> diagnostics, Version version)
    {
        _dialogs = dialogs;
        _state = state;
        _updates = updates;
        _diagnostics = diagnostics;
        _version = version;
        Version = version.ToString(3);
        _checkForUpdates = state.CheckForUpdates;
        _updateStatus = state.LastUpdateCheckUtc is { } last
            ? $"Last checked {last.ToLocalTime():g}."
            : "Not checked yet.";
    }

    /// <summary>Writes the page's settings into the app state. The caller saves it.</summary>
    internal void Save() => _state.CheckForUpdates = CheckForUpdates;

    [RelayCommand]
    private async Task CheckNowAsync()
    {
        if (_updates == null || IsChecking)
            return;
        IsChecking = true;
        UpdateStatus = "Checking…";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var update = await _updates.CheckAsync(_version, cts.Token);
            _state.LastUpdateCheckUtc = DateTime.UtcNow;
            _state.Save();
            UpdatePageUrl = update?.PageUrl;
            UpdateStatus = update == null
                ? $"RoboMouse {Version} is the latest version."
                : $"RoboMouse {update.Version} is available.";
        }
        catch (Exception ex)
        {
            SimpleLogger.Log("Update", $"Check failed: {ex.Message}");
            UpdateStatus = "Could not reach GitHub to check for updates. Try again later.";
        }
        finally
        {
            IsChecking = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage() => _dialogs.Open(UpdatePageUrl ?? UpdateChecker.ReleasesPage);

    [RelayCommand]
    private void OpenLogFolder() => _dialogs.Open(_diagnostics().DataFolder);

    [RelayCommand]
    private async Task ExportDiagnosticsAsync()
    {
        var path = await _dialogs.PickSaveFileAsync("Export diagnostics", Diagnostics.SuggestedFileName(DateTime.Now), "zip");
        if (path == null)
            return;
        try
        {
            var sources = _diagnostics();
            if (File.Exists(path))
                File.Delete(path);
            await Task.Run(() => Diagnostics.Export(path, sources));
            await _dialogs.InfoAsync($"Diagnostics saved to {path}.\n\nIt holds the logs, the settings with the pairing code removed, and this PC's version and monitor layout. Attach it to a bug report.");
        }
        catch (Exception ex)
        {
            await _dialogs.ErrorAsync($"Could not save the diagnostics: {ex.Message}");
        }
    }
}
