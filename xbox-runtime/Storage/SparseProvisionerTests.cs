using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace DroidDeck.XboxRuntime.Storage
{
    /// <summary>
    /// Test suite for SparseRawProvisioner verifying byte correctness, hole punch behavior,
    /// destination preservation, and error cleanup.
    /// </summary>
    public static class SparseProvisionerTests
    {
        private static void Assert(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidOperationException("Assertion Failed: " + message);
            }
        }

        private static string ComputeSha512Hex(byte[] data)
        {
            using (SHA512 sha512 = SHA512.Create())
            {
                byte[] hash = sha512.ComputeHash(data);
                StringBuilder sb = new StringBuilder(hash.Length * 2);
                for (int i = 0; i < hash.Length; i++)
                {
                    sb.Append(hash[i].ToString("x2"));
                }
                return sb.ToString();
            }
        }

        public static int RunAllTests()
        {
            Console.WriteLine("=== Running SparseRawProvisioner Tests ===");

            TestIsChunkAllZero();
            Console.WriteLine("[PASS] TestIsChunkAllZero");

            TestSuccessByteAndHoleCorrectness();
            Console.WriteLine("[PASS] TestSuccessByteAndHoleCorrectness");

            TestExistingDestinationUnchanged();
            Console.WriteLine("[PASS] TestExistingDestinationUnchanged");

            TestChecksumFailureCleanup();
            Console.WriteLine("[PASS] TestChecksumFailureCleanup");

            TestCancellationCleanup();
            Console.WriteLine("[PASS] TestCancellationCleanup");

            TestMissingOrInvalidHash();
            Console.WriteLine("[PASS] TestMissingOrInvalidHash");

            TestOversizedStreamEarlyFailure();
            Console.WriteLine("[PASS] TestOversizedStreamEarlyFailure");

            TestInvalidOptions();
            Console.WriteLine("[PASS] TestInvalidOptions");

            Console.WriteLine("=== All 8 SparseRawProvisioner Tests Passed Successfully ===");
            return 0;
        }

        public static void TestIsChunkAllZero()
        {
            byte[] zeroChunk = new byte[65536];
            Assert(SparseRawProvisioner.IsChunkAllZero(zeroChunk, zeroChunk.Length), "All-zero chunk must return true");

            byte[] nonZeroFirst = (byte[])zeroChunk.Clone();
            nonZeroFirst[0] = 1;
            Assert(!SparseRawProvisioner.IsChunkAllZero(nonZeroFirst, nonZeroFirst.Length), "Non-zero at index 0 must return false");

            byte[] nonZeroWord = (byte[])zeroChunk.Clone();
            nonZeroWord[8] = 0x80;
            Assert(!SparseRawProvisioner.IsChunkAllZero(nonZeroWord, nonZeroWord.Length), "Non-zero at word boundary must return false");

            byte[] nonZeroLast = (byte[])zeroChunk.Clone();
            nonZeroLast[nonZeroLast.Length - 1] = 0xFF;
            Assert(!SparseRawProvisioner.IsChunkAllZero(nonZeroLast, nonZeroLast.Length), "Non-zero at last byte must return false");
        }

        public static void TestSuccessByteAndHoleCorrectness()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "sparse_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string source = Path.Combine(testDir, "source.raw");
                string dest = Path.Combine(testDir, "dest.raw");

                int chunkSize = 64 * 1024; // 64 KiB
                int totalSourceBytes = chunkSize * 4; // 256 KiB
                long targetLogicalBytes = 1024 * 1024; // 1 MiB target

                byte[] sourceData = new byte[totalSourceBytes];

                // Chunk 0: non-zero pattern
                for (int i = 0; i < chunkSize; i++) sourceData[i] = (byte)(i % 251 + 1);
                // Chunk 1: all zeroes
                // Chunk 2: non-zero pattern
                for (int i = chunkSize * 2; i < chunkSize * 3; i++) sourceData[i] = (byte)((i * 7) % 249 + 1);
                // Chunk 3: all zeroes

                File.WriteAllBytes(source, sourceData);
                string expectedSha512 = ComputeSha512Hex(sourceData);

                SparseRawProvisioner provisioner = new SparseRawProvisioner();
                ProvisionOptions options = new ProvisionOptions
                {
                    ChunkSizeBytes = chunkSize,
                    ExpectedSourceSizeBytes = totalSourceBytes,
                    TargetLogicalSizeBytes = targetLogicalBytes,
                    ExpectedSha512 = expectedSha512
                };

                ProvisionResult result = provisioner.ProvisionRawImage(source, dest, options);

                Assert(result.Success, "Provisioning must succeed: " + result.ErrorMessage);
                Assert(result.Sha512Verified, "SHA-512 must be verified");
                Assert(result.NonZeroChunksCount == 2, "Expected 2 non-zero chunks, got: " + result.NonZeroChunksCount);
                Assert(result.ZeroChunksCount == 2, "Expected 2 zero chunks, got: " + result.ZeroChunksCount);
                Assert(result.NonZeroBytesWritten == chunkSize * 2, "Expected 128 KiB non-zero bytes written");
                Assert(result.ZeroBytesSkipped == chunkSize * 2, "Expected 128 KiB zero bytes skipped");
                Assert(result.LogicalSizeBytes == targetLogicalBytes, "Logical size must be 1 MiB");

                FileInfo destInfo = new FileInfo(dest);
                Assert(destInfo.Exists, "Destination file must exist");
                Assert(destInfo.Length == targetLogicalBytes, "Destination file length must match target logical bytes");

                // Verify readback byte-for-byte
                byte[] readback = File.ReadAllBytes(dest);
                Assert(readback.Length == targetLogicalBytes, "Readback buffer length must match target logical bytes");

                // Chunk 0 readback match
                for (int i = 0; i < chunkSize; i++)
                {
                    Assert(readback[i] == sourceData[i], "Chunk 0 byte mismatch at " + i);
                }
                // Chunk 1 hole zeroes
                for (int i = chunkSize; i < chunkSize * 2; i++)
                {
                    Assert(readback[i] == 0, "Chunk 1 sparse hole must read back 0 at " + i);
                }
                // Chunk 2 readback match
                for (int i = chunkSize * 2; i < chunkSize * 3; i++)
                {
                    Assert(readback[i] == sourceData[i], "Chunk 2 byte mismatch at " + i);
                }
                // Chunk 3 hole zeroes
                for (int i = chunkSize * 3; i < chunkSize * 4; i++)
                {
                    Assert(readback[i] == 0, "Chunk 3 sparse hole must read back 0 at " + i);
                }
                // Tail sparse expansion (from 256 KiB up to 1 MiB) zeroes
                for (int i = chunkSize * 4; i < targetLogicalBytes; i++)
                {
                    Assert(readback[i] == 0, "Tail sparse range must read back 0 at " + i);
                }

                // Assert no temporary files remained
                string[] tmpFiles = Directory.GetFiles(testDir, "*.tmp.*");
                Assert(tmpFiles.Length == 0, "No temporary files must remain");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static void TestExistingDestinationUnchanged()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "sparse_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string source = Path.Combine(testDir, "source.raw");
                string dest = Path.Combine(testDir, "dest.raw");

                byte[] dummySource = new byte[65536];
                dummySource[0] = 0x42;
                File.WriteAllBytes(source, dummySource);

                byte[] existingDestContent = new byte[512];
                for (int i = 0; i < existingDestContent.Length; i++) existingDestContent[i] = 0xAA;
                File.WriteAllBytes(dest, existingDestContent);

                SparseRawProvisioner provisioner = new SparseRawProvisioner();
                ProvisionOptions options = new ProvisionOptions
                {
                    ChunkSizeBytes = 65536,
                    ExpectedSourceSizeBytes = dummySource.Length,
                    TargetLogicalSizeBytes = 1048576,
                    ExpectedSha512 = ComputeSha512Hex(dummySource)
                };

                ProvisionResult result = provisioner.ProvisionRawImage(source, dest, options);

                Assert(!result.Success, "Provisioner must return Success = false when destination already exists");
                Assert(!string.IsNullOrEmpty(result.ErrorMessage), "ErrorMessage must be populated on failure");

                byte[] destAfter = File.ReadAllBytes(dest);
                Assert(destAfter.Length == 512, "Pre-existing destination length must remain untouched");
                for (int i = 0; i < destAfter.Length; i++)
                {
                    Assert(destAfter[i] == 0xAA, "Pre-existing destination content must not be modified");
                }

                string[] tmpFiles = Directory.GetFiles(testDir, "*.tmp.*");
                Assert(tmpFiles.Length == 0, "No temporary files must remain");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static void TestChecksumFailureCleanup()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "sparse_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string source = Path.Combine(testDir, "source.raw");
                string dest = Path.Combine(testDir, "dest.raw");

                byte[] dummySource = new byte[65536];
                dummySource[0] = 0x55;
                File.WriteAllBytes(source, dummySource);

                SparseRawProvisioner provisioner = new SparseRawProvisioner();
                ProvisionOptions options = new ProvisionOptions
                {
                    ChunkSizeBytes = 65536,
                    ExpectedSourceSizeBytes = dummySource.Length,
                    TargetLogicalSizeBytes = 1048576,
                    ExpectedSha512 = new string('0', 128) // Valid 128-char hex but mismatched hash
                };

                ProvisionResult result = provisioner.ProvisionRawImage(source, dest, options);

                Assert(!result.Success, "Provisioner must return Success = false on checksum mismatch");
                Assert(!File.Exists(dest), "Destination must not exist on checksum failure");

                string[] tmpFiles = Directory.GetFiles(testDir, "*.tmp.*");
                Assert(tmpFiles.Length == 0, "Temporary files must be cleaned up on checksum failure");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static void TestCancellationCleanup()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "sparse_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string source = Path.Combine(testDir, "source.raw");
                string dest = Path.Combine(testDir, "dest.raw");

                byte[] dummySource = new byte[65536];
                File.WriteAllBytes(source, dummySource);

                SparseRawProvisioner provisioner = new SparseRawProvisioner();
                ProvisionOptions options = new ProvisionOptions
                {
                    ChunkSizeBytes = 65536,
                    ExpectedSourceSizeBytes = dummySource.Length,
                    TargetLogicalSizeBytes = 1048576,
                    ExpectedSha512 = ComputeSha512Hex(dummySource)
                };

                CancellationTokenSource cts = new CancellationTokenSource();
                cts.Cancel(); // Pre-cancelled

                ProvisionResult result = provisioner.ProvisionRawImage(source, dest, options, null, cts.Token);

                Assert(!result.Success, "Provisioner must return Success = false on cancellation");
                Assert(!File.Exists(dest), "Destination must not exist on cancellation");

                string[] tmpFiles = Directory.GetFiles(testDir, "*.tmp.*");
                Assert(tmpFiles.Length == 0, "Temporary files must be cleaned up on cancellation");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static void TestMissingOrInvalidHash()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "sparse_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string source = Path.Combine(testDir, "source.raw");
                string dest = Path.Combine(testDir, "dest.raw");

                byte[] dummySource = new byte[65536];
                File.WriteAllBytes(source, dummySource);

                SparseRawProvisioner provisioner = new SparseRawProvisioner();

                // 1. Null hash
                ProvisionOptions optNull = new ProvisionOptions { ExpectedSha512 = null, ExpectedSourceSizeBytes = 65536, TargetLogicalSizeBytes = 1048576 };
                ProvisionResult resNull = provisioner.ProvisionRawImage(source, dest, optNull);
                Assert(!resNull.Success, "Must reject null ExpectedSha512");
                Assert(!File.Exists(dest), "Destination must not exist");

                // 2. Empty hash
                ProvisionOptions optEmpty = new ProvisionOptions { ExpectedSha512 = "   ", ExpectedSourceSizeBytes = 65536, TargetLogicalSizeBytes = 1048576 };
                ProvisionResult resEmpty = provisioner.ProvisionRawImage(source, dest, optEmpty);
                Assert(!resEmpty.Success, "Must reject empty ExpectedSha512");
                Assert(!File.Exists(dest), "Destination must not exist");

                // 3. Short hash
                ProvisionOptions optShort = new ProvisionOptions { ExpectedSha512 = "abc123", ExpectedSourceSizeBytes = 65536, TargetLogicalSizeBytes = 1048576 };
                ProvisionResult resShort = provisioner.ProvisionRawImage(source, dest, optShort);
                Assert(!resShort.Success, "Must reject short ExpectedSha512");
                Assert(!File.Exists(dest), "Destination must not exist");

                // 4. Non-hex hash of length 128
                ProvisionOptions optNonHex = new ProvisionOptions { ExpectedSha512 = new string('z', 128), ExpectedSourceSizeBytes = 65536, TargetLogicalSizeBytes = 1048576 };
                ProvisionResult resNonHex = provisioner.ProvisionRawImage(source, dest, optNonHex);
                Assert(!resNonHex.Success, "Must reject non-hex ExpectedSha512");
                Assert(!File.Exists(dest), "Destination must not exist");

                string[] tmpFiles = Directory.GetFiles(testDir, "*.tmp.*");
                Assert(tmpFiles.Length == 0, "No temporary files must remain");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static void TestOversizedStreamEarlyFailure()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "sparse_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string source = Path.Combine(testDir, "source.raw");
                string dest = Path.Combine(testDir, "dest.raw");

                // Source is 128 KiB
                byte[] largeSource = new byte[128 * 1024];
                File.WriteAllBytes(source, largeSource);

                SparseRawProvisioner provisioner = new SparseRawProvisioner();
                // But ExpectedSourceSizeBytes is only 64 KiB
                ProvisionOptions options = new ProvisionOptions
                {
                    ChunkSizeBytes = 64 * 1024,
                    ExpectedSourceSizeBytes = 64 * 1024,
                    TargetLogicalSizeBytes = 1048576,
                    ExpectedSha512 = new string('a', 128)
                };

                ProvisionResult result = provisioner.ProvisionRawImage(source, dest, options);

                Assert(!result.Success, "Must reject oversized source image early");
                Assert(!File.Exists(dest), "Destination must not exist");

                string[] tmpFiles = Directory.GetFiles(testDir, "*.tmp.*");
                Assert(tmpFiles.Length == 0, "Temporary files must be cleaned up");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }

        public static void TestInvalidOptions()
        {
            string testDir = Path.Combine(Path.GetTempPath(), "sparse_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(testDir);

            try
            {
                string source = Path.Combine(testDir, "source.raw");
                string dest = Path.Combine(testDir, "dest.raw");

                byte[] dummy = new byte[65536];
                File.WriteAllBytes(source, dummy);

                SparseRawProvisioner provisioner = new SparseRawProvisioner();

                // Non-positive source size
                Assert(!provisioner.ProvisionRawImage(source, dest, new ProvisionOptions { ExpectedSourceSizeBytes = 0 }).Success, "Must reject ExpectedSourceSizeBytes = 0");
                Assert(!provisioner.ProvisionRawImage(source, dest, new ProvisionOptions { ExpectedSourceSizeBytes = -1 }).Success, "Must reject ExpectedSourceSizeBytes < 0");

                // Non-positive chunk size
                Assert(!provisioner.ProvisionRawImage(source, dest, new ProvisionOptions { ChunkSizeBytes = 0 }).Success, "Must reject ChunkSizeBytes = 0");
                Assert(!provisioner.ProvisionRawImage(source, dest, new ProvisionOptions { ChunkSizeBytes = -100 }).Success, "Must reject ChunkSizeBytes < 0");

                // Non-positive target size
                Assert(!provisioner.ProvisionRawImage(source, dest, new ProvisionOptions { TargetLogicalSizeBytes = 0 }).Success, "Must reject TargetLogicalSizeBytes = 0");
                Assert(!provisioner.ProvisionRawImage(source, dest, new ProvisionOptions { TargetLogicalSizeBytes = -5 }).Success, "Must reject TargetLogicalSizeBytes < 0");

                // Source > Target
                Assert(!provisioner.ProvisionRawImage(source, dest, new ProvisionOptions { ExpectedSourceSizeBytes = 2000, TargetLogicalSizeBytes = 1000 }).Success, "Must reject ExpectedSourceSizeBytes > TargetLogicalSizeBytes");

                Assert(!File.Exists(dest), "Destination must not exist");
                string[] tmpFiles = Directory.GetFiles(testDir, "*.tmp.*");
                Assert(tmpFiles.Length == 0, "No temporary files must remain");
            }
            finally
            {
                try { Directory.Delete(testDir, true); } catch { }
            }
        }
    }
}
