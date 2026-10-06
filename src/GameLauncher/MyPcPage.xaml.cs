using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The My PC page (see MyPcPage.xaml). Its DataContext is the <see cref="SystemInfoSnapshot"/>.</summary>
public partial class MyPcPage : UserControl
{
    public MyPcPage() => InitializeComponent();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemInfoSnapshot snapshot)
            return;

        try
        {
            Clipboard.SetText(snapshot.ToText());
            CopyButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            Logger.Warn("My PC: couldn't copy to the clipboard.", ex);
            CopyButton.Content = "Couldn't copy";
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            CopyButton.Content = "Copy all";
        };
        timer.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this)?.DataContext is LibraryViewModel library)
            library.ShowMyPcCommand.Execute(null);
    }
}
