using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GameLauncher.Services;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The My PC page (see MyPcPage.xaml). Its DataContext is the <see cref="SystemInfoSnapshot"/>.</summary>
public partial class MyPcPage : UserControl
{
    public MyPcPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Deal();
        SizeChanged += (_, _) => Deal();
    }

    private int _columns;
    private SystemInfoSnapshot? _dealt;

    /// <summary>How many columns a page this wide holds: three on a wide window, two on a narrow one, one on the smallest.</summary>
    internal static int ColumnsFor(double width) => width >= 880 ? 3 : width >= 560 ? 2 : 1;

    /// <summary>Deals the sections into even columns: each goes under the column with the fewest lines so far (a heading counts as two), keeping their order otherwise. The
    /// page used to wrap fixed-width sections, which left a short section's row half empty and made the page tall.</summary>
    internal static List<List<SystemInfoSection>> Columns(IReadOnlyList<SystemInfoSection> sections, int count)
    {
        var columns = Enumerable.Range(0, Math.Max(1, count)).Select(_ => new List<SystemInfoSection>()).ToList();
        var weights = new int[columns.Count];
        foreach (var section in sections)
        {
            var at = Array.IndexOf(weights, weights.Min());
            columns[at].Add(section);
            weights[at] += section.Items.Count + 2;
        }

        return columns;
    }

    private void Deal()
    {
        if (DataContext is not SystemInfoSnapshot snapshot || ActualWidth <= 0)
            return;

        var count = ColumnsFor(ActualWidth);
        if (count == _columns && ReferenceEquals(snapshot, _dealt))
            return;

        _columns = count;
        _dealt = snapshot;
        SectionColumns.ItemsSource = Columns(snapshot.Sections, count);
    }

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
