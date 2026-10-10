using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using WindowsSandboxManager.App.ViewModels;
using WindowsSandboxManager.Application.Abstractions;

namespace WindowsSandboxManager.App;

public sealed partial class MainWindow : Window
{
    private bool _initialized;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Windows Sandbox Manager";

        ViewModel = new MainViewModel(App.Services.GetRequiredService<ISandboxApplicationService>());
        Root.DataContext = ViewModel;

        Activated += OnActivated;
    }

    public MainViewModel ViewModel { get; }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        await ViewModel.InitializeAsync();
    }
}
