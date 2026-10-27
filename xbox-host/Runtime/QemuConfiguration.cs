using System;
using System.IO;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Configuration model for launching the native QEMU UWP guest engine.
    /// Strictly encapsulates VM arguments without allowing arbitrary command injection.
    /// </summary>
    public sealed class QemuConfiguration
    {
        /// <summary>
        /// Absolute path to caller-provisioned, owned fresh sparse raw root disk.
        /// Mandatory; existing booted guest disks are rejected.
        /// </summary>
        public string RootDiskPath { get; set; }

        public string ProvisioningReceiptPath { get; set; }

        // Task-created NoCloud seed; never included in source or cloud builds.
        public string SeedIsoPath { get; set; }
        public bool AudioEnabled { get; set; } = true;

        /// <summary>
        /// Absolute path to packaged pc-bios directory containing BIOS ROM binaries.
        /// </summary>
        public string BiosDirectory { get; set; }

        /// <summary>
        /// Guest RAM allocation in megabytes. Default: 2048 (2 GiB).
        /// </summary>
        public int RamMegabytes { get; set; } = 2048;

        /// <summary>
        /// Guest virtual SMP CPU cores. Default: 4 (TCG multi-threaded).
        /// </summary>
        public int CpuCount { get; set; } = 4;

        /// <summary>
        /// Guest CPU emulation model. Default: "max".
        /// </summary>
        public string CpuModel { get; set; } = "max";

        /// <summary>
        /// TCG translation block cache size in megabytes. Default: 512.
        /// </summary>
        public int TbCacheMegabytes { get; set; } = 512;

        /// <summary>
        /// Host port forwarded to guest SSH (port 22) via SLIRP NAT. Default: 2223.
        /// (Engine probe previously used 2222; 2223 prevents collision).
        /// </summary>
        public int SshHostPort { get; set; } = 2223;

        /// <summary>
        /// Graphics rendering and presentation mode. Default: GpuDirectAngle.
        /// </summary>
        public QemuGpuMode GpuMode { get; set; } = QemuGpuMode.GpuDirectAngle;

        /// <summary>
        /// Virtual display width in pixels. Default: 1280.
        /// </summary>
        public int DisplayWidth { get; set; } = 1280;

        /// <summary>
        /// Virtual display height in pixels. Default: 720.
        /// </summary>
        public int DisplayHeight { get; set; } = 720;

        /// <summary>
        /// ANGLE binary suffix for Xbox Direct3D 11 compatibility. Default: "_new".
        /// </summary>
        public string AngleSuffix { get; set; } = "_new";

        /// <summary>
        /// Disk device serial number exposed in Linux guest /dev/disk/by-id/.
        /// Default: "droiddeck-root".
        /// </summary>
        public string DiskSerial { get; set; } = "droiddeck-root";

        /// <summary>
        /// Optional path to redirect native QEMU C runtime stdout/stderr log output.
        /// </summary>
        public string OutLogPath { get; set; }

        public string SerialBootLogPath { get; set; }

        /// <summary>
        /// Optional path to local directory exposed as read-only vvfat shared folder.
        /// </summary>
        public string SharedFolderPath { get; set; }

        /// <summary>
        /// Validates that configuration adheres to safety constraints and bounds.
        /// </summary>
        public void Validate()
        {
            if (string.IsNullOrWhiteSpace(RootDiskPath))
            {
                throw new ArgumentException("RootDiskPath must be explicitly specified.", nameof(RootDiskPath));
            }

            if (CpuCount < 1 || CpuCount > 8)
            {
                throw new ArgumentOutOfRangeException(nameof(CpuCount), "CpuCount must be between 1 and 8 (default 4).");
            }

            if (RamMegabytes < 512 || RamMegabytes > 4096)
            {
                throw new ArgumentOutOfRangeException(nameof(RamMegabytes), "RamMegabytes must be between 512 and 4096 (default 2048).");
            }

            if (TbCacheMegabytes < 64 || TbCacheMegabytes > 1024)
            {
                throw new ArgumentOutOfRangeException(nameof(TbCacheMegabytes), "TbCacheMegabytes must be between 64 and 1024 (default 512).");
            }

            if (SshHostPort < 1024 || SshHostPort > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(SshHostPort), "SshHostPort must be in valid unprivileged range (1024-65535, default 2223).");
            }

            if (DisplayWidth <= 0 || DisplayHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(DisplayWidth), "Display dimensions must be positive (default 1280x720).");
            }

            if (string.IsNullOrWhiteSpace(CpuModel))
            {
                CpuModel = "max";
            }

            if (string.IsNullOrWhiteSpace(DiskSerial))
            {
                DiskSerial = "droiddeck-root";
            }
        }
    }
}
