using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace RoboMouse.App;

public enum DialogResult { None, OK, Cancel, Yes, No }

public enum DialogButtons { OK, YesNo, YesNoCancel }

public enum DialogIcon { None, Information, Question, Warning, Error }

/// <summary>
/// Message boxes. Avalonia has none built in; this is a small modal window styled like the rest of
/// the app. With no owner window the dialog is shown standalone (used from the tray menu).
/// </summary>
internal static class Dialogs
{
    public static Task<DialogResult> ShowAsync(Window? owner, string text, string title = "RoboMouse",
        DialogButtons buttons = DialogButtons.OK, DialogIcon icon = DialogIcon.None)
    {
        var dialog = new MessageWindow(text, title, buttons, icon);
        if (owner != null && owner.IsVisible)
            return dialog.ShowDialog<DialogResult>(owner);

        var completion = new TaskCompletionSource<DialogResult>();
        dialog.Closed += (_, _) => completion.TrySetResult(dialog.Result);
        dialog.Show();
        return completion.Task;
    }

    public static Task InfoAsync(Window? owner, string text) => ShowAsync(owner, text, icon: DialogIcon.Information);
    public static Task WarnAsync(Window? owner, string text) => ShowAsync(owner, text, icon: DialogIcon.Warning);
    public static Task ErrorAsync(Window? owner, string text) => ShowAsync(owner, text, icon: DialogIcon.Error);

    public static async Task<bool> ConfirmAsync(Window? owner, string text, string title = "RoboMouse") =>
        await ShowAsync(owner, text, title, DialogButtons.YesNo, DialogIcon.Question) == DialogResult.Yes;

    private sealed class MessageWindow : Window
    {
        public DialogResult Result { get; private set; } = DialogResult.Cancel;

        public MessageWindow(string text, string title, DialogButtons buttons, DialogIcon icon)
        {
            Ui.Style(this);
            Title = title;
            Width = 440;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            CanMinimize = false;
            CanMaximize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            Topmost = true;

            var (glyph, colour) = icon switch
            {
                DialogIcon.Information => ("i", Ui.Accent),
                DialogIcon.Question => ("?", Ui.Accent),
                DialogIcon.Warning => ("!", Ui.Orange),
                DialogIcon.Error => ("×", Ui.Red),
                _ => ("", Ui.Grey)
            };

            var body = new DockPanel { LastChildFill = true };

            var content = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Ui.Pad), Spacing = 14 };
            if (glyph.Length > 0)
            {
                content.Children.Add(new Border
                {
                    Width = 32,
                    Height = 32,
                    CornerRadius = new CornerRadius(16),
                    Background = new SolidColorBrush(colour),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = glyph,
                        Foreground = Brushes.White,
                        FontWeight = FontWeight.Bold,
                        FontSize = 16,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    }
                });
            }
            content.Children.Add(new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Ui.TextBrush
            });

            var buttonList = new List<Button>();
            void Add(string caption, DialogResult result, bool primary, bool cancel = false)
            {
                var b = primary ? Ui.PrimaryButton(caption, 96) : Ui.Button(caption, 96);
                b.Click += (_, _) => { Result = result; Close(result); };
                b.IsDefault = primary;
                b.IsCancel = cancel;
                buttonList.Add(b);
            }

            switch (buttons)
            {
                case DialogButtons.OK:
                    Add("OK", DialogResult.OK, primary: true, cancel: true);
                    break;
                case DialogButtons.YesNo:
                    Add("Yes", DialogResult.Yes, primary: true);
                    Add("No", DialogResult.No, primary: false, cancel: true);
                    break;
                case DialogButtons.YesNoCancel:
                    Add("Yes", DialogResult.Yes, primary: true);
                    Add("No", DialogResult.No, primary: false);
                    Add("Cancel", DialogResult.Cancel, primary: false, cancel: true);
                    break;
            }

            body.Children.Add(Ui.ActionBar(buttonList.ToArray()));
            body.Children.Add(content);
            Content = body;
        }
    }
}
