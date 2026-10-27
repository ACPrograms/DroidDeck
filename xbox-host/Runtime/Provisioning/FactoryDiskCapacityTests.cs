using System;
using System.IO;
using DroidDeck.XboxRuntime.Storage;

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    internal static class FactoryDiskCapacityTests
    {
        private static void Check(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }

        private static string Fixture(string directory, string name)
        {
            string path = Path.Combine(directory, name + ".raw");
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                int error;
                Check(SparseRawProvisioner.SetSparse(stream.SafeFileHandle, out error), "Sparse fixture setup failed: " + error);
                stream.SetLength(FactoryDiskCapacity.LegacyBytes);
                stream.WriteByte(0xA5);
                stream.Position = FactoryDiskCapacity.LegacyBytes - 1;
                stream.WriteByte(0x5A);
                stream.Flush(true);
            }
            ProvisioningReceipt.SaveAtomic(path + ".json", new ProvisioningReceipt
            {
                SourceSha512 = BackendProvisionerConstants.PinnedRawSha512,
                ExpectedSourceSizeBytes = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                TargetLogicalBytes = FactoryDiskCapacity.LegacyBytes,
                IsSparseConfirmed = true,
                GuestBootAttempted = true,
                LastBootAttemptUtc = "2026-10-27T00:00:00Z"
            });
            return path;
        }

        internal static void Run(string directory)
        {
            string path = Fixture(directory, "capacity-owned");
            string receiptPath = path + ".json";
            using (var active = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
            {
                bool refused = false;
                try { FactoryDiskCapacity.UpgradeLegacyDisk(path, receiptPath); }
                catch (IOException) { refused = true; }
                Check(refused && active.Length == FactoryDiskCapacity.LegacyBytes, "Active disk must remain unchanged.");
                Check(ProvisioningReceipt.LoadFromFile(receiptPath).TargetLogicalBytes == FactoryDiskCapacity.LegacyBytes,
                    "Active disk receipt must remain unchanged.");
            }
            Check(FactoryDiskCapacity.UpgradeLegacyDisk(path, receiptPath), "Factory capacity migration must succeed.");
            Check(new FileInfo(path).Length == BackendProvisionerConstants.TargetLogicalSizeBytes, "Expected expanded length.");
            using (var fixture = File.OpenRead(path))
            {
                Check(fixture.ReadByte() == 0xA5, "Public fixture prefix must survive expansion.");
                fixture.Position = FactoryDiskCapacity.LegacyBytes - 1;
                Check(fixture.ReadByte() == 0x5A, "Public fixture last original byte must survive expansion.");
            }
            var grown = ProvisioningReceipt.LoadFromFile(receiptPath);
            Check(grown.GuestBootAttempted && grown.LastBootAttemptUtc == "2026-10-27T00:00:00Z",
                "Capacity migration must preserve guest boot protection/history.");
            Check(!FactoryDiskCapacity.UpgradeLegacyDisk(path, receiptPath), "Repeated completed migration must be a no-op.");
            grown.TargetLogicalBytes = FactoryDiskCapacity.LegacyBytes;
            ProvisioningReceipt.SaveAtomic(receiptPath, grown);
            Check(FactoryDiskCapacity.UpgradeLegacyDisk(path, receiptPath), "Interrupted receipt update must recover without shrinking.");

            string foreign = Fixture(directory, "capacity-foreign");
            var unknown = ProvisioningReceipt.LoadFromFile(foreign + ".json");
            unknown.SourceSha512 = "unknown";
            ProvisioningReceipt.SaveAtomic(foreign + ".json", unknown);
            string before = File.ReadAllText(foreign + ".json");
            Check(!FactoryDiskCapacity.UpgradeLegacyDisk(foreign, foreign + ".json"), "Unrecognized receipt must not authorize growth.");
            Check(new FileInfo(foreign).Length == FactoryDiskCapacity.LegacyBytes &&
                File.ReadAllText(foreign + ".json") == before, "Unrecognized disk and receipt must be preserved.");
        }
    }
}
