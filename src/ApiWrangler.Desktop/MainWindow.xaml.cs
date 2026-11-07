using System.Windows;
using System.Windows.Automation.Peers;

namespace ApiWrangler.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Loaded += async (_, _) => await viewModel.InitializeSettingsAsync();
        Closing += (_, _) => viewModel.SendCommand.Cancel();
        viewModel.ProjectEditor.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(ProjectEditorViewModel.ProjectStatus)) return;
            Dispatcher.BeginInvoke(() =>
            {
                var peer = FrameworkElementAutomationPeer.FromElement(ProjectStatusText) ??
                    FrameworkElementAutomationPeer.CreatePeerForElement(ProjectStatusText);
                peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
            });
        };
    }
}
