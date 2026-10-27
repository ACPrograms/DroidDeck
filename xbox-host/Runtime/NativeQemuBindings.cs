using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Low-level P/Invoke bindings to the native QEMU UWP engine dynamic library (qemu.dll)
    /// and Win32 process environment APIs on Xbox Dev Mode.
    ///
    /// Factual Attribution:
    /// Adapted under GPL-2.0 from public native ABI declarations in:
    /// research/YoCanIGetUhhhh-XboxInstallOtherOS-dev/MainPage.xaml.cs
    /// commit 426235db19fc1ae17f8d50e8702015efbb676496.
    /// Preserves factual attribution; no invented copyright.
    /// </summary>
    internal static class NativeQemuBindings
    {
        private const string QemuDll = "qemu.dll";
        private const string KernelEnvDll = "api-ms-win-core-processenvironment-l1-1-0.dll";

        [DllImport(KernelEnvDll, EntryPoint = "SetEnvironmentVariableW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetEnvironmentVariable(string name, string value);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_main", CallingConvention = CallingConvention.Cdecl)]
        public static extern int qemu_dll_main(IntPtr outfile, int argc, IntPtr argv);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_stop", CallingConvention = CallingConvention.Cdecl)]
        public static extern void qemu_dll_stop();

        [DllImport(QemuDll, EntryPoint = "qemu_dll_hmp", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
        public static extern int qemu_dll_hmp(
            [MarshalAs(UnmanagedType.LPStr)] string cmd,
            [Out] StringBuilder outText,
            int cap,
            int timeoutMs);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_set_panel", CallingConvention = CallingConvention.Cdecl)]
        public static extern void qemu_dll_set_panel(IntPtr panel);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_set_panel_size", CallingConvention = CallingConvention.Cdecl)]
        public static extern void qemu_dll_set_panel_size(int w, int h, float sx, float sy);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_attach_panel", CallingConvention = CallingConvention.Cdecl)]
        public static extern int qemu_dll_attach_panel();

        [DllImport(QemuDll, EntryPoint = "qemu_dll_direct_info", CallingConvention = CallingConvention.Cdecl)]
        public static extern int qemu_dll_direct_info(out int w, out int h);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_request_snapshot", CallingConvention = CallingConvention.Cdecl)]
        public static extern void qemu_dll_request_snapshot();

        [DllImport(QemuDll, EntryPoint = "qemu_dll_get_frame", CallingConvention = CallingConvention.Cdecl)]
        public static extern int qemu_dll_get_frame(
            [Out] byte[] dst,
            int capBytes,
            out int w,
            out int h,
            ref int seq);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_key", CallingConvention = CallingConvention.Cdecl)]
        public static extern void qemu_dll_key(int keyNumber, int down);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_abs", CallingConvention = CallingConvention.Cdecl)]
        public static extern void qemu_dll_abs(int x, int y, int wheel, int buttons);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_mouse", CallingConvention = CallingConvention.Cdecl)]
        public static extern void qemu_dll_mouse(int dx, int dy, int wheel, int buttons);

        [DllImport(QemuDll, EntryPoint = "qemu_dll_ping", CallingConvention = CallingConvention.Cdecl)]
        public static extern int qemu_dll_ping();
    }
}
