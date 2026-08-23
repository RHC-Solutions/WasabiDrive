using System.Windows;
using System.Windows.Input;

namespace WasabiDrive.App.Views;

/// <summary>A single-line text prompt, used to collect the destination folder for a bulk move.</summary>
public partial class PromptWindow : Window
{
    private PromptWindow(string title, string prompt, string hint, string initialValue)
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        HintText.Text = hint;
        InputBox.Text = initialValue;
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    /// <summary>Shows the prompt and returns the entered text, or null if it was cancelled.</summary>
    public static string? Show(string title, string prompt, string hint, string initialValue = "")
    {
        var window = new PromptWindow(title, prompt, hint, initialValue);
        return window.ShowDialog() == true ? window.InputBox.Text.Trim() : null;
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            DialogResult = true;
    }
}
