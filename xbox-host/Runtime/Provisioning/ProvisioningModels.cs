// ------------------------------------------------------------------------------------------------
// DroidDeck Xbox Host Backend: Provisioning Models
//
// Status, progress, and result data structures for safe, bounded backend provisioning.
// ------------------------------------------------------------------------------------------------

using System;

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    /// <summary>
    /// Lifecycle states of the backend storage provisioner.
    /// </summary>
    public enum ProvisioningState
    {
        /// <summary>
        /// Provisioner is idle and awaiting invocation.
        /// </summary>
        Idle,

        /// <summary>
        /// Inspecting storage for an existing disk and validating receipt metadata.
        /// </summary>
        CheckingExisting,

        /// <summary>
        /// A matching existing disk was detected, verified against receipt, and reused.
        /// </summary>
        ReusedExisting,

        /// <summary>
        /// Invoking caller-provided public stream opener to begin decompression.
        /// </summary>
        OpeningSource,

        /// <summary>
        /// Decompressing public raw GZip stream and allocating NTFS sparse blocks.
        /// </summary>
        DecompressingAndAllocating,

        /// <summary>
        /// Atomically committing the provisioning verification receipt to disk.
        /// </summary>
        WritingReceipt,

        /// <summary>
        /// Backend root disk is verified, provisioned, and ready for VM launch.
        /// </summary>
        Ready,

        /// <summary>
        /// Provisioning encountered an unrecoverable failure or metadata mismatch.
        /// </summary>
        Faulted,

        /// <summary>
        /// Provisioning was explicitly cancelled via CancellationToken.
        /// </summary>
        Cancelled
    }

    /// <summary>
    /// Outcome classification for a provisioning run.
    /// </summary>
    public enum ProvisionDisposition
    {
        /// <summary>
        /// Existing disk was verified against receipt and reused without disk writes.
        /// </summary>
        ReusedExisting,

        /// <summary>
        /// Fresh sparse raw disk was provisioned, hash-verified, and receipt committed.
        /// </summary>
        FreshlyProvisioned,

        /// <summary>
        /// Operation faulted due to I/O, hash failure, or safety mismatch.
        /// </summary>
        Failed,

        /// <summary>
        /// Operation was cancelled before completion.
        /// </summary>
        Cancelled
    }

    /// <summary>
    /// Bounded progress report delivered during backend provisioning.
    /// Contains no secret credentials, auth memory, or guest logs.
    /// </summary>
    public sealed class BackendProvisionProgress
    {
        /// <summary>
        /// Current lifecycle state.
        /// </summary>
        public ProvisioningState State { get; set; }

        /// <summary>
        /// Concise, non-sensitive human-readable status description.
        /// </summary>
        public string StatusMessage { get; set; }

        /// <summary>
        /// Number of uncompressed source payload bytes processed so far.
        /// </summary>
        public long BytesRead { get; set; }

        /// <summary>
        /// Expected total uncompressed source bytes (3,221,225,472 for Debian Bookworm raw image).
        /// </summary>
        public long TotalBytes { get; set; }

        /// <summary>
        /// Non-zero payload bytes physically committed to disk.
        /// </summary>
        public long NonZeroBytesWritten { get; set; }

        /// <summary>
        /// Zero bytes skipped via sparse hole allocation.
        /// </summary>
        public long ZeroBytesSkipped { get; set; }

        /// <summary>
        /// Bounded percentage complete (0.0 to 100.0).
        /// </summary>
        public double PercentComplete { get; set; }

        /// <summary>
        /// Elapsed time since provisioning started.
        /// </summary>
        public long ElapsedMilliseconds { get; set; }
    }

    /// <summary>
    /// Final result of a backend provisioning invocation.
    /// </summary>
    public sealed class BackendProvisionResult
    {
        /// <summary>
        /// True if a valid backing disk is ready for guest boot (either freshly created or verified reuse).
        /// </summary>
        public bool Success { get; set; }

        /// <summary>
        /// Specific outcome disposition of the provisioning operation.
        /// </summary>
        public ProvisionDisposition Disposition { get; set; }

        /// <summary>
        /// Full absolute filesystem path of the verified root.raw disk.
        /// </summary>
        public string DiskPath { get; set; }

        /// <summary>
        /// Full absolute filesystem path of the atomic root.raw.receipt.json record.
        /// </summary>
        public string ReceiptPath { get; set; }

        /// <summary>
        /// Logical capacity of the disk in bytes (8,589,934,592 bytes for 8 GiB).
        /// </summary>
        public long LogicalSizeBytes { get; set; }

        /// <summary>
        /// Total uncompressed source bytes read and verified.
        /// </summary>
        public long SourceBytesRead { get; set; }

        /// <summary>
        /// Non-zero bytes physically written to sparse storage.
        /// </summary>
        public long NonZeroBytesWritten { get; set; }

        /// <summary>
        /// Zero bytes skipped as sparse holes.
        /// </summary>
        public long ZeroBytesSkipped { get; set; }

        /// <summary>
        /// Calculated or verified SHA-512 hex hash.
        /// </summary>
        public string Sha512 { get; set; }

        /// <summary>
        /// True if NTFS FSCTL_SET_SPARSE was confirmed on the file handle.
        /// </summary>
        public bool IsSparseConfirmed { get; set; }

        /// <summary>
        /// True if this disk has previously had a guest boot attempted.
        /// </summary>
        public bool GuestBootAttempted { get; set; }

        /// <summary>
        /// Elapsed duration in milliseconds.
        /// </summary>
        public long ElapsedMilliseconds { get; set; }

        /// <summary>
        /// Error description if provisioning failed; otherwise null.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Factory for a successful fresh provisioning run.
        /// </summary>
        public static BackendProvisionResult SuccessResult(
            string diskPath,
            string receiptPath,
            ProvisioningReceipt receipt,
            long elapsedMs)
        {
            return new BackendProvisionResult
            {
                Success = true,
                Disposition = ProvisionDisposition.FreshlyProvisioned,
                DiskPath = diskPath,
                ReceiptPath = receiptPath,
                LogicalSizeBytes = receipt != null ? receipt.TargetLogicalBytes : BackendProvisionerConstants.TargetLogicalSizeBytes,
                SourceBytesRead = receipt != null ? receipt.ExpectedSourceSizeBytes : BackendProvisionerConstants.ExpectedSourceSizeBytes,
                NonZeroBytesWritten = receipt != null ? receipt.NonZeroBytesWritten : 0,
                ZeroBytesSkipped = receipt != null ? receipt.ZeroBytesSkipped : 0,
                Sha512 = receipt != null ? receipt.SourceSha512 : BackendProvisionerConstants.PinnedRawSha512,
                IsSparseConfirmed = receipt != null && receipt.IsSparseConfirmed,
                GuestBootAttempted = receipt != null && receipt.GuestBootAttempted,
                ElapsedMilliseconds = elapsedMs
            };
        }

        /// <summary>
        /// Factory for safely reusing an existing verified disk without rewrite.
        /// </summary>
        public static BackendProvisionResult ReusedResult(
            string diskPath,
            string receiptPath,
            ProvisioningReceipt receipt,
            long elapsedMs = 0)
        {
            return new BackendProvisionResult
            {
                Success = true,
                Disposition = ProvisionDisposition.ReusedExisting,
                DiskPath = diskPath,
                ReceiptPath = receiptPath,
                LogicalSizeBytes = receipt != null ? receipt.TargetLogicalBytes : BackendProvisionerConstants.TargetLogicalSizeBytes,
                SourceBytesRead = receipt != null ? receipt.ExpectedSourceSizeBytes : BackendProvisionerConstants.ExpectedSourceSizeBytes,
                NonZeroBytesWritten = receipt != null ? receipt.NonZeroBytesWritten : 0,
                ZeroBytesSkipped = receipt != null ? receipt.ZeroBytesSkipped : 0,
                Sha512 = receipt != null ? receipt.SourceSha512 : BackendProvisionerConstants.PinnedRawSha512,
                IsSparseConfirmed = receipt != null && receipt.IsSparseConfirmed,
                GuestBootAttempted = receipt != null && receipt.GuestBootAttempted,
                ElapsedMilliseconds = elapsedMs
            };
        }

        /// <summary>
        /// Factory for a failed provisioning attempt.
        /// </summary>
        public static BackendProvisionResult FaultedResult(
            string diskPath,
            string receiptPath,
            string errorMessage,
            long elapsedMs = 0)
        {
            return new BackendProvisionResult
            {
                Success = false,
                Disposition = ProvisionDisposition.Failed,
                DiskPath = diskPath,
                ReceiptPath = receiptPath,
                ErrorMessage = errorMessage,
                ElapsedMilliseconds = elapsedMs
            };
        }

        /// <summary>
        /// Factory for a cancelled provisioning attempt.
        /// </summary>
        public static BackendProvisionResult CancelledResult(
            string diskPath,
            string receiptPath,
            string message = "Provisioning was cancelled.",
            long elapsedMs = 0)
        {
            return new BackendProvisionResult
            {
                Success = false,
                Disposition = ProvisionDisposition.Cancelled,
                DiskPath = diskPath,
                ReceiptPath = receiptPath,
                ErrorMessage = message,
                ElapsedMilliseconds = elapsedMs
            };
        }
    }
}
