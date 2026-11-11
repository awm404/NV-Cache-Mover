using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using NvidiaCacheMover.Models;
using NvidiaCacheMover.Services;

namespace NvidiaCacheMover;

public partial class MainWindow : Window
{
    private readonly CacheMigrationService _service = new();
    private readonly List<CacheItemModel> _items;
    private List<DriveCardModel> _drives = [];
    private bool _isBusy;

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;
    private const int WS_SYSMENU = 0x00080000;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WM_STYLECHANGING = 0x007C;
    private const int WM_NCCALCSIZE = 0x0083;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_FRAMECHANGED = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct STYLESTRUCT
    {
        public int styleOld;
        public int styleNew;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTA_OPTIONS
    {
        public uint dwFlags;
        public uint dwMask;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("uxtheme.dll")]
    private static extern int SetWindowThemeAttribute(IntPtr hWnd, int wtype, ref WTA_OPTIONS attributes, uint size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLongW(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    public MainWindow()
    {
        InitializeComponent();

        _items = _service.CreateDefaultItems();
        CacheItemsControl.ItemsSource = _items;

        string initialTarget = _service.DetectInitialTargetRoot(_items);
        TargetPathTextBox.Text = initialTarget;

        SourceInitialized += (_, _) => ApplyWindows11WindowStyling();
        StateChanged += (_, _) =>
        {
            RootWindowBorder.Padding = WindowState == WindowState.Maximized
                ? new Thickness(8)
                : new Thickness(0);
        };
        Loaded += async (_, _) => await RefreshDashboardAsync("Live NT kernel engine initialized. Ready to relocate caches.");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_STYLECHANGING && (long)wParam == GWL_STYLE && lParam != IntPtr.Zero)
        {
            var ss = Marshal.PtrToStructure<STYLESTRUCT>(lParam);
            ss.styleNew &= ~(WS_CAPTION | WS_SYSMENU);
            ss.styleNew |= WS_THICKFRAME;
            Marshal.StructureToPtr(ss, lParam, fDeleteOld: false);
            return IntPtr.Zero;
        }

        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }

        return IntPtr.Zero;
    }

    private void ApplyWindows11WindowStyling()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            var hwndSource = HwndSource.FromHwnd(hwnd);
            if (hwndSource != null)
            {
                hwndSource.AddHook(WndProc);
                if (hwndSource.CompositionTarget != null)
                    hwndSource.CompositionTarget.BackgroundColor = Colors.Transparent;
            }

            // Strip WS_CAPTION & WS_SYSMENU while keeping WS_THICKFRAME so Windows 11 DWM never paints the solid 31px active titlebar strip on focus
            int style = GetWindowLongW(hwnd, GWL_STYLE);
            _ = SetWindowLongW(hwnd, GWL_STYLE, (style & ~(WS_CAPTION | WS_SYSMENU)) | WS_THICKFRAME);

            var wta = new WTA_OPTIONS { dwFlags = 0x0F, dwMask = 0x0F };
            _ = SetWindowThemeAttribute(hwnd, 1, ref wta, (uint)Marshal.SizeOf<WTA_OPTIONS>());

            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);

            var margins = new MARGINS { cxLeftWidth = -1, cxRightWidth = -1, cyTopHeight = -1, cyBottomHeight = -1 };
            _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);

            int useDark = 1;
            _ = DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE

            int roundCorners = 2; // DWMWCP_ROUND
            _ = DwmSetWindowAttribute(hwnd, 33, ref roundCorners, sizeof(int)); // DWMWA_WINDOW_CORNER_PREFERENCE

            int noColor = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
            _ = DwmSetWindowAttribute(hwnd, 34, ref noColor, sizeof(int)); // DWMWA_BORDER_COLOR
            _ = DwmSetWindowAttribute(hwnd, 35, ref noColor, sizeof(int)); // DWMWA_CAPTION_COLOR (suppresses top focus band!)

            int acrylicBackdrop = 3; // DWMSBT_TRANSIENTWINDOW (Live Desktop Acrylic)
            _ = DwmSetWindowAttribute(hwnd, 38, ref acrylicBackdrop, sizeof(int)); // DWMWA_SYSTEMBACKDROP_TYPE
        }
        catch
        {
            // Ignore on older Windows versions
        }
    }

    private async Task RefreshDashboardAsync(string? logMessage = null)
    {
        _drives = _service.GetFixedNtfsDrives();
        DriveCardsControl.ItemsSource = _drives;
        UpdateSelectedDriveHighlight();

        string targetRoot = string.IsNullOrWhiteSpace(TargetPathTextBox.Text)
            ? @"D:\NVIDIA_Cache"
            : TargetPathTextBox.Text.Trim();

        await _service.RefreshAllAsync(_items, targetRoot);

        long totalBytes = _items.Sum(i => i.SizeBytes);
        int linkedCount = _items.Count(i => i.IsLinked);

        TotalCacheHeroText.Text = CacheItemModel.FormatBytes(totalBytes);
        ActiveLinksChipText.Text = $"{linkedCount}/{_items.Count} Linked";

        if (!string.IsNullOrWhiteSpace(logMessage))
            AppendLog(logMessage);
    }

    private void UpdateSelectedDriveHighlight()
    {
        if (_drives.Count == 0) return;

        string currentText = TargetPathTextBox?.Text?.Trim() ?? "";
        string? root = null;
        try
        {
            if (currentText.Length >= 2 && currentText[1] == ':')
                root = currentText[..2] + "\\";
        }
        catch
        {
            // Ignore partial typing
        }

        foreach (var d in _drives)
        {
            d.IsSelectedDrive = root != null && JunctionHelper.PathsEqual(d.DriveRoot, root);
        }
    }

    private void ShowToast(string message, bool isError = false)
    {
        ToastText.Text = message;
        if (isError)
        {
            ToastBanner.Background = new SolidColorBrush(Color.FromArgb(0x75, 0x3D, 0x12, 0x1E));
            ToastDot.Fill = new SolidColorBrush(Color.FromRgb(0xFB, 0x71, 0x85));
        }
        else
        {
            ToastBanner.Background = new SolidColorBrush(Color.FromArgb(0x70, 0x1D, 0x36, 0x10));
            ToastDot.Fill = new SolidColorBrush(Color.FromRgb(0x8C, 0xE0, 0x00));
        }
        ToastBanner.Visibility = Visibility.Visible;
    }

    private void DismissToast_Click(object sender, RoutedEventArgs e)
    {
        ToastBanner.Visibility = Visibility.Collapsed;
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        MoveAndLinkBtn.IsEnabled = !busy;
        CleanOtaBtn.IsEnabled = !busy;
        RestoreBtn.IsEnabled = !busy;
        RefreshBtn.IsEnabled = !busy;
        BrowseBtn.IsEnabled = !busy;
        TargetPathTextBox.IsEnabled = !busy;
    }

    private void AppendLog(string message)
    {
        Dispatcher.InvokeAsync(() =>
        {
            string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (string.IsNullOrEmpty(LogTextBox.Text))
                LogTextBox.Text = line;
            else
                LogTextBox.AppendText(Environment.NewLine + line);

            LogTextBox.ScrollToEnd();
        });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            MaximizeBtn_Click(sender, e);
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();

    private void TargetPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSelectedDriveHighlight();
    }

    private void DriveCard_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (sender is Button { Tag: string suggestedPath })
        {
            TargetPathTextBox.Text = suggestedPath;
            UpdateSelectedDriveHighlight();
            AppendLog($"Target volume selected -> {suggestedPath}");
        }
    }

    private void ToggleAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        bool shouldSelectAll = _items.Any(i => !i.IsSelected);
        foreach (var item in _items)
            item.IsSelected = shouldSelectAll;
    }

    private void ClearLogBtn_Click(object sender, RoutedEventArgs e)
    {
        LogTextBox.Clear();
    }

    private void BrowseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var dlg = new OpenFolderDialog
        {
            Title = "Choose Preferred NVIDIA Cache Folder",
            Multiselect = false
        };

        string current = TargetPathTextBox.Text.Trim();
        if (!string.IsNullOrWhiteSpace(current))
        {
            string? existing = Directory.Exists(current) ? current : Path.GetDirectoryName(current);
            if (!string.IsNullOrWhiteSpace(existing) && Directory.Exists(existing))
                dlg.InitialDirectory = existing;
        }

        if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            TargetPathTextBox.Text = dlg.FolderName;
            UpdateSelectedDriveHighlight();
            AppendLog($"Selected custom folder -> {dlg.FolderName}");
        }
    }

    private void OpenDestBtn_Click(object sender, RoutedEventArgs e)
    {
        string path = TargetPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path)) return;

        try
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);

            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            ShowToast($"Could not open folder: {ex.Message}", isError: true);
        }
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        SetBusy(true);
        try
        {
            await RefreshDashboardAsync("Telemetry & junction states refreshed.");
            ShowToast("All cache module statuses refreshed.");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void MoveAndLinkBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ShowToast("Enable at least one cache module toggle before relocating.", isError: true);
            return;
        }

        string targetRoot = TargetPathTextBox.Text.Trim();
        SetBusy(true);
        ActionProgressBar.Value = 0;

        try
        {
            var progress = new Progress<double>(pct => ActionProgressBar.Value = pct);
            AppendLog($"Relocating {selected.Count} module(s) -> {targetRoot}");

            await _service.MoveAndLinkSelectedAsync(selected, targetRoot, AppendLog, progress);
            await RefreshDashboardAsync("All selected modules migrated & linked successfully.");

            ActionProgressBar.Value = 100;
            ShowToast($"Relocated & linked {selected.Count} cache module(s) to {Path.GetFullPath(targetRoot)}.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ShowToast($"Relocation error: {ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CleanOtaBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var otaItem = _items.FirstOrDefault(i => i.Id == "ota_artifacts");
        if (otaItem == null) return;

        SetBusy(true);
        try
        {
            string targetRoot = TargetPathTextBox.Text.Trim();
            long freed = await _service.CleanOtaArtifactsAsync(otaItem, targetRoot, AppendLog);
            await RefreshDashboardAsync();

            ShowToast(freed > 0
                ? $"Purged {CacheItemModel.FormatBytes(freed)} of leftover NVIDIA driver installers."
                : "Driver installer cache is already clean (0 MB).");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ShowToast($"Purge failed: {ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void RestoreBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;

        var selected = _items.Where(i => i.IsSelected && i.IsLinked).ToList();
        if (selected.Count == 0)
        {
            ShowToast("No externally-linked cache modules are toggled for restore.", isError: true);
            return;
        }

        var confirm = MessageBox.Show(
            this,
            $"Restore {selected.Count} selected module(s) back to their default folders on C:? Make sure C: has sufficient free space.",
            "Confirm Restore to C:",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm != MessageBoxResult.Yes)
            return;

        SetBusy(true);
        ActionProgressBar.Value = 0;

        try
        {
            string targetRoot = TargetPathTextBox.Text.Trim();
            var progress = new Progress<double>(pct => ActionProgressBar.Value = pct);
            await _service.RestoreSelectedToDefaultAsync(selected, targetRoot, AppendLog, progress);
            await RefreshDashboardAsync("Selected modules restored to default C: folders.");
            ActionProgressBar.Value = 100;
            ShowToast($"Restored {selected.Count} cache module(s) back to C:.");
        }
        catch (Exception ex)
        {
            AppendLog($"ERROR: {ex.Message}");
            ShowToast($"Restore failed: {ex.Message}", isError: true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void ShortcutBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? exePath = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
                throw new InvalidOperationException("Could not resolve executable path.");

            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string lnkPath = Path.Combine(desktop, "NVIDIA Cache Mover.lnk");
            string workDir = Path.GetDirectoryName(exePath) ?? desktop;

            string psScript =
                $"$ws = New-Object -ComObject WScript.Shell; " +
                $"$s = $ws.CreateShortcut('{lnkPath.Replace("'", "''")}'); " +
                $"$s.TargetPath = '{exePath.Replace("'", "''")}'; " +
                $"$s.WorkingDirectory = '{workDir.Replace("'", "''")}'; " +
                $"$s.IconLocation = '{exePath.Replace("'", "''")},0'; " +
                $"$s.Description = 'Move and link NVIDIA shader caches to any NTFS drive'; " +
                $"$s.Save()";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -NonInteractive -Command \"{psScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            proc?.WaitForExit(5000);

            AppendLog($"Pinned shortcut to Desktop -> {lnkPath}");
            ShowToast("Shortcut pinned to your Desktop.");
        }
        catch (Exception ex)
        {
            AppendLog($"Shortcut error: {ex.Message}");
            ShowToast($"Could not create shortcut: {ex.Message}", isError: true);
        }
    }
}
