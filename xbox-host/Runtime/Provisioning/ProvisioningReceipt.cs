// ------------------------------------------------------------------------------------------------
// DroidDeck Xbox Host Backend: Provisioning Receipt
//
// Persistent metadata receipt documenting provisioned disk parameters and boot protection flags.
// NOTE: This receipt is an unauthenticated, non-tamper-evident JSON record for state tracking
// and boot protection, not a cryptographic attestation.
// ------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    /// <summary>
    /// Persistent metadata receipt created atomically upon successful sparse disk provisioning.
    /// Documents verified source hash, size, and guest boot flags to avoid re-reading or re-hashing
    /// mutable filesystem data after boot.
    /// NOTE: Unauthenticated plaintext JSON; not tamper-evident.
    /// </summary>
    public sealed class ProvisioningReceipt
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>
        /// Verified SHA-512 cryptographic hash of the uncompressed public source image.
        /// </summary>
        public string SourceSha512 { get; set; }

        /// <summary>
        /// Source raw image size in bytes as verified during decompression.
        /// </summary>
        public long ExpectedSourceSizeBytes { get; set; }

        /// <summary>
        /// Configured logical target disk size in bytes (e.g. 8 GiB).
        /// </summary>
        public long TargetLogicalBytes { get; set; }

        /// <summary>
        /// Disk format identifier (always "raw").
        /// </summary>
        public string RawFormat { get; set; } = BackendProvisionerConstants.RawFormat;

        /// <summary>
        /// ISO 8601 UTC timestamp of original disk provisioning.
        /// </summary>
        public string CreatedAtUtc { get; set; }

        /// <summary>
        /// ISO 8601 UTC timestamp of most recent validation check.
        /// </summary>
        public string LastVerifiedAtUtc { get; set; }

        /// <summary>
        /// Protection flag set explicitly by the host runner BEFORE first QEMU boot attempt.
        /// Once true, this disk contains private guest state and must NEVER be deleted or modified
        /// by the provisioner, even across host application updates or failed boots.
        /// </summary>
        public bool GuestBootAttempted { get; set; }

        /// <summary>
        /// ISO 8601 UTC timestamp of first or latest recorded VM boot initiation.
        /// </summary>
        public string LastBootAttemptUtc { get; set; }

        /// <summary>
        /// Number of non-zero payload bytes physically written during sparse provisioning.
        /// </summary>
        public long NonZeroBytesWritten { get; set; }

        /// <summary>
        /// Number of zero bytes skipped (sparse holes punched) during provisioning.
        /// </summary>
        public long ZeroBytesSkipped { get; set; }

        /// <summary>
        /// True if NTFS FSCTL_SET_SPARSE was confirmed on the file handle.
        /// </summary>
        public bool IsSparseConfirmed { get; set; }

        /// <summary>
        /// Sparse chunk evaluation block size used during provisioning.
        /// </summary>
        public int ChunkSizeBytes { get; set; } = BackendProvisionerConstants.ChunkSizeBytes;

        /// <summary>
        /// Evaluates whether this receipt matches required pinned deployment constants.
        /// </summary>
        public bool Matches(string expectedSha512, long expectedLogicalBytes, string expectedFormat = BackendProvisionerConstants.RawFormat)
        {
            if (string.IsNullOrWhiteSpace(SourceSha512))
            {
                return false;
            }

            if (!string.Equals(SourceSha512.Trim(), expectedSha512.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (TargetLogicalBytes != expectedLogicalBytes)
            {
                return false;
            }

            if (!string.Equals(RawFormat, expectedFormat, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        /// <summary>
        /// Serializes this receipt to formatted JSON.
        /// </summary>
        public string ToJson()
        {
            return JsonSerializer.Serialize(this, JsonOptions);
        }

        /// <summary>
        /// Deserializes a receipt from a JSON string.
        /// </summary>
        public static ProvisioningReceipt FromJson(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonSerializer.Deserialize<ProvisioningReceipt>(json, JsonOptions);
        }

        /// <summary>
        /// Reads and deserializes a receipt file from disk. Returns null if file does not exist.
        /// </summary>
        public static ProvisioningReceipt LoadFromFile(string receiptPath)
        {
            if (string.IsNullOrWhiteSpace(receiptPath) || !File.Exists(receiptPath))
            {
                return null;
            }

            string json = File.ReadAllText(receiptPath);
            return FromJson(json);
        }

        /// <summary>
        /// Atomically saves the receipt to disk by writing to a temporary file, flushing,
        /// and renaming/replacing to the destination path.
        /// Preserves prior receipt if replacement fails; never deletes existing receipt as fallback.
        /// </summary>
        public static void SaveAtomic(string receiptPath, ProvisioningReceipt receipt)
        {
            if (string.IsNullOrWhiteSpace(receiptPath))
            {
                throw new ArgumentException("Receipt path cannot be null or empty.", nameof(receiptPath));
            }

            if (receipt == null)
            {
                throw new ArgumentNullException(nameof(receipt));
            }

            string directory = Path.GetDirectoryName(receiptPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            string tempPath = receiptPath + ".tmp." + Guid.NewGuid().ToString("N");
            try
            {
                string json = receipt.ToJson();
                using (FileStream stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (StreamWriter writer = new StreamWriter(stream))
                {
                    writer.Write(json);
                    writer.Flush();
                    stream.Flush(true);
                }

                if (File.Exists(receiptPath))
                {
                    // Atomic replacement: preserves existing receipt if replacement fails.
                    // Under no circumstances should receiptPath be deleted as a fallback.
                    // Failure to replace means the existing receipt remains intact and no boot is permitted.
                    File.Replace(tempPath, receiptPath, null);
                }
                else
                {
                    File.Move(tempPath, receiptPath);
                }
            }
            finally
            {
                // Ensure temporary file cleanup on any failure
                try
                {
                    if (File.Exists(tempPath))
                    {
                        File.Delete(tempPath);
                    }
                }
                catch
                {
                    // Best effort cleanup
                }
            }
        }
    }
}
