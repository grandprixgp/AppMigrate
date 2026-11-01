using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TeaChest.Models;
using TeaChest.Services;
using TeaChest.ViewModels;

namespace TeaChest;

// TODO: v2 - general tighten-up and redesign of the window. This code-behind has grown a click
// handler per button; most of it should move into commands on the view models.
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private readonly NearbyViewModel _nearby;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel();
        _viewModel.Error += ViewModel_Error;
        DataContext = _viewModel;

        _nearby = new NearbyViewModel(_viewModel, Dispatcher);
        _nearby.PairingRequested += (_, _) =>
        {
            NearbyNav.IsChecked = true;
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        };
        _nearby.InstallListReady += (_, _) => InstallNav.IsChecked = true;
        NearbyView.DataContext = _nearby;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        bool dark = ThemeService.LoadPreference();
        ThemeService.Apply(dark);
        UpdateThemeButton();
        VersionText.Text = _viewModel.VersionText;

        if (ManifestService.FindDefault(AppContext.BaseDirectory) is { } manifest)
        {
            await _viewModel.LoadManifestAsync(manifest);
        }
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
        => ThemeService.ApplyWindowTheme(this, ThemeService.IsDark);

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_viewModel.IsBusy)
        {
            _ = _nearby.ShutdownAsync();
            return;
        }
        e.Cancel = true;
        MessageBox.Show(this, "An operation is still running. Wait for it to finish, or use Cancel in the status bar.", "TeaChest", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void CollectNav_Checked(object sender, RoutedEventArgs e) => ShowView(CollectView);

    private void InstallNav_Checked(object sender, RoutedEventArgs e) => ShowView(InstallView);

    private void NearbyNav_Checked(object sender, RoutedEventArgs e) => ShowView(NearbyView);

    private void ShowView(Grid? view)
    {
        // Checked fires for the default tab while InitializeComponent is still building the views.
        if (CollectView == null || InstallView == null || NearbyView == null || view == null) return;
        CollectView.Visibility = view == CollectView ? Visibility.Visible : Visibility.Collapsed;
        InstallView.Visibility = view == InstallView ? Visibility.Visible : Visibility.Collapsed;
        NearbyView.Visibility = view == NearbyView ? Visibility.Visible : Visibility.Collapsed;

        // Only search the network while someone is looking at the list.
        if (view == NearbyView) _nearby?.StartBrowsing();
        else _nearby?.StopBrowsing();
    }

    private async void ShareButton_Click(object sender, RoutedEventArgs e) => await _nearby.ToggleSharingAsync();

    private void ConfirmPairing_Click(object sender, RoutedEventArgs e) => _nearby.AnswerPairing(true);

    private void RejectPairing_Click(object sender, RoutedEventArgs e) => _nearby.AnswerPairing(false);

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DevicesList.SelectedItem is NearbyDevice device) await _nearby.ConnectAsync(device);
        else if (_nearby.Devices.Count == 1) await _nearby.ConnectAsync(_nearby.Devices[0]);
    }

    private async void DevicesList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DevicesList.SelectedItem is NearbyDevice device) await _nearby.ConnectAsync(device);
    }

    private async void ConnectManualButton_Click(object sender, RoutedEventArgs e) => await _nearby.ConnectManualAsync();

    private void CancelConnect_Click(object sender, RoutedEventArgs e) => _nearby.CancelConnect();

    private void OpenNetworkSettings_Click(object sender, RoutedEventArgs e) => _nearby.OpenNetworkSettings();

    private void InstallMissing_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            MessageBox.Show(this, "Wait for the current operation to finish first.", "TeaChest", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (_viewModel.Apps.Count > 0)
        {
            MessageBoxResult replace = MessageBox.Show(this,
                "This replaces the current app list with the apps missing on this PC. Save a manifest first if you need the current list.\n\nContinue?",
                "Use missing apps",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Question);
            if (replace != MessageBoxResult.OK) return;
        }
        _nearby.UseMissingAsInstallList();
    }

    private string? PickManifestFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open TeaChest manifest",
            Filter = "JSON manifest (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        if (PickManifestFile() is { } path)
        {
            await _viewModel.LoadManifestAsync(path);
            CollectNav.IsChecked = true;
        }
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        if (PickManifestFile() is { } path)
        {
            await _viewModel.LoadManifestAsync(path);
        }
    }

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        string initial = _viewModel.ManifestPath.Length > 0 ? _viewModel.ManifestPath : _viewModel.DefaultManifestPath;
        var dialog = new SaveFileDialog
        {
            Title = "Save TeaChest manifest",
            Filter = "JSON manifest (*.json)|*.json|All files (*.*)|*.*",
            DefaultExt = "json",
            AddExtension = true,
            OverwritePrompt = true,
            FileName = Path.GetFileName(initial),
            InitialDirectory = Path.GetDirectoryName(initial) ?? AppContext.BaseDirectory
        };
        if (dialog.ShowDialog(this) == true)
        {
            await _viewModel.SaveManifestAsync(dialog.FileName);
        }
    }

    private async void PreviewButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        await _viewModel.BuildPlanAsync();
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        InstallPlan? plan = await _viewModel.BuildPlanAsync();
        if (plan == null) return;
        if (plan.InstallCount == 0)
        {
            MessageBox.Show(this,
                "There is nothing to install automatically. Check the plan for manual entries or skipped apps.",
                "Nothing to install",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        if (!ConfirmInstall(plan.InstallCount, plan.SkipCount, plan.ManualCount)) return;

        InstallReport? report = await _viewModel.InstallAsync();
        if (report == null) return;
        ShowInstallReport(report);
    }

    private async void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.IsBusy) return;
        int count = _viewModel.RetryCount;
        if (count == 0) return;

        // Confirm before the retry plan resets the rows, so declining changes nothing.
        if (!ConfirmInstall(count, 0, 0)) return;

        InstallReport? report = await _viewModel.RetryFailedAsync();
        if (report == null) return;
        ShowInstallReport(report);
    }

    private bool ConfirmInstall(int installCount, int skipCount, int manualCount)
    {
        string others = skipCount + manualCount > 0
            ? $"The plan also contains {skipCount} already-present and {manualCount} manual item(s).\n\n"
            : "";
        MessageBoxResult confirm = MessageBox.Show(this,
            $"Install {installCount} app(s) now?\n\n" +
            others +
            "This will change this PC.",
            "Confirm install",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        return confirm == MessageBoxResult.Yes;
    }

    private void ShowInstallReport(InstallReport report)
    {
        string message = $"Installed: {report.Installed}\nAlready present: {report.AlreadyInstalled}\nManual: {report.Manual}\nFailed: {report.Failed}";
        if (report.RebootRequired > 0) message += $"\nInstalled, restart needed: {report.RebootRequired}";
        if (report.Cancelled) message += $"\nNot attempted (cancelled): {report.NotAttempted}";
        message += $"\n\nReport: {report.ReportPath}\nLog: {report.LogPath}";
        if (report.ManualActionsPath.Length > 0) message += $"\nManual actions: {report.ManualActionsPath}";
        bool warn = report.Failed > 0 || report.Cancelled;
        string title = report.Cancelled ? "Install cancelled"
            : report.Failed > 0 ? "Install finished with errors"
            : report.RebootRequired > 0 ? "Install complete - restart needed"
            : "Install complete";
        MessageBox.Show(this, message, title,
            MessageBoxButton.OK,
            warn ? MessageBoxImage.Warning : MessageBoxImage.Information);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Apply(!ThemeService.IsDark);
        ThemeService.SavePreference();
        UpdateThemeButton();
        ThemeService.ApplyWindowTheme(this, ThemeService.IsDark);
    }

    private void UpdateThemeButton()
        => ThemeButton.Content = ThemeService.IsDark ? "Light theme" : "Dark theme";

    private void ViewModel_Error(object? sender, AppErrorEventArgs e)
    {
        MessageBox.Show(this, e.Message, e.Title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // TODO: a MessageBox is a pretty sad details view. A side panel with the ID, where it was
    // matched from, and an "open in Store / winget.run" link would be much nicer.
    private void AppsGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (AppsGrid.SelectedItem is not AppItem app) return;
        string details = $"Name: {app.Name}\nID: {app.Id}\nType: {app.InstallType}\nVersion: {app.Version}\n" +
                         $"Category: {app.Category}\nPublisher: {app.Publisher}\nScope: {app.Scope}\n\n{app.Notes}";
        MessageBox.Show(this, details, app.Name, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PlanGrid_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PlanGrid.SelectedItem is not PlanItem item) return;
        MessageBox.Show(this, $"{item.Name}\n\n{item.Detail}\n\n{item.Id}", "Plan item", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void LogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (LogBox.IsVisible) LogBox.ScrollToEnd();
    }
}
