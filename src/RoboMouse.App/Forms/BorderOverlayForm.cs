namespace RoboMouse.App.Forms;

/// <summary>
/// A transparent overlay form that displays a colored border around the screen
/// to indicate when the mouse has entered from a remote machine.
/// </summary>
public class BorderOverlayForm : Form
{
    private readonly int _borderThickness;
    private Color _borderColor;
    private System.Windows.Forms.Timer? _fadeTimer;
    private float _opacity = 1.0f;
    private bool _isFadingOut;

    public BorderOverlayForm(int borderThickness = 4, Color? borderColor = null)
    {
        _borderThickness = borderThickness;
        _borderColor = borderColor ?? Color.FromArgb(0, 162, 255); // Nice blue color

        InitializeForm();
    }

    private void InitializeForm()
    {
        // Make the form cover the entire virtual screen (all monitors)
        var screen = Screen.PrimaryScreen!;

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = screen.Bounds.Location;
        Size = screen.Bounds.Size;

        // Make it a layered window for transparency
        BackColor = Color.Black;
        TransparencyKey = Color.Black;

        // Topmost but don't steal focus
        TopMost = true;
        ShowInTaskbar = false;

        // Click-through - allow mouse events to pass through
        SetClickThrough();

        // Enable double buffering for smooth drawing
        DoubleBuffered = true;

        // Custom paint
        Paint += OnPaint;
    }

    private void SetClickThrough()
    {
        // Make the window click-through using extended window styles
        var initialStyle = GetWindowLong(Handle, GWL_EXSTYLE);
        SetWindowLong(Handle, GWL_EXSTYLE, initialStyle | WS_EX_LAYERED | WS_EX_TRANSPARENT);
    }

    private void OnPaint(object? sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.Black); // Transparent via TransparencyKey

        // Draw border with current opacity
        var color = Color.FromArgb((int)(255 * _opacity), _borderColor);
        using var pen = new Pen(color, _borderThickness);

        // Draw border inside the form bounds
        var rect = new Rectangle(
            _borderThickness / 2,
            _borderThickness / 2,
            Width - _borderThickness,
            Height - _borderThickness);

        g.DrawRectangle(pen, rect);
    }

    /// <summary>
    /// Shows the border overlay with optional fade-in effect.
    /// </summary>
    public void ShowBorder(bool fadeIn = false)
    {
        _isFadingOut = false;
        _opacity = 1.0f;

        // Update to cover primary screen
        var screen = Screen.PrimaryScreen!;
        Location = screen.Bounds.Location;
        Size = screen.Bounds.Size;

        Show();
        Invalidate();
    }

    /// <summary>
    /// Hides the border overlay with optional fade-out effect.
    /// </summary>
    public void HideBorder(bool fadeOut = true)
    {
        if (fadeOut)
        {
            StartFadeOut();
        }
        else
        {
            Hide();
        }
    }

    private void StartFadeOut()
    {
        if (_isFadingOut)
            return;

        _isFadingOut = true;
        _fadeTimer?.Dispose();
        _fadeTimer = new System.Windows.Forms.Timer
        {
            Interval = 16 // ~60fps
        };
        _fadeTimer.Tick += OnFadeTimerTick;
        _fadeTimer.Start();
    }

    private void OnFadeTimerTick(object? sender, EventArgs e)
    {
        _opacity -= 0.05f; // Fade out over ~300ms

        if (_opacity <= 0)
        {
            _fadeTimer?.Stop();
            _fadeTimer?.Dispose();
            _fadeTimer = null;
            _isFadingOut = false;
            _opacity = 0;
            Hide();
        }

        Invalidate();
    }

    /// <summary>
    /// Sets the border color.
    /// </summary>
    public void SetBorderColor(Color color)
    {
        _borderColor = color;
        Invalidate();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_NOACTIVATE - prevent the form from becoming the foreground window
            cp.ExStyle |= WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override bool ShowWithoutActivation => true;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _fadeTimer?.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Native Methods

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    #endregion
}
