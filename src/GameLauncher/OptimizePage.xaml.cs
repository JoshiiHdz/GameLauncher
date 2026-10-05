using System.Windows.Controls;
using GameLauncher.ViewModels;

namespace GameLauncher;

/// <summary>The Optimize page. All behaviour is on OptimizeViewModel; the page only starts the first scan when it opens.</summary>
public partial class OptimizePage : UserControl
{
    public OptimizePage()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as OptimizeViewModel)?.ScanCommand.Execute(null);
    }
}
