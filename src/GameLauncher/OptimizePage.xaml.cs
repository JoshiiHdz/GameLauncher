using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The Optimize tabs (Settings > Performance). All behaviour is on OptimizeViewModel; the page only asks it to measure the storage the first time it is in view.</summary>
public partial class OptimizePage : UserControl
{
    public OptimizePage()
    {
        InitializeComponent();
        Loaded += (_, _) => ScanWhenInView();
        IsVisibleChanged += (_, _) => ScanWhenInView();
        DataContextChanged += (_, _) => ScanWhenInView();
    }

    private void ScanWhenInView()
    {
        if (IsVisible && DataContext is OptimizeViewModel page)
            page.EnsureScanned();
    }
}
