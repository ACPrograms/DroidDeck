using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using DroidDeck.XboxRuntime.Storage;

namespace DroidDeckXboxHost
{
    internal static class StoragePreflight
    {
        [DllImport("api-ms-win-core-file-l1-1-0.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode)]
        private static extern bool GetDiskFreeSpaceEx(string path, out ulong available, out ulong total, out ulong free);

        [DllImport("api-ms-win-core-file-l2-1-0.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
        private static extern bool GetFileInformationByHandleEx(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int kind, byte[] buffer, uint size);

        private static long? Allocated(FileStream file)
        {
            byte[] info = new byte[24];
            return GetFileInformationByHandleEx(file.SafeFileHandle, 1, info, 24) ? (long?)BitConverter.ToInt64(info, 0) : null;
        }

        private static ulong? Available(string path)
        {
            ulong available, total, free;
            return GetDiskFreeSpaceEx(path, out available, out total, out free) ? (ulong?)available : null;
        }

        internal static Task RunIfRequestedAsync()
        {
            string local = ApplicationData.Current.LocalFolder.Path;
            string request = Path.Combine(local, "storage-preflight.request");
            if (!File.Exists(request)) return Task.CompletedTask;
            File.Delete(request);
            return Task.Run(() => Run(local));
        }

        private static void Run(string local)
        {
            string directory = Path.Combine(local, "public-storage-test-" + Guid.NewGuid().ToString("N"));
            string source = Path.Combine(directory, "public-fixture.raw");
            string disk = Path.Combine(directory, "prepared.raw");
            bool ownSource = false, ownDisk = false;
            int? error = null, cleanupError = null;
            var writes = new List<object>();
            ulong? dataFreeBefore = Available(local);
            ulong? packageFree = Available(Package.Current.InstalledLocation.Path);
            ProvisionResult provision = null;
            ulong? dataFreeProvisioned = null;
            long? allocatedProvisioned = null;
            try
            {
                Directory.CreateDirectory(directory);
                byte[] pattern = new byte[4096];
                for (int i = 0; i < pattern.Length; i++) pattern[i] = 0xA5;
                using (var file = new FileStream(source, FileMode.CreateNew, FileAccess.Write))
                {
                    ownSource = true;
                    file.Write(pattern, 0, pattern.Length);
                }
                string hash;
                using (var sha = SHA512.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(pattern)).Replace("-", "").ToLowerInvariant();
                provision = new SparseRawProvisioner().ProvisionRawImage(source, disk, new ProvisionOptions
                {
                    ExpectedSourceSizeBytes = pattern.Length,
                    ExpectedSha512 = hash,
                    TargetLogicalSizeBytes = 8L * 1024 * 1024 * 1024
                });
                ownDisk = provision.Success;
                if (ownDisk)
                {
                    using (var file = new FileStream(disk, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                    {
                        dataFreeProvisioned = Available(local);
                        allocatedProvisioned = Allocated(file);
                        foreach (long offset in new long[] { 0, 3L * 1024 * 1024 * 1024, 5L * 1024 * 1024 * 1024 })
                        {
                            int? writeError = null;
                            bool verified = false;
                            try
                            {
                                file.Position = offset;
                                file.Write(pattern, 0, pattern.Length);
                                file.Flush(true);
                                file.Position = offset;
                                byte[] actual = new byte[pattern.Length];
                                verified = file.Read(actual, 0, actual.Length) == actual.Length;
                                for (int i = 0; i < actual.Length; i++) verified &= actual[i] == pattern[i];
                            }
                            catch (Exception ex) { writeError = ex.HResult; }
                            writes.Add(new { Offset = offset, Verified = verified, HResult = writeError, AllocationBytes = Allocated(file), DataFreeBytes = Available(local) });
                            if (writeError.HasValue) break;
                        }
                    }
                }
            }
            catch (Exception ex) { error = ex.HResult; }
            finally
            {
                try
                {
                    if (ownDisk) File.Delete(disk);
                    if (ownSource) File.Delete(source);
                    if (Directory.Exists(directory)) Directory.Delete(directory, false);
                }
                catch (Exception ex) { cleanupError = ex.HResult; }
            }
            File.WriteAllText(Path.Combine(local, "droiddeck-storage-preflight.json"), JsonSerializer.Serialize(new
            {
                PublicFixtureOnly = true,
                TargetLogicalBytes = 8L * 1024 * 1024 * 1024,
                DataFreeBefore = dataFreeBefore,
                PackageVolumeFreeBytes = packageFree,
                DataFreeAfterProvision = dataFreeProvisioned,
                AllocationAfterProvision = allocatedProvisioned,
                DataFreeAfterCleanup = Available(local),
                ProvisionSucceeded = provision != null && provision.Success,
                SparseConfirmed = provision != null && provision.IsSparseConfirmed,
                ProvisionError = provision == null ? null : provision.ErrorMessage,
                Writes = writes,
                HResult = error,
                CleanupHResult = cleanupError
            }));
        }
    }
}
