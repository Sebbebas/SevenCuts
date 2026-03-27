using Avalonia.Controls;
using Avalonia.Platform;
using System;
using System.Runtime.InteropServices;

namespace SevenCuts
{
    public class VlcVideoView : NativeControlHost
    {
        public IntPtr VideoHandle { get; private set; } = IntPtr.Zero;

        [DllImport("user32.dll")]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle, string lpClassName, string lpWindowName,
            int dwStyle, int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll")]
        private static extern bool DestroyWindow(IntPtr hwnd);

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var hwnd = CreateWindowEx(0, "Static", "",
                    0x40000000 | 0x10000000,
                    0, 0, 1, 1,
                    parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                VideoHandle = hwnd;
                return new PlatformHandle(hwnd, "HWND");
            }
            return base.CreateNativeControlCore(parent);
        }

        protected override void DestroyNativeControlCore(IPlatformHandle control)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && VideoHandle != IntPtr.Zero)
            {
                DestroyWindow(VideoHandle);
                VideoHandle = IntPtr.Zero;
            }
            else
            {
                base.DestroyNativeControlCore(control);
            }
        }
    }
}