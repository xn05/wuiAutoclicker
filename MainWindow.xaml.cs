using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
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
                appWindow.ResizeClient(new Windows.Graphics.SizeInt32(300, 300));
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
        }

        private void ShowOnTopCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            var appWindow = GetAppWindow();
            if (appWindow != null && appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsAlwaysOnTop = false;
            }
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
            _ = ClickerLoop(cps, x, y, _clickingTokenSource.Token);
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            _isClicking = false;
            _clickingTokenSource?.Cancel();
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
            UpdateWindowTitle();
        }

        private async Task ClickerLoop(int cps, int x, int y, CancellationToken cancellationToken)
        {
            int delayMs = (int)(1000.0 / cps);

            while (_isClicking && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    MouseClicker.ClickType clickType = (MouseClicker.ClickType)ClickTypeCombo.SelectedIndex;

                    if (ClickLocationCombo.SelectedIndex == 0) // Cursor
                    {
                        MouseClicker.Click(clickType);
                    }
                    else // Specific
                    {
                        MouseClicker.Click(clickType, x, y);
                    }

                    await Task.Delay(delayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
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

                e.Handled = true;
            }
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
