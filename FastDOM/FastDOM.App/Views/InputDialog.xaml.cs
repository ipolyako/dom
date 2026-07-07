using System.Globalization;
using System.Windows;
using System.Windows.Input;

namespace FastDOM.App.Views;

public partial class InputDialog : Window
{
    public decimal? Value { get; private set; }

    public InputDialog(string prompt, string title = "FastDOM")
    {
        InitializeComponent();
        Title = title;
        PromptLabel.Text = prompt;
        Loaded += (_, _) => { ValueBox.Focus(); ValueBox.SelectAll(); };
    }

    private void OK_Click(object sender, RoutedEventArgs e) => TryAccept();

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        Value = null;
        DialogResult = false;
    }

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) { e.Handled = true; TryAccept(); }
    }

    private void ValueBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ErrorLabel.Visibility = Visibility.Collapsed;
    }

    private void TryAccept()
    {
        var text = ValueBox.Text.Trim().Replace(",", ".");
        if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) && v > 0)
        {
            Value = v;
            DialogResult = true;
        }
        else
        {
            ErrorLabel.Text = "Enter a valid positive price (e.g. 549.50)";
            ErrorLabel.Visibility = Visibility.Visible;
            ValueBox.Focus();
            ValueBox.SelectAll();
        }
    }
}
