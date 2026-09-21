using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace NektronMoments;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        if (Services.DiagnosticTrace.Enabled) {
            Title = "Nektron Moments — Diagnostics";
            AppTitleBar.Title = "Nektron Moments · Diagnostics";
        }

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon(System.IO.Path.Combine(AppContext.BaseDirectory, "Assets/Window.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1480, 960));
        ((FrameworkElement)Content).RequestedTheme =
            Services.UserPreferences.Theme == "Dark"
                ? ElementTheme.Dark : ElementTheme.Light;

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
        Activated += (_, args) => {
            if (args.WindowActivationState == WindowActivationState.Deactivated)
                (RootFrame.Content as MainPage)?.SuspendGalleryMotion();
        };
        Closed += (_, _) => (RootFrame.Content as MainPage)?.Shutdown();
    }
}
