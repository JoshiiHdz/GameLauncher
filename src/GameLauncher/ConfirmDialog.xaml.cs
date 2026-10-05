using System.Windows;
using System.Windows.Controls;
using Wpf.Ui.Controls;

namespace GameLauncher;

/// <summary>The yes/no card behind <see cref="AppShell.Confirm"/>. No takes the focus, so a stray Enter never confirms.</summary>
public partial class ConfirmDialog : UserControl
{
    public ConfirmDialog(string question, string yes, string no, bool warning)
    {
        InitializeComponent();
        QuestionText.Text = question;
        YesButton.Content = yes;
        NoButton.Content = no;
        YesButton.Appearance = warning ? ControlAppearance.Danger : ControlAppearance.Primary;
        Loaded += (_, _) => NoButton.Focus();
    }

    private void Yes_Click(object sender, RoutedEventArgs e) => AppShell.RequestClose(this, true);

    private void No_Click(object sender, RoutedEventArgs e) => AppShell.RequestClose(this, false);
}
