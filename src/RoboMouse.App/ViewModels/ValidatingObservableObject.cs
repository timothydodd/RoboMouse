using System.Collections;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace RoboMouse.App.ViewModels;

/// <summary>
/// A view model whose fields can carry an error message. Bound controls show it through
/// <see cref="INotifyDataErrorInfo"/> (no reflection, unlike DataAnnotations), and Save checks
/// <see cref="HasErrors"/> instead of quietly keeping an old value.
/// </summary>
public abstract class ValidatingObservableObject : ObservableObject, INotifyDataErrorInfo
{
    private readonly Dictionary<string, string> _errors = new();

    public bool HasErrors => _errors.Count > 0;

    public event EventHandler<DataErrorsChangedEventArgs>? ErrorsChanged;

    public IEnumerable GetErrors(string? propertyName) =>
        propertyName != null && _errors.TryGetValue(propertyName, out var error) ? new[] { error } : Array.Empty<string>();

    /// <summary>Sets or (with null) clears the error shown on a property.</summary>
    protected void SetError(string propertyName, string? error)
    {
        var changed = error == null
            ? _errors.Remove(propertyName)
            : !_errors.TryGetValue(propertyName, out var old) || old != error;
        if (error != null)
            _errors[propertyName] = error;
        if (!changed)
            return;
        ErrorsChanged?.Invoke(this, new DataErrorsChangedEventArgs(propertyName));
        OnPropertyChanged(nameof(HasErrors));
    }

    /// <summary>
    /// A cleared number box sets its value to null; flag it rather than keep the previous number.
    /// </summary>
    protected void RequireValue(decimal? value, string propertyName, string message) =>
        SetError(propertyName, value == null ? message : null);
}
