// ------------------------------------------------------------------------------------------------
// DroidDeck Xbox Host Backend: Provisioner Tests & Self-Verification
//
// Standalone internal test suite for BackendProvisioner, receipts, and invariant contracts.
// Free of WinRT references and external test framework dependencies for full UWP .NET Native
// and desktop test runner compatibility.
// ------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DroidDeck.XboxRuntime.Storage;

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    /// <summary>
    /// Self-contained verification tests for backend disk provisioning and receipt safety invariants.
    /// Free of WinRT references and external test frameworks.
    /// </summary>
    internal static class BackendProvisionerTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion failed: " + message);
            }
        }

        /// <summary>
        /// Runs all non-destructive in-memory and temporary-file unit tests.
        /// </summary>
        public static async Task<bool> RunAllTestsAsync(string testBaseDir = null)
        {
            string testDir = !string.IsNullOrWhiteSpace(testBaseDir)
                ? Path.Combine(testBaseDir, "backend-prov-tests-" + Guid.NewGuid().ToString("N"))
                : Path.Combine(Path.GetTempPath(), "backend-prov-tests-" + Guid.NewGuid().ToString("N"));

            try
            {
                Directory.CreateDirectory(testDir);

                TestReceiptSerialization();
                TestReceiptMatching();
                TestAtomicReceiptFileOperations(testDir);
                TestAtomicFailurePreservesReceipt(testDir);
                await TestReusedExistingDiskAsync(testDir);
                await TestConcurrentBootProtectionAsync(testDir);
                await TestRejectUnmanagedDiskAsync(testDir);
                await TestGuestBootAttemptProtectionAsync(testDir);
                await TestStreamOpenerValidationAsync(testDir);
                await TestGZipStreamPipelineAsync(testDir);

                return true;
            }
            finally
            {
                try
                {
                    if (Directory.Exists(testDir))
                    {
                        Directory.Delete(testDir, true);
                    }
                }
                catch
                {
                    // Best effort cleanup of test fixtures
                }
            }
        }

        private static void TestReceiptSerialization()
        {
            ProvisioningReceipt receipt = new ProvisioningReceipt
            {
                SourceSha512 = BackendProvisionerConstants.PinnedRawSha512,
                ExpectedSourceSizeBytes = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                TargetLogicalBytes = BackendProvisionerConstants.TargetLogicalSizeBytes,
                RawFormat = BackendProvisionerConstants.RawFormat,
                CreatedAtUtc = "2026-10-27T10:00:00.0000000Z",
                LastVerifiedAtUtc = "2026-10-27T10:05:00.0000000Z",
                GuestBootAttempted = false,
                NonZeroBytesWritten = 1048576,
                ZeroBytesSkipped = 2097152,
                IsSparseConfirmed = true,
                ChunkSizeBytes = 65536
            };

            string json = receipt.ToJson();
            Assert(!string.IsNullOrWhiteSpace(json), "Serialized JSON must not be empty.");
            Assert(json.Contains(BackendProvisionerConstants.PinnedRawSha512), "JSON must contain pinned hash.");

            ProvisioningReceipt deserialized = ProvisioningReceipt.FromJson(json);
            Assert(deserialized != null, "Deserialized receipt must not be null.");
            Assert(deserialized.SourceSha512 == receipt.SourceSha512, "Hash must roundtrip.");
            Assert(deserialized.ExpectedSourceSizeBytes == receipt.ExpectedSourceSizeBytes, "Source size must roundtrip.");
            Assert(deserialized.TargetLogicalBytes == receipt.TargetLogicalBytes, "Logical size must roundtrip.");
            Assert(deserialized.GuestBootAttempted == receipt.GuestBootAttempted, "Boot attempted flag must roundtrip.");
            Assert(deserialized.IsSparseConfirmed == receipt.IsSparseConfirmed, "Sparse flag must roundtrip.");
        }

        private static void TestReceiptMatching()
        {
            ProvisioningReceipt receipt = new ProvisioningReceipt
            {
                SourceSha512 = BackendProvisionerConstants.PinnedRawSha512,
                TargetLogicalBytes = BackendProvisionerConstants.TargetLogicalSizeBytes,
                RawFormat = BackendProvisionerConstants.RawFormat
            };

            Assert(receipt.Matches(BackendProvisionerConstants.PinnedRawSha512, BackendProvisionerConstants.TargetLogicalSizeBytes),
                "Receipt must match identical parameters.");

            Assert(!receipt.Matches("badhash00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000",
                BackendProvisionerConstants.TargetLogicalSizeBytes), "Must reject mismatched hash.");

            Assert(!receipt.Matches(BackendProvisionerConstants.PinnedRawSha512, 1024),
                "Must reject mismatched logical size.");

            Assert(!receipt.Matches(BackendProvisionerConstants.PinnedRawSha512, BackendProvisionerConstants.TargetLogicalSizeBytes, "qcow2"),
                "Must reject non-raw format.");
        }

        private static void TestAtomicReceiptFileOperations(string testDir)
        {
            string receiptPath = Path.Combine(testDir, "atomic-test.receipt.json");
            ProvisioningReceipt receipt = new ProvisioningReceipt
            {
                SourceSha512 = BackendProvisionerConstants.PinnedRawSha512,
                TargetLogicalBytes = BackendProvisionerConstants.TargetLogicalSizeBytes,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O")
            };

            ProvisioningReceipt.SaveAtomic(receiptPath, receipt);
            Assert(File.Exists(receiptPath), "Receipt file must exist after SaveAtomic.");

            ProvisioningReceipt loaded = ProvisioningReceipt.LoadFromFile(receiptPath);
            Assert(loaded != null, "Loaded receipt must not be null.");
            Assert(loaded.SourceSha512 == BackendProvisionerConstants.PinnedRawSha512, "Loaded receipt hash must match.");

            // Update atomically
            loaded.GuestBootAttempted = true;
            loaded.LastBootAttemptUtc = DateTimeOffset.UtcNow.ToString("O");
            ProvisioningReceipt.SaveAtomic(receiptPath, loaded);

            ProvisioningReceipt reloaded = ProvisioningReceipt.LoadFromFile(receiptPath);
            Assert(reloaded.GuestBootAttempted == true, "Updated boot flag must persist.");
        }

        private static async Task TestReusedExistingDiskAsync(string testDir)
        {
            string runtimeDir = Path.Combine(testDir, "reuse-runtime");
            Directory.CreateDirectory(runtimeDir);

            string diskPath = Path.Combine(runtimeDir, BackendProvisionerConstants.DiskFileName);
            string receiptPath = Path.Combine(runtimeDir, BackendProvisionerConstants.ReceiptFileName);

            // Create synthetic 8 GiB disk file (sparse or zero-length simulation with SetLength)
            using (FileStream fs = new FileStream(diskPath, FileMode.CreateNew, FileAccess.Write))
            {
                fs.SetLength(BackendProvisionerConstants.TargetLogicalSizeBytes);
            }

            // Create matching receipt
            ProvisioningReceipt receipt = new ProvisioningReceipt
            {
                SourceSha512 = BackendProvisionerConstants.PinnedRawSha512,
                ExpectedSourceSizeBytes = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                TargetLogicalBytes = BackendProvisionerConstants.TargetLogicalSizeBytes,
                RawFormat = BackendProvisionerConstants.RawFormat,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                GuestBootAttempted = false,
                IsSparseConfirmed = true
            };
            ProvisioningReceipt.SaveAtomic(receiptPath, receipt);

            BackendProvisioner provisioner = new BackendProvisioner(runtimeDir);
            Assert(provisioner.IsProvisioned(), "IsProvisioned must report true for matching disk and receipt.");

            // EnsureProvisionedAsync should reuse without calling opener
            bool openerCalled = false;
            Func<Task<Stream>> opener = () =>
            {
                openerCalled = true;
                return Task.FromResult<Stream>(new MemoryStream());
            };

            BackendProvisionResult result = await provisioner.EnsureProvisionedAsync(opener);
            Assert(result.Success, "EnsureProvisionedAsync must succeed on valid existing disk.");
            Assert(result.Disposition == ProvisionDisposition.ReusedExisting, "Disposition must be ReusedExisting.");
            Assert(!openerCalled, "Stream opener must NOT be invoked when reusing existing disk.");
        }

        private static async Task TestRejectUnmanagedDiskAsync(string testDir)
        {
            string runtimeDir = Path.Combine(testDir, "unmanaged-runtime");
            Directory.CreateDirectory(runtimeDir);

            string diskPath = Path.Combine(runtimeDir, BackendProvisionerConstants.DiskFileName);

            // Create existing disk WITHOUT receipt
            using (FileStream fs = new FileStream(diskPath, FileMode.CreateNew, FileAccess.Write))
            {
                fs.SetLength(BackendProvisionerConstants.TargetLogicalSizeBytes);
            }

            BackendProvisioner provisioner = new BackendProvisioner(runtimeDir);
            Assert(!provisioner.IsProvisioned(), "IsProvisioned must be false without receipt.");

            BackendProvisionResult result = await provisioner.EnsureProvisionedAsync(() => Task.FromResult<Stream>(new MemoryStream()));
            Assert(!result.Success, "Must fail when existing unmanaged disk is found.");
            Assert(File.Exists(diskPath), "Existing unmanaged disk must NOT be deleted.");
        }

        private static async Task TestGuestBootAttemptProtectionAsync(string testDir)
        {
            string runtimeDir = Path.Combine(testDir, "boot-protection-runtime");
            Directory.CreateDirectory(runtimeDir);

            string diskPath = Path.Combine(runtimeDir, BackendProvisionerConstants.DiskFileName);
            string receiptPath = Path.Combine(runtimeDir, BackendProvisionerConstants.ReceiptFileName);

            using (FileStream fs = new FileStream(diskPath, FileMode.CreateNew, FileAccess.Write))
            {
                fs.SetLength(BackendProvisionerConstants.TargetLogicalSizeBytes);
            }

            ProvisioningReceipt receipt = new ProvisioningReceipt
            {
                SourceSha512 = BackendProvisionerConstants.PinnedRawSha512,
                ExpectedSourceSizeBytes = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                TargetLogicalBytes = BackendProvisionerConstants.TargetLogicalSizeBytes,
                RawFormat = BackendProvisionerConstants.RawFormat,
                CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                GuestBootAttempted = false
            };
            ProvisioningReceipt.SaveAtomic(receiptPath, receipt);

            BackendProvisioner provisioner = new BackendProvisioner(runtimeDir);
            Assert(!provisioner.GetReceipt().GuestBootAttempted, "Initial boot flag must be false.");

            bool marked = await provisioner.MarkBootAttemptedAsync();
            Assert(marked, "MarkBootAttemptedAsync must succeed.");
            Assert(provisioner.GetReceipt().GuestBootAttempted, "Boot attempted flag must be true after marking.");
            Assert(!string.IsNullOrEmpty(provisioner.GetReceipt().LastBootAttemptUtc), "LastBootAttemptUtc must be recorded.");

            // If disk is deleted but receipt records GuestBootAttempted == true, EnsureProvisionedAsync must refuse re-provision
            File.Delete(diskPath);
            BackendProvisionResult result = await provisioner.EnsureProvisionedAsync(() => Task.FromResult<Stream>(new MemoryStream()));
            Assert(!result.Success, "Must refuse to re-provision if guest boot was already attempted.");
            Assert(result.ErrorMessage.Contains("already attempted"), "Error must state that guest boot was already attempted.");
        }

        private static async Task TestStreamOpenerValidationAsync(string testDir)
        {
            string runtimeDir = Path.Combine(testDir, "stream-validation-runtime");
            Directory.CreateDirectory(runtimeDir);

            BackendProvisioner provisioner = new BackendProvisioner(runtimeDir);

            // Missing sources fail without producing a disk or receipt.
            var missing = await provisioner.EnsureProvisionedAsync(null);
            Assert(!missing.Success && missing.Disposition == ProvisionDisposition.Failed, "Missing source must fail.");

            // 2. Opener returning null stream returns Faulted result without creating disk or receipt
            BackendProvisionResult nullStreamResult = await provisioner.EnsureProvisionedAsync(() => Task.FromResult<Stream>(null));
            Assert(!nullStreamResult.Success, "Opener returning null stream must yield failure result.");
            Assert(nullStreamResult.Disposition == ProvisionDisposition.Failed, "Disposition must be Failed.");
            Assert(!File.Exists(provisioner.TargetDiskPath), "Target disk must not exist after opener null failure.");
            Assert(!File.Exists(provisioner.ReceiptPath), "Receipt must not exist after opener null failure.");

            // 3. Pre-cancelled token yields Cancelled result
            using (CancellationTokenSource cts = new CancellationTokenSource())
            {
                cts.Cancel();
                BackendProvisionResult cancelResult = await provisioner.EnsureProvisionedAsync(
                    () => Task.FromResult<Stream>(new MemoryStream(new byte[16])),
                    progress: null,
                    cancellationToken: cts.Token);

                Assert(!cancelResult.Success, "Cancelled operation must not succeed.");
                Assert(cancelResult.Disposition == ProvisionDisposition.Cancelled, "Disposition must be Cancelled.");
            }
        }

        private static async Task TestGZipStreamPipelineAsync(string testDir)
        {
            byte[] compressed;
            using (var buffer = new MemoryStream())
            {
                using (var gzip = new GZipStream(buffer, CompressionMode.Compress, true))
                    gzip.Write(new byte[] { 1, 2, 3, 4 }, 0, 4);
                compressed = buffer.ToArray();
            }
            string runtimeDir = Path.Combine(testDir, "short-public-gzip");
            var provisioner = new BackendProvisioner(runtimeDir);
            var result = await provisioner.EnsureProvisionedAsync(() => Task.FromResult<Stream>(new MemoryStream(compressed)));
            Assert(!result.Success, "Short public GZip cannot satisfy the pinned 3 GiB source contract.");
            Assert(!File.Exists(provisioner.TargetDiskPath), "Invalid public source committed a disk.");
            Assert(!File.Exists(provisioner.ReceiptPath), "Invalid public source committed a receipt.");
            Assert(Directory.GetFiles(runtimeDir, "*.tmp.*").Length == 0, "Invalid source leaked temporary storage.");
        }

        private static void TestAtomicFailurePreservesReceipt(string testDir)
        {
            string path = Path.Combine(testDir, "protected-receipt.json");
            var receipt = new ProvisioningReceipt { GuestBootAttempted = true };
            ProvisioningReceipt.SaveAtomic(path, receipt);
            bool failed = false;
            using (var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                receipt.GuestBootAttempted = false;
                try { ProvisioningReceipt.SaveAtomic(path, receipt); }
                catch (IOException) { failed = true; }
                catch (UnauthorizedAccessException) { failed = true; }
            }
            Assert(failed, "Replacing a receipt without delete sharing must fail.");
            Assert(ProvisioningReceipt.LoadFromFile(path).GuestBootAttempted, "Atomic failure lost existing boot protection.");
        }

        private static async Task TestConcurrentBootProtectionAsync(string testDir)
        {
            var provisioner = new BackendProvisioner(Path.Combine(testDir, "reuse-runtime"));
            var tasks = new Task[10];
            for (int i = 0; i < tasks.Length; i++)
                tasks[i] = i % 2 == 0 ? (Task)provisioner.EnsureProvisionedAsync(null) : provisioner.MarkBootAttemptedAsync();
            await Task.WhenAll(tasks);
            Assert(provisioner.GetReceipt().GuestBootAttempted, "Concurrent reuse lost boot protection.");
        }
    }
}
