using System;
using System.Collections.Generic;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Lifecycle state of the native QEMU UWP execution adapter.
    /// </summary>
    public enum QemuAdapterState
    {
        Uninitialized,
        Configured,
        Starting,
        Running,
        Paused,
        Stopping,
        Terminated,
        Faulted
    }

    /// <summary>
    /// Graphics presentation pipeline mode for the guest VM.
    /// </summary>
    public enum QemuGpuMode
    {
        /// <summary>
        /// Standard VGA with software memory framebuffer readback (qemu_dll_get_frame).
        /// </summary>
        SoftwareFramebuffer,

        /// <summary>
        /// Accelerated 3D Virgl via ANGLE Direct3D 11 swapchain direct presentation
        /// (virtio-vga-gl + egl-headless gl=es).
        /// </summary>
        GpuDirectAngle
    }

    /// <summary>
    /// Diagnostic validation result inspecting packaged native binaries, BIOS ROMs, and disk.
    /// </summary>
    public sealed class PackageValidationResult
    {
        public bool IsValid { get; set; }
        public string ErrorMessage { get; set; }
        public bool HasQemuDll { get; set; }
        public bool HasBiosDirectory { get; set; }
        public bool HasAngleBinaries { get; set; }
        public bool HasValidRootDisk { get; set; }
        public long RootDiskSizeBytes { get; set; }
        public string QemuDllSha256 { get; set; }

        public PackageValidationResult()
        {
            ErrorMessage = string.Empty;
        }
    }

    /// <summary>
    /// Snapshot of runtime diagnostic metrics.
    /// </summary>
    public sealed class QemuDiagnosticInfo
    {
        public QemuAdapterState State { get; set; }
        public int ExitCode { get; set; }
        public bool IsRunning { get; set; }
        public bool IsDirectPanelAttached { get; set; }
        public int DirectFrameCount { get; set; }
        public int FrameWidth { get; set; }
        public int FrameHeight { get; set; }
        public long UptimeMilliseconds { get; set; }
        public string CpuModel { get; set; }
        public int CpuCount { get; set; }
        public int RamMegabytes { get; set; }
        public int TbCacheMegabytes { get; set; }
        public int SshHostPort { get; set; }
        public QemuGpuMode GpuMode { get; set; }
        public string LastErrorMessage { get; set; }
        public IReadOnlyList<string> RecentLogMessages { get; set; }

        public QemuDiagnosticInfo()
        {
            ExitCode = -999;
            CpuModel = string.Empty;
            LastErrorMessage = string.Empty;
            RecentLogMessages = new List<string>();
        }
    }

    /// <summary>
    /// Event arguments for bounded status log notifications.
    /// </summary>
    public sealed class QemuLogEventArgs : EventArgs
    {
        public string Message { get; }
        public DateTimeOffset Timestamp { get; }

        public QemuLogEventArgs(string message)
        {
            Message = message ?? string.Empty;
            Timestamp = DateTimeOffset.UtcNow;
        }
    }
}
