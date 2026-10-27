using System;

namespace DroidDeck.XboxRuntime.Storage
{
    /// <summary>
    /// Progress update reported during sparse image provisioning.
    /// </summary>
    public sealed class ProvisionProgress
    {
        public long BytesRead { get; set; }
        public long TotalBytes { get; set; }
        public long NonZeroBytesWritten { get; set; }
        public long ZeroBytesSkipped { get; set; }
        public double PercentComplete { get; set; }
    }

    /// <summary>
    /// Configuration options for the sparse raw provisioner.
    /// </summary>
    public sealed class ProvisionOptions
    {
        /// <summary>
        /// Logical file size in bytes for the target virtual disk.
        /// Default is 12 GiB (12,884,901,888 bytes).
        /// </summary>
        public long TargetLogicalSizeBytes { get; set; } = 12L * 1024 * 1024 * 1024;

        /// <summary>
        /// Chunk evaluation block size in bytes.
        /// Default is 64 KiB (65,536 bytes) per public granularity measurements.
        /// </summary>
        public int ChunkSizeBytes { get; set; } = 64 * 1024;

        /// <summary>
        /// Expected SHA-512 hex hash for the source raw image (128 hex characters).
        /// Required; cannot be null, empty, or whitespace.
        /// </summary>
        public string ExpectedSha512 { get; set; } = "422133ccb1f9975f62c230bdfb32be62cc2d223f86aa949be9632d411c7094b33cd2bc48b827bb93696b7b4552eb9854e9c21467f4cb1ca7875ddf77535ef84a";

        /// <summary>
        /// Expected total byte count for the source raw image.
        /// Default is 3 GiB (3,221,225,472 bytes) for official Debian generic-amd64 raw image.
        /// </summary>
        public long ExpectedSourceSizeBytes { get; set; } = 3221225472L;
    }

    /// <summary>
    /// Result metrics returned upon completion of sparse provisioning.
    /// </summary>
    public sealed class ProvisionResult
    {
        public bool Success { get; set; }
        public string SourcePath { get; set; }
        public string DestinationPath { get; set; }
        public string TemporaryPath { get; set; }
        public long LogicalSizeBytes { get; set; }
        public long TotalSourceBytesRead { get; set; }
        public long NonZeroBytesWritten { get; set; }
        public long ZeroBytesSkipped { get; set; }
        public int NonZeroChunksCount { get; set; }
        public int ZeroChunksCount { get; set; }
        public string CalculatedSha512 { get; set; }
        public bool Sha512Verified { get; set; }
        public bool IsSparseConfirmed { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public string ErrorMessage { get; set; }
    }
}
