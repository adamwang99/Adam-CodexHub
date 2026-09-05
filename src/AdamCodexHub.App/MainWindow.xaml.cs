using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using AdamCodexHub.App.Services;
using AdamCodexHub.App.ViewModels;

namespace AdamCodexHub.App;

public partial class MainWindow : Window
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int size);

    private readonly MainViewModel _viewModel;
    private Rect _restoreBoundsCache;

    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        ViewModel = viewModel;
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
        _viewModel.CodexLaunched += OnCodexLaunched;
    }

    private void OnCodexLaunched(object? sender, EventArgs e)
    {
        var app = System.Windows.Application.Current;
        var mainWindow = this;
        mainWindow.ShowInTaskbar = false;
        mainWindow.Hide();
        if (app is App typedApp)
        {
            typedApp.NotifyCodexLaunched();
        }
    }
    /// Handled via Checked/Unchecked (not Click) so any state change — mouse,
    /// keyboard space, UI Automation Toggle — applies the language immediately.</summary>
    private void LanguageToggle_Checked(object sender, RoutedEventArgs e)
    {
        App.ApplyLanguage(L10n.Vietnamese);
    }

    private void LanguageToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        App.ApplyLanguage(L10n.English);
    }

    public MainViewModel ViewModel { get; }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        // Closing the main window hides the app to the system tray instead of exiting: the
        // in-process gateway must keep serving Codex. Only a real exit (tray "Exit" or an OS
        // session ending) is allowed to tear the window down.
        if (App.IsRealExit)
        {
            return;
        }

        e.Cancel = true;
        ShowInTaskbar = false;
        Hide();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        // Rounded window corners on Windows 11 (harmless no-op on older builds).
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var preference = DWMWCP_ROUND;
            DwmSetWindowAttribute(
                hwnd,
                DWMWA_WINDOW_CORNER_PREFERENCE,
                ref preference,
                sizeof(int));
        }
        catch
        {
            // Best-effort; the fallback chrome still looks fine.
        }
    }

    private void OnProviderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox ||
            listBox.DataContext is not HomeViewModel home ||
            home.SelectedCard is not { } card)
        {
            return;
        }

        e.Handled = true;
        home.DoubleClickCommand.Execute(card);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (IsCustomMaximized)
        {
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // Thrown if the mouse button is released before DragMove begins.
        }
    }

    private void TitleBar_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Kept for symmetry; drag state is owned by DragMove itself.
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeRestore_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void Guide_Click(object sender, RoutedEventArgs e)
    {
        var guide = new GuideWindow { Owner = this };
        guide.ShowDialog();
    }

    /// <summary>
    /// True while the window is filling the work area using the manual custom-chrome
    /// maximize (WindowState.Maximized would hide the taskbar on a frameless window).
    /// </summary>
    private bool IsCustomMaximized { get; set; }

    private void ToggleMaximize()
    {
        if (IsCustomMaximized)
        {
            RestoreFromMaximize();
            return;
        }

        var workArea = SystemParameters.WorkArea;
        _restoreBoundsCache = new Rect(Left, Top, ActualWidth, ActualHeight);
        WindowState = WindowState.Normal;
        Left = workArea.Left;
        Top = workArea.Top;
        Width = workArea.Width;
        Height = workArea.Height;
        IsCustomMaximized = true;
        UpdateMaximizeGlyph();
    }

    private void RestoreFromMaximize()
    {
        IsCustomMaximized = false;
        WindowState = WindowState.Normal;
        var bounds = _restoreBoundsCache;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            Left = bounds.Left;
            Top = bounds.Top;
            Width = bounds.Width;
            Height = bounds.Height;
        }

        UpdateMaximizeGlyph();
    }

    private void UpdateMaximizeGlyph() =>
        MaximizeButton.Tag = IsCustomMaximized ? "restore" : "max";
}
