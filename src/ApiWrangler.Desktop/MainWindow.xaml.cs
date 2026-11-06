using System.Windows;

namespace ApiWrangler.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += (_, _) => viewModel.SendCommand.Cancel();
    }
}
