using Avalonia.Controls;
using RoboMouse.App.Services;

namespace RoboMouse.App.Views;

/// <summary>A message box: title, text and OK / Yes-No / Yes-No-Cancel buttons.</summary>
public partial class MessageDialog : Window
{
    public DialogResult Result { get; private set; } = DialogResult.Cancel;

    public MessageDialog() : this("", "RoboMouse", DialogButtons.OK, DialogIcon.None)
    {
    }

    public MessageDialog(string text, string title, DialogButtons buttons, DialogIcon icon)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = icon switch
        {
            DialogIcon.Warning => "Check this",
            DialogIcon.Error => "Something went wrong",
            DialogIcon.Question => "Confirm",
            _ => title
        };
        MessageText.Text = text;

        void Add(string caption, DialogResult result, bool primary, bool cancel = false)
        {
            var button = new Button { Content = caption, IsDefault = primary, IsCancel = cancel };
            if (primary)
                button.Classes.Add("accent");
            button.Click += (_, _) => { Result = result; Close(); };
            Buttons.Children.Add(button);
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
    }
}
