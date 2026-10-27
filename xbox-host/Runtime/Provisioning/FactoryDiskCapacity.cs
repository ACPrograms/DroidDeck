using System;
using System.IO;

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    internal static class FactoryDiskCapacity
    {
        internal const long LegacyBytes = 8L * 1024 * 1024 * 1024;

        // Append capacity to the known factory disk without reading private guest contents.
        // Receipt-first validation and exclusive write access prevent touching an active VM.
        internal static bool UpgradeLegacyDisk(string diskPath, string receiptPath)
        {
            if (!File.Exists(diskPath) || !File.Exists(receiptPath)) return false;
            var receipt = ProvisioningReceipt.LoadFromFile(receiptPath);
            if (receipt == null || !receipt.Matches(BackendProvisionerConstants.PinnedRawSha512, LegacyBytes) ||
                receipt.ExpectedSourceSizeBytes != BackendProvisionerConstants.ExpectedSourceSizeBytes ||
                !receipt.IsSparseConfirmed) return false;
            long target = BackendProvisionerConstants.TargetLogicalSizeBytes;
            if (target <= LegacyBytes) throw new InvalidOperationException("Capacity upgrade must only grow the disk.");
            if ((File.GetAttributes(diskPath) & FileAttributes.SparseFile) == 0)
                throw new IOException("Factory disk is not marked sparse; preserving it unchanged.");
            // Also accept the expanded length after an interrupted atomic receipt update.
            long length = new FileInfo(diskPath).Length;
            if (length != LegacyBytes && length != target)
                throw new IOException("Unexpected factory disk length; preserving it unchanged.");
            using (var disk = new FileStream(diskPath, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                if (disk.Length != length) throw new IOException("Disk length changed during capacity upgrade.");
                if (length == LegacyBytes) disk.SetLength(target);
                disk.Flush(true);
            }
            if (new FileInfo(diskPath).Length != target) throw new IOException("Capacity upgrade was not committed.");
            receipt.TargetLogicalBytes = target;
            // This timestamp validates metadata only. The source hash remains the initial PUBLIC image hash.
            receipt.LastVerifiedAtUtc = DateTimeOffset.UtcNow.ToString("O");
            ProvisioningReceipt.SaveAtomic(receiptPath, receipt);
            return true;
        }
    }
}
