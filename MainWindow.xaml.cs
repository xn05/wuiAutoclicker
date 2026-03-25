using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Windows.System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace Autoclicker
{
    /// <summary>
    /// An empty window that can be used on its own or navigated to within a Frame.
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private bool _isRecording = false;
        private bool _isClicking = false;
        private VirtualKey? _boundHotkey = null;
        private CancellationTokenSource? _clickingTokenSource = null;
        private GlobalHotkey _globalHotkey;
        private IntPtr _hwnd;
        private ConfigManager _configManager;
        private bool _isLoadingConfig = false;
        private List<Task> _clickerTasks = new List<Task>();

        // P/Invoke for message hook
        private delegate IntPtr WndProcHandler(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        private const uint WM_HOTKEY = 0x0312;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        private const int GWLP_WNDPROC = -4;
        private WndProcHandler _wndProcHandler;
        private IntPtr _originalWndProc;

        public MainWindow()
        {
            InitializeComponent();

            _configManager = new ConfigManager();

            // Get window handle for global hotkey
            _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            _globalHotkey = new GlobalHotkey(_hwnd);

            // Hook the window proc
            _wndProcHandler = new WndProcHandler(WndProc);
            _originalWndProc = GetWindowLongPtr(_hwnd, GWLP_WNDPROC);
            SetWindowLongPtr(_hwnd, GWLP_WNDPROC, Marshal.GetFunctionPointerForDelegate(_wndProcHandler));

            // Set window size and disable resizing/maximizing
            var appWindow = GetAppWindow();
            if (appWindow != null)
            {
                appWindow.ResizeClient(new Windows.Graphics.SizeInt32(400, 400));
                var presenter = appWindow.Presenter as OverlappedPresenter;
                if (presenter != null)
                {
                    presenter.IsResizable = false;
                    presenter.IsMaximizable = false;
                }
            }

            // Customize the title bar
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // Set initial state
            UpdateWindowTitle();
            CPSInput.InputScope = new InputScope();
            CPSInput.InputScope.Names.Add(new InputScopeName(InputScopeNameValue.Number));

            // Load config
            LoadConfigAsync();

            // Subscribe to input changes for config saving
            CPSInput.TextChanged += CPSInput_TextChanged;
            ClickTypeCombo.SelectionChanged += ClickTypeCombo_SelectionChanged;
            XInput.ValueChanged += XInput_ValueChanged;
            YInput.ValueChanged += YInput_ValueChanged;
        }

        private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_HOTKEY)
            {
                HandleGlobalHotkey();
            }

            // Call the original window procedure
            if (_originalWndProc != IntPtr.Zero)
            {
                return CallWindowProc(_originalWndProc, hWnd, msg, wParam, lParam);
            }

            return IntPtr.Zero;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        private void HandleGlobalHotkey()
        {
            if (_isClicking)
            {
                // Stop the autoclicker
                StopButton_Click(null, null);
            }
            else
            {
                // Start the autoclicker
                StartButton_Click(null, null);
            }
        }

        private void ClickLocationCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ClickLocationCombo.SelectedIndex == 1) // "Specific" selected
            {
                XInput.Visibility = Visibility.Visible;
                YInput.Visibility = Visibility.Visible;
            }
            else // "Cursor" selected
            {
                XInput.Visibility = Visibility.Collapsed;
                YInput.Visibility = Visibility.Collapsed;
            }
            _ = SaveConfigAsync();
        }

        private void RecordButton_Click(object sender, RoutedEventArgs e)
        {
            _isRecording = !_isRecording;
            RecordButton.IsEnabled = false;

            if (_isRecording)
            {
                HotkeyDisplay.Text = "(Waiting for key...)";
            }
        }

        private void ShowOnTopCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            var appWindow = GetAppWindow();
            if (appWindow != null && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = true;
            }
            _ = SaveConfigAsync();
        }

        private void ShowOnTopCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            var appWindow = GetAppWindow();
            if (appWindow != null && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = false;
            }
            _ = SaveConfigAsync();
        }

        private async void StartButton_Click(object sender, RoutedEventArgs e)
        {
            // Validate inputs
            if (!int.TryParse(CPSInput.Text, out int cps) || cps <= 0)
            {
                ContentDialog dialog = new ContentDialog
                {
                    Title = "Invalid CPS",
                    Content = "Please enter a valid CPS value (greater than 0)",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            if (_boundHotkey == null)
            {
                ContentDialog dialog = new ContentDialog
                {
                    Title = "No Hotkey Set",
                    Content = "Please set a hotkey first by clicking the Record button",
                    CloseButtonText = "OK",
                    XamlRoot = Content.XamlRoot
                };
                await dialog.ShowAsync();
                return;
            }

            _isClicking = true;
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            UpdateWindowTitle();

            int x = -1, y = -1;
            if (ClickLocationCombo.SelectedIndex == 1) // Specific
            {
                if (!int.TryParse(XInput.Value.ToString(), out x) || !int.TryParse(YInput.Value.ToString(), out y))
                {
                    ContentDialog dialog = new ContentDialog
                    {
                        Title = "Invalid Coordinates",
                        Content = "Please enter valid X and Y coordinates",
                        CloseButtonText = "OK",
                        XamlRoot = Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                    _isClicking = false;
                    StartButton.IsEnabled = true;
                    StopButton.IsEnabled = false;
                    return;
                }
            }

            _clickingTokenSource = new CancellationTokenSource();

            // Read UI values BEFORE starting background threads (can't safely access UI from background threads)
            int clickTypeIndex = ClickTypeCombo?.SelectedIndex ?? 0;

            // Distribute CPS across multiple hands if needed
            List<int> handCPSValues = DistributeCPS(cps);
            _clickerTasks.Clear();

            foreach (int handCps in handCPSValues)
            {
                // Capture the value to avoid closure issues
                int capturedCps = handCps;
                int capturedX = x;
                int capturedY = y;
                int capturedClickType = clickTypeIndex;

                // Run each clicking hand on a completely separate background thread
                var task = Task.Run(async () => 
                {
                    try
                    {
                        await ClickerLoop(capturedCps, capturedX, capturedY, capturedClickType, _clickingTokenSource.Token);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"Clicker error: {ex.Message}");
                    }
                });

                _clickerTasks.Add(task);
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _isClicking = false;
            _clickingTokenSource?.Cancel();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            UpdateWindowTitle();
        }

        private async Task ClickerLoop(int cps, int x, int y, int clickTypeIndex, CancellationToken cancellationToken)
        {
            double intervalMs = 1000.0 / cps;
            MouseClicker.ClickType clickType = (MouseClicker.ClickType)clickTypeIndex;
            Stopwatch stopwatch = Stopwatch.StartNew();
            long clickCount = 0;
            const int delayChunkMs = 5; // Check responsiveness every 5ms

            while (!cancellationToken.IsCancellationRequested && _isClicking)
            {
                // Calculate the scheduled time for the next click based on click count
                double scheduledTimeMs = (clickCount + 1) * intervalMs;
                double currentTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                double delayMs = scheduledTimeMs - currentTimeMs;

                // If we're behind schedule, skip the delay
                if (delayMs > 0)
                {
                    // Break the delay into small chunks for responsiveness to cancellation
                    while (delayMs > 0 && !cancellationToken.IsCancellationRequested && _isClicking)
                    {
                        int chunkDelay = Math.Min(delayChunkMs, (int)Math.Ceiling(delayMs));
                        try
                        {
                            await Task.Delay(chunkDelay, cancellationToken);
                        }
                        catch (OperationCanceledException)
                        {
                            return;
                        }

                        currentTimeMs = stopwatch.Elapsed.TotalMilliseconds;
                        delayMs = scheduledTimeMs - currentTimeMs;
                    }
                }

                // Double-check we should still be clicking before clicking
                if (!_isClicking || cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                // Perform the click
                MouseClicker.Click(clickType, x == -1 ? null : x, y == -1 ? null : y);
                clickCount++;
            }
        }

        private List<int> DistributeCPS(int totalCps)
        {
            List<int> hands = new List<int>();
            const int maxCpsPerHand = 30;

            if (totalCps <= maxCpsPerHand)
            {
                // Single hand
                hands.Add(totalCps);
            }
            else
            {
                // Multiple hands needed
                int fullHands = totalCps / maxCpsPerHand;
                int remainder = totalCps % maxCpsPerHand;

                // Add full hands
                for (int i = 0; i < fullHands; i++)
                {
                    hands.Add(maxCpsPerHand);
                }

                // Add remainder as final hand if it exists
                if (remainder > 0)
                {
                    hands.Add(remainder);
                }
            }

            return hands;
        }

        private async Task LoadConfigAsync()
        {
            _isLoadingConfig = true;
            try
            {
                var config = await _configManager.LoadConfigAsync();

                // Load CPS only if it has a value
                if (config.CPS.HasValue)
                {
                    CPSInput.Text = config.CPS.Value.ToString();
                }

                // Load ClickLocation only if it has a value
                if (config.ClickLocation.HasValue)
                {
                    ClickLocationCombo.SelectedIndex = config.ClickLocation.Value;
                }

                // Load coordinates only if they have values
                if (config.CoordinateX.HasValue)
                {
                    XInput.Value = config.CoordinateX.Value;
                }
                if (config.CoordinateY.HasValue)
                {
                    YInput.Value = config.CoordinateY.Value;
                }

                // Load ClickType only if it has a value
                if (config.ClickType.HasValue)
                {
                    ClickTypeCombo.SelectedIndex = config.ClickType.Value;
                }

                ShowOnTopCheckBox.IsChecked = config.ShowOnTop;

                if (!string.IsNullOrEmpty(config.BoundHotkey) && Enum.TryParse<VirtualKey>(config.BoundHotkey, out var hotkey))
                {
                    _boundHotkey = hotkey;
                    HotkeyDisplay.Text = hotkey.ToString();
                    int vkCode = GlobalHotkey.VirtualKeyFromVirtualKey(hotkey);
                    _globalHotkey.Register(vkCode);
                }

                // Update visibility based on loaded click location
                if (ClickLocationCombo.SelectedIndex == 1)
                {
                    XInput.Visibility = Visibility.Visible;
                    YInput.Visibility = Visibility.Visible;
                }
                else
                {
                    XInput.Visibility = Visibility.Collapsed;
                    YInput.Visibility = Visibility.Collapsed;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading config: {ex.Message}");
            }
            finally
            {
                _isLoadingConfig = false;
            }
        }

        private async Task SaveConfigAsync()
        {
            if (_isLoadingConfig)
                return;

            try
            {
                // Save blank values as null instead of converting to defaults
                var config = new AutoclickerConfig
                {
                    CPS = int.TryParse(CPSInput.Text, out int cps) ? cps : null,
                    ClickLocation = ClickLocationCombo.SelectedIndex >= 0 ? ClickLocationCombo.SelectedIndex : null,
                    CoordinateX = !double.IsNaN(XInput.Value) ? (int)XInput.Value : null,
                    CoordinateY = !double.IsNaN(YInput.Value) ? (int)YInput.Value : null,
                    ClickType = ClickTypeCombo.SelectedIndex >= 0 ? ClickTypeCombo.SelectedIndex : null,
                    ShowOnTop = ShowOnTopCheckBox.IsChecked ?? false,
                    BoundHotkey = _boundHotkey?.ToString()
                };

                await _configManager.SaveConfigAsync(config);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving config: {ex.Message}");
            }
        }

        private void Grid_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (_isRecording)
            {
                _boundHotkey = e.Key;
                HotkeyDisplay.Text = e.Key.ToString();
                _isRecording = false;
                RecordButton.IsEnabled = true;

                // Register the global hotkey
                int vkCode = GlobalHotkey.VirtualKeyFromVirtualKey(e.Key);
                _globalHotkey.Register(vkCode);

                // Save config after hotkey change
                _ = SaveConfigAsync();

                e.Handled = true;
            }
        }

        private void CPSInput_TextChanged(object sender, TextChangedEventArgs e)
        {
            _ = SaveConfigAsync();
        }

        private void ClickTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            _ = SaveConfigAsync();
        }

        private void XInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            _ = SaveConfigAsync();
        }

        private void YInput_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
        {
            _ = SaveConfigAsync();
        }

        public void OnWindowClosing()
        {
            // Stop clicking immediately
            if (_isClicking)
            {
                _clickingTokenSource?.Cancel();
                _isClicking = false;
            }

            // Unregister hotkey
            _globalHotkey?.Unregister();

            // Save config asynchronously without blocking shutdown
            var config = new AutoclickerConfig
            {
                CPS = int.TryParse(CPSInput.Text, out int cps) ? cps : null,
                ClickLocation = ClickLocationCombo.SelectedIndex >= 0 ? ClickLocationCombo.SelectedIndex : null,
                CoordinateX = !double.IsNaN(XInput.Value) ? (int)XInput.Value : null,
                CoordinateY = !double.IsNaN(YInput.Value) ? (int)YInput.Value : null,
                ClickType = ClickTypeCombo.SelectedIndex >= 0 ? ClickTypeCombo.SelectedIndex : null,
                ShowOnTop = ShowOnTopCheckBox.IsChecked ?? false,
                BoundHotkey = _boundHotkey?.ToString()
            };

            // Fire and forget - don't block on shutdown
            _ = _configManager.SaveConfigAsync(config);
        }

        private void UpdateWindowTitle()
        {
            if (_isClicking)
            {
                Title = "Autoclicker (Clicking)";
                AppTitle.Text = "Autoclicker (Clicking)";
            }
            else
            {
                Title = "Autoclicker (Stopped)";
                AppTitle.Text = "Autoclicker (Stopped)";
            }
        }

        private AppWindow GetAppWindow()
        {
            var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            return AppWindow.GetFromWindowId(windowId);
        }
    }
}
