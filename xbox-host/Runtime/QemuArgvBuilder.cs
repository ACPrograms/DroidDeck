using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Unmanaged argv memory wrapper providing deterministic IDisposable lifetime cleanup.
    /// Guarantees that all unmanaged HGlobal string and pointer allocations are freed.
    /// </summary>
    public sealed class AllocatedNativeArgv : IDisposable
    {
        private IntPtr _outp = IntPtr.Zero;
        private IntPtr _argv = IntPtr.Zero;
        private readonly List<IntPtr> _stringPointers = new List<IntPtr>();
        private readonly string[] _managedArgs;
        private bool _disposed;

        public IntPtr OutFilePathPointer => _outp;
        public IntPtr ArgvPointer => _argv;
        public int Argc => _stringPointers.Count;
        public IReadOnlyList<string> ManagedArgs => _managedArgs;

        public AllocatedNativeArgv(string outFilePath, string[] args)
        {
            if (args == null) throw new ArgumentNullException(nameof(args));
            _managedArgs = (string[])args.Clone();

            try
            {
                if (!string.IsNullOrEmpty(outFilePath))
                {
                    _outp = Marshal.StringToHGlobalAnsi(outFilePath);
                }

                // Allocate array of pointers: (argc + 1) * IntPtr.Size to hold null terminator
                _argv = Marshal.AllocHGlobal(IntPtr.Size * (args.Length + 1));

                for (int i = 0; i < args.Length; i++)
                {
                    IntPtr strPtr = Marshal.StringToHGlobalAnsi(args[i]);
                    _stringPointers.Add(strPtr);
                    Marshal.WriteIntPtr(_argv, i * IntPtr.Size, strPtr);
                }

                // Standard C main convention: null-terminated argv array
                Marshal.WriteIntPtr(_argv, args.Length * IntPtr.Size, IntPtr.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_outp != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_outp);
                _outp = IntPtr.Zero;
            }

            for (int i = 0; i < _stringPointers.Count; i++)
            {
                IntPtr p = _stringPointers[i];
                if (p != IntPtr.Zero)
                {
                    Marshal.FreeHGlobal(p);
                }
            }
            _stringPointers.Clear();

            if (_argv != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(_argv);
                _argv = IntPtr.Zero;
            }
        }
    }

    /// <summary>
    /// Deterministic, whitelist-only argument builder for the native QEMU engine.
    /// Strictly prohibits arbitrary user command arguments, shell injection, or unknown flags.
    /// </summary>
    public static class QemuArgvBuilder
    {
        /// <summary>
        /// Generates the fixed command-line argument list according to Xbox Dev Mode parameters.
        /// </summary>
        public static string[] BuildArgumentList(QemuConfiguration config)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();

            var list = new List<string>
            {
                "qemu-system-x86_64",
                "-L", config.BiosDirectory ?? "pc-bios",
                "-M", "pc,usb=off,hpet=off",
                "-accel", "tcg,thread=" + (config.CpuCount > 1 ? "multi" : "single") + ",tb-size=" + config.TbCacheMegabytes,
                "-cpu", config.CpuModel,
                "-smp", config.CpuCount.ToString(),
                "-m", config.RamMegabytes.ToString() + "M",
                "-nodefaults",
                "-monitor", "none",
                "-serial", "none"
            };

            // Graphics pipeline
            if (config.GpuMode == QemuGpuMode.GpuDirectAngle)
            {
                list.AddRange(new string[]
                {
                    "-display", "egl-headless,gl=es",
                    "-device", "virtio-vga-gl,edid=on,xres=" + config.DisplayWidth + ",yres=" + config.DisplayHeight
                });
            }
            else
            {
                list.AddRange(new string[]
                {
                    "-display", "none",
                    "-device", "VGA,edid=on,xres=" + config.DisplayWidth + ",yres=" + config.DisplayHeight
                });
            }

            // Networking: SLIRP user NAT with host port forwarding to guest SSH (port 22)
            // addr=0x10 ensures stable PCI slot assignment (ens16) across disk changes
            list.AddRange(new string[]
            {
                "-netdev", "user,id=n0,hostfwd=tcp::" + config.SshHostPort + "-:22",
                "-device", "virtio-net-pci,netdev=n0,romfile=,addr=0x10"
            });

            // Storage: caller-provisioned owned fresh sparse raw root disk on virtio-blk-pci with dedicated iothread
            list.AddRange(new string[]
            {
                "-drive", "file=" + config.RootDiskPath + ",format=raw,if=none,id=hd0,cache=writeback",
                "-object", "iothread,id=io0",
                "-device", "virtio-blk-pci,drive=hd0,serial=" + config.DiskSerial + ",iothread=io0,bootindex=0"
            });

            // Input: Virtio absolute tablet
            list.AddRange(new string[]
            {
                "-device", "virtio-tablet-pci"
            });

            // Optional: read-only vvfat shared folder
            if (!string.IsNullOrEmpty(config.SharedFolderPath))
            {
                string escapedDir = config.SharedFolderPath.Replace(",", ",,");
                list.AddRange(new string[]
                {
                    "-drive", "if=none,id=sh0,driver=vvfat,dir=" + escapedDir + ",label=SHARED,readonly=on",
                    "-device", "virtio-blk-pci,drive=sh0,serial=SHARED"
                });
            }

            return list.ToArray();
        }

        /// <summary>
        /// Constructs an AllocatedNativeArgv instance ready for passing to qemu_dll_main.
        /// Caller is responsible for disposing the returned instance.
        /// </summary>
        public static AllocatedNativeArgv BuildNativeArgv(QemuConfiguration config)
        {
            string[] args = BuildArgumentList(config);
            return new AllocatedNativeArgv(config.OutLogPath, args);
        }
    }
}
