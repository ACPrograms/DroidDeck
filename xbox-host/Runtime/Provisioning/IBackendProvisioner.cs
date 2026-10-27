// ------------------------------------------------------------------------------------------------
// DroidDeck Xbox Host Backend: Provisioner Interface
//
// Contract for idempotent, verified guest disk provisioning on Xbox Dev Mode.
// ------------------------------------------------------------------------------------------------

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    /// <summary>
    /// Contract for provisioning and managing the backing Linux guest disk on Xbox Dev Mode.
    /// Guarantees that public OS streams are decompressed directly into sparse storage,
    /// verified against pinned hashes, and preserved once private guest execution commences.
    /// </summary>
    public interface IBackendProvisioner
    {
        /// <summary>
        /// Current state of the provisioner.
        /// </summary>
        ProvisioningState State { get; }

        /// <summary>
        /// Absolute path of the target root disk file ({RuntimeDirectory}/root.raw).
        /// </summary>
        string TargetDiskPath { get; }

        /// <summary>
        /// Absolute path of the atomic provisioning receipt record ({RuntimeDirectory}/root.raw.receipt.json).
        /// </summary>
        string ReceiptPath { get; }

        /// <summary>
        /// Absolute path of the runtime directory housing VM assets.
        /// </summary>
        string RuntimeDirectory { get; }

        /// <summary>
        /// Synchronously checks whether a valid, verified root disk already exists and matches the receipt.
        /// Checks FileInfo metadata only; never reads or hashes mutable disk contents.
        /// </summary>
        bool IsProvisioned();

        /// <summary>
        /// Reads and returns the existing provisioning receipt if present on disk; otherwise null.
        /// </summary>
        ProvisioningReceipt GetReceipt();

        /// <summary>
        /// Ensures that the backing root disk is provisioned and ready for VM boot.
        /// If a valid disk matching the receipt already exists, it is reused immediately without writes.
        /// If an unknown disk exists without a matching receipt, the operation fails without deleting the disk.
        /// If no disk exists, streams and decompresses the public image via caller-supplied publicStreamOpener.
        /// </summary>
        /// <param name="publicStreamOpener">Caller-controlled factory returning the public compressed raw image stream.</param>
        /// <param name="progress">Optional bounded progress callback.</param>
        /// <param name="cancellationToken">Cancellation token to abort provisioning.</param>
        Task<BackendProvisionResult> EnsureProvisionedAsync(
            Func<Task<Stream>> publicStreamOpener,
            IProgress<BackendProvisionProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Flags the provisioning receipt to record that a VM boot has been attempted.
        /// MUST be invoked by the host runner immediately before calling QEMU Start.
        /// Once set, the provisioner will NEVER delete or modify this disk under any circumstance.
        /// Serialized under the same semaphore as EnsureProvisionedAsync; verifies persistence before returning true.
        /// </summary>
        Task<bool> MarkBootAttemptedAsync(CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Synchronous variant of MarkBootAttemptedAsync.
        /// </summary>
        bool MarkBootAttempted();
    }
}
