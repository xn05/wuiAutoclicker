using System;
using System.Runtime.InteropServices;

namespace Autoclicker
{
    public class GlobalHotkey
    {
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int WM_HOTKEY = 0x0312;
        private const int MOD_NONE = 0x0;

        private IntPtr _windowHandle;
        private int _hotkeyId = 9000;
        private bool _isRegistered = false;

        public GlobalHotkey(IntPtr windowHandle)
        {
            _windowHandle = windowHandle;
        }

        public bool Register(int vkCode)
        {
            if (_isRegistered)
            {
                Unregister();
            }

            if (RegisterHotKey(_windowHandle, _hotkeyId, MOD_NONE, vkCode))
            {
                _isRegistered = true;
                return true;
            }

            return false;
        }

        public void Unregister()
        {
            if (_isRegistered)
            {
                UnregisterHotKey(_windowHandle, _hotkeyId);
                _isRegistered = false;
            }
        }

        public static int VirtualKeyFromVirtualKey(Windows.System.VirtualKey key)
        {
            return (int)key;
        }
    }
}
