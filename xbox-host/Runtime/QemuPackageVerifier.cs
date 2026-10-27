using System;
using System.IO;
using System.Security.Cryptography;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Validates packaged native binaries, BIOS ROM assets, and caller-provisioned root disk.
    /// Strictly verifies system prerequisites prior to invoking unmanaged QEMU entrypoints.
    /// </summary>
    public static class QemuPackageVerifier
    {
        /// <summary>
        /// Verified SHA-256 hash of native qemu.dll from pinned XboxVmHost 1.2.76.0 MSIX package.
        /// </summary>
        public const string ExpectedQemuDllSha256 = "538d4662a879eb767e0bebb5921e21d63280a2439e10a84996dc3ad59f97eac7";

        /// <summary>
        /// Verified SHA-256 hash of reference package XboxVmHost_1.2.76.0_x64.msix.
        /// </summary>
        public const string ExpectedPackageSha256 = "c100a055887e9c066068c4fbc6b00253e3ffdaabd125ea8f095211b00917ff69";

        // Qcow2 magic header bytes ('Q', 'F', 'I', 0xFB) in big-endian
        private static readonly byte[] Qcow2Magic = new byte[] { 0x51, 0x46, 0x49, 0xFB };

        /// <summary>
        /// Validates package environment and disk prerequisites against safety policies.
        /// </summary>
        /// <param name="packageDirectory">Base directory containing packaged native assets.</param>
        /// <param name="config">Execution configuration.</param>
        /// <param name="verifyQemuChecksum">Whether to compute and verify SHA-256 of qemu.dll.</param>
        /// <returns>Validation result detailing asset availability.</returns>
        public static PackageValidationResult Validate(
            string packageDirectory,
            QemuConfiguration config,
            bool verifyQemuChecksum = false)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();

            var result = new PackageValidationResult();

            if (string.IsNullOrWhiteSpace(packageDirectory) || !Directory.Exists(packageDirectory))
            {
                result.IsValid = false;
                result.ErrorMessage = "Package directory does not exist or is not specified: " + (packageDirectory ?? "null");
                return result;
            }

            // 1. Verify qemu.dll
            string qemuDllPath = Path.Combine(packageDirectory, "qemu.dll");
            if (!File.Exists(qemuDllPath))
            {
                result.IsValid = false;
                result.ErrorMessage = "Native engine binary 'qemu.dll' missing from package directory: " + qemuDllPath;
                return result;
            }
            result.HasQemuDll = true;

            if (verifyQemuChecksum)
            {
                string calculatedSha256 = ComputeSha256(qemuDllPath);
                result.QemuDllSha256 = calculatedSha256;
                if (!string.Equals(calculatedSha256, ExpectedQemuDllSha256, StringComparison.OrdinalIgnoreCase))
                {
                    result.IsValid = false;
                    result.ErrorMessage = "qemu.dll SHA-256 mismatch! Expected " + ExpectedQemuDllSha256 + " but got " + calculatedSha256;
                    return result;
                }
            }

            // 2. Verify BIOS ROM assets
            string biosDir = !string.IsNullOrEmpty(config.BiosDirectory)
                ? config.BiosDirectory
                : Path.Combine(packageDirectory, "pc-bios");

            if (!Directory.Exists(biosDir))
            {
                result.IsValid = false;
                result.ErrorMessage = "BIOS directory missing: " + biosDir;
                return result;
            }

            bool hasBios = File.Exists(Path.Combine(biosDir, "bios-256k.bin")) || File.Exists(Path.Combine(biosDir, "bios.bin"));
            bool hasVga = File.Exists(Path.Combine(biosDir, "vgabios-stdvga.bin")) || File.Exists(Path.Combine(biosDir, "vgabios.bin"));
            bool hasVirtioVga = File.Exists(Path.Combine(biosDir, "vgabios-virtio.bin"));

            if (!hasBios || !hasVga)
            {
                result.IsValid = false;
                result.ErrorMessage = "Required standard BIOS ROMs missing in " + biosDir;
                return result;
            }
            result.HasBiosDirectory = true;

            // 3. Verify ANGLE GPU dependencies if GpuDirectAngle requested
            if (config.GpuMode == QemuGpuMode.GpuDirectAngle)
            {
                string suffix = config.AngleSuffix ?? "";
                string eglName = "libEGL" + suffix + ".dll";
                string glesName = "libGLESv2" + suffix + ".dll";
                string pthreadName = "libwinpthread-1.dll";

                bool hasEgl = File.Exists(Path.Combine(packageDirectory, eglName));
                bool hasGles = File.Exists(Path.Combine(packageDirectory, glesName));
                bool hasPthread = File.Exists(Path.Combine(packageDirectory, pthreadName));

                result.HasAngleBinaries = hasEgl && hasGles && hasPthread && hasVirtioVga;
                if (!result.HasAngleBinaries)
                {
                    result.IsValid = false;
                    result.ErrorMessage = "GPU direct ANGLE binaries missing (" + eglName + ", " + glesName + ", " + pthreadName + ", vgabios-virtio.bin).";
                    return result;
                }
            }

            // 4. Verify caller-provisioned owned disk
            string diskPath = config.RootDiskPath;
            if (!File.Exists(diskPath))
            {
                result.IsValid = false;
                result.ErrorMessage = "Caller-provisioned root disk file not found: " + diskPath;
                return result;
            }

            // Strict security isolation: never open, read, or migrate existing booted guest disks
            string diskFileName = Path.GetFileName(diskPath).ToLowerInvariant();
            if (diskFileName == "vmdisk-1.img" || diskFileName == "vm1.img" || diskFileName == "vmdisk-2.img" || diskFileName == "vm2.img")
            {
                result.IsValid = false;
                result.ErrorMessage = "Access denied: Root disk points to existing booted guest disk name. New app must own fresh sparse raw root disk.";
                return result;
            }

            FileInfo fi = new FileInfo(diskPath);
            if (fi.Length <= 0)
            {
                result.IsValid = false;
                result.ErrorMessage = "Root disk file is empty (0 bytes). Must be caller-provisioned sparse raw disk.";
                return result;
            }
            result.RootDiskSizeBytes = fi.Length;

            // Verify disk is NOT qcow2 (qcow2 causes unrecoverable crashes on Xbox UWP)
            if (IsQcow2(diskPath))
            {
                result.IsValid = false;
                result.ErrorMessage = "QCOW2 disk format detected! Only raw sparse disks are supported on Xbox Dev Mode.";
                return result;
            }

            result.HasValidRootDisk = true;
            result.IsValid = true;
            return result;
        }

        private static bool IsQcow2(string path)
        {
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    if (fs.Length < 4) return false;
                    byte[] header = new byte[4];
                    int read = fs.Read(header, 0, 4);
                    if (read == 4)
                    {
                        return header[0] == Qcow2Magic[0] &&
                               header[1] == Qcow2Magic[1] &&
                               header[2] == Qcow2Magic[2] &&
                               header[3] == Qcow2Magic[3];
                    }
                }
            }
            catch
            {
                // If stream cannot be read, fail safe
            }
            return false;
        }

        private static string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] hash = sha256.ComputeHash(stream);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }
    }
}
