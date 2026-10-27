// ------------------------------------------------------------------------------------------------
// DroidDeck Xbox Host Backend: Provisioning Constants
//
// Pinned official distribution image metadata and storage parameters for safe,
// idempotent Debian raw disk provisioning on Xbox Dev Mode.
// ------------------------------------------------------------------------------------------------

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    /// <summary>
    /// Constants and pinned verification metadata for DroidDeck backend disk provisioning.
    /// </summary>
    public static class BackendProvisionerConstants
    {
        /// <summary>
        /// Pinned SHA-512 cryptographic hash of the official uncompressed public Debian 12 Bookworm generic-amd64 raw cloud image.
        /// Build version: 20261026-2643.
        /// </summary>
        public const string PinnedRawSha512 = "422133ccb1f9975f62c230bdfb32be62cc2d223f86aa949be9632d411c7094b33cd2bc48b827bb93696b7b4552eb9854e9c21467f4cb1ca7875ddf77535ef84a";

        /// <summary>
        /// Exact byte length of the uncompressed source raw image: 3,221,225,472 bytes (3,072 MiB / 3 GiB).
        /// Any decompressed stream exceeding or falling short of this count is rejected immediately.
        /// </summary>
        public const long ExpectedSourceSizeBytes = 3221225472L;

        /// <summary>
        /// Target logical capacity for the sparse raw root disk: 17,179,869,184 bytes (16 GiB).
        /// Earlier 8 GiB factory disks are expanded in place by metadata-only capacity migration.
        /// </summary>
        public const long TargetLogicalSizeBytes = 16L * 1024 * 1024 * 1024; // 17,179,869,184 bytes

        /// <summary>
        /// Sparse evaluation block size: 64 KiB (65,536 bytes).
        /// Optimal balance between NTFS cluster alignment and sparse hole efficiency.
        /// </summary>
        public const int ChunkSizeBytes = 64 * 1024; // 65,536 bytes

        /// <summary>
        /// Default relative folder name for DroidDeck VM runtime assets.
        /// </summary>
        public const string RuntimeFolderName = "DroidDeckRuntime";

        /// <summary>
        /// Filename of the primary provisioned backing disk.
        /// </summary>
        public const string DiskFileName = "root.raw";

        /// <summary>
        /// Filename of the atomic provisioning receipt JSON record.
        /// </summary>
        public const string ReceiptFileName = "root.raw.receipt.json";

        /// <summary>
        /// Backing disk image format identifier.
        /// </summary>
        public const string RawFormat = "raw";
    }
}
