// ------------------------------------------------------------------------------------------------
// DroidDeck Xbox Host Backend: Production Backend Provisioner
//
// Streams and decompresses public official Debian raw cloud images via caller-supplied factory,
// delegates sparse hole allocation directly to Storage SparseRawProvisioner via exact Stream signature,
// enforces pinned SHA-512 and 8 GiB capacity, and manages metadata receipts with strict protection
// for booted guest disks.
// ------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using DroidDeck.XboxRuntime.Storage;

namespace DroidDeckXboxHost.Runtime.Provisioning
{
    /// <summary>
    /// Production backend provisioner for DroidDeck Linux guest disk on Xbox Dev Mode.
    /// Operates on explicit runtime directory provided by host; zero WinRT or reflection dependencies.
    /// </summary>
    public sealed class BackendProvisioner : IBackendProvisioner
    {
        private readonly object _syncLock = new object();
        private readonly SemaphoreSlim _provisionLock = new SemaphoreSlim(1, 1);
        private readonly SparseRawProvisioner _sparseProvisioner;
        private volatile ProvisioningState _state = ProvisioningState.Idle;

        /// <inheritdoc />
        public ProvisioningState State
        {
            get { lock (_syncLock) return _state; }
            private set { lock (_syncLock) _state = value; }
        }

        /// <inheritdoc />
        public string TargetDiskPath { get; }

        /// <inheritdoc />
        public string ReceiptPath { get; }

        /// <inheritdoc />
        public string RuntimeDirectory { get; }

        /// <summary>
        /// Initializes a new instance of BackendProvisioner targeting an explicit runtime directory.
        /// </summary>
        /// <param name="runtimeDirectory">Explicit directory path supplied by the host runner (mandatory).</param>
        /// <param name="sparseProvisioner">Optional provisioner instance; defaults to new SparseRawProvisioner().</param>
        public BackendProvisioner(
            string runtimeDirectory,
            SparseRawProvisioner sparseProvisioner = null)
        {
            if (string.IsNullOrWhiteSpace(runtimeDirectory))
            {
                throw new ArgumentException("Runtime directory must be explicitly provided by the host.", nameof(runtimeDirectory));
            }

            RuntimeDirectory = runtimeDirectory;
            TargetDiskPath = Path.Combine(RuntimeDirectory, BackendProvisionerConstants.DiskFileName);
            ReceiptPath = Path.Combine(RuntimeDirectory, BackendProvisionerConstants.ReceiptFileName);

            _sparseProvisioner = sparseProvisioner ?? new SparseRawProvisioner();
        }

        /// <inheritdoc />
        public bool IsProvisioned()
        {
            lock (_syncLock)
            {
                if (!File.Exists(TargetDiskPath) || !File.Exists(ReceiptPath))
                {
                    return false;
                }

                try
                {
                    FileInfo info = new FileInfo(TargetDiskPath);
                    if (info.Length != BackendProvisionerConstants.TargetLogicalSizeBytes)
                    {
                        return false;
                    }

                    ProvisioningReceipt receipt = ProvisioningReceipt.LoadFromFile(ReceiptPath);
                    if (receipt == null)
                    {
                        return false;
                    }

                    return receipt.Matches(
                        BackendProvisionerConstants.PinnedRawSha512,
                        BackendProvisionerConstants.TargetLogicalSizeBytes);
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <inheritdoc />
        public ProvisioningReceipt GetReceipt()
        {
            lock (_syncLock)
            {
                try
                {
                    return ProvisioningReceipt.LoadFromFile(ReceiptPath);
                }
                catch
                {
                    return null;
                }
            }
        }

        /// <inheritdoc />
        public async Task<BackendProvisionResult> EnsureProvisionedAsync(
            Func<Task<Stream>> publicStreamOpener,
            IProgress<BackendProvisionProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try { await _provisionLock.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                State = ProvisioningState.Cancelled;
                return BackendProvisionResult.CancelledResult(TargetDiskPath, ReceiptPath);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                State = ProvisioningState.CheckingExisting;

                // 1. Check existing disk under strict safety contracts
                if (File.Exists(TargetDiskPath))
                {
                    if (File.Exists(ReceiptPath))
                    {
                        ProvisioningReceipt receipt = null;
                        try
                        {
                            receipt = ProvisioningReceipt.LoadFromFile(ReceiptPath);
                        }
                        catch (Exception ex)
                        {
                            State = ProvisioningState.Faulted;
                            return BackendProvisionResult.FaultedResult(
                                TargetDiskPath,
                                ReceiptPath,
                                "Failed to parse existing receipt at " + ReceiptPath + ": " + ex.Message +
                                ". Existing disk will NOT be overwritten or deleted.",
                                stopwatch.ElapsedMilliseconds);
                        }

                        if (receipt == null)
                        {
                            State = ProvisioningState.Faulted;
                            return BackendProvisionResult.FaultedResult(
                                TargetDiskPath,
                                ReceiptPath,
                                "Existing provisioning receipt at " + ReceiptPath + " is empty or invalid." +
                                " Existing disk will NOT be overwritten or deleted.",
                                stopwatch.ElapsedMilliseconds);
                        }

                        // Inspect FileInfo metadata ONLY without reading or hashing mutable disk blocks
                        FileInfo diskInfo = new FileInfo(TargetDiskPath);
                        if (diskInfo.Length != BackendProvisionerConstants.TargetLogicalSizeBytes)
                        {
                            State = ProvisioningState.Faulted;
                            return BackendProvisionResult.FaultedResult(
                                TargetDiskPath,
                                ReceiptPath,
                                "Existing disk logical capacity mismatch (Found: " + diskInfo.Length +
                                " bytes, Expected: " + BackendProvisionerConstants.TargetLogicalSizeBytes +
                                " bytes). Existing disk will NOT be overwritten or deleted.",
                                stopwatch.ElapsedMilliseconds);
                        }

                        if (!receipt.Matches(BackendProvisionerConstants.PinnedRawSha512, BackendProvisionerConstants.TargetLogicalSizeBytes))
                        {
                            State = ProvisioningState.Faulted;
                            return BackendProvisionResult.FaultedResult(
                                TargetDiskPath,
                                ReceiptPath,
                                "Existing provisioning receipt does not match pinned requirements (Hash/Format/Size mismatch)." +
                                " Existing disk will NOT be overwritten or deleted.",
                                stopwatch.ElapsedMilliseconds);
                        }

                        // Existing disk verified via receipt + FileInfo metadata: safely reuse
                        try
                        {
                            receipt.LastVerifiedAtUtc = DateTimeOffset.UtcNow.ToString("O");
                            ProvisioningReceipt.SaveAtomic(ReceiptPath, receipt);
                        }
                        catch
                        {
                            // Non-fatal if read-only verification cannot update timestamp
                        }

                        State = ProvisioningState.ReusedExisting;

                        if (progress != null)
                        {
                            progress.Report(new BackendProvisionProgress
                            {
                                State = ProvisioningState.ReusedExisting,
                                StatusMessage = "Existing verified guest disk reused without disk writes.",
                                BytesRead = receipt.ExpectedSourceSizeBytes,
                                TotalBytes = receipt.ExpectedSourceSizeBytes,
                                NonZeroBytesWritten = receipt.NonZeroBytesWritten,
                                ZeroBytesSkipped = receipt.ZeroBytesSkipped,
                                PercentComplete = 100.0,
                                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                            });
                        }

                        return BackendProvisionResult.ReusedResult(TargetDiskPath, ReceiptPath, receipt, stopwatch.ElapsedMilliseconds);
                    }
                    else
                    {
                        // Existing disk exists without receipt: unmanaged / unknown!
                        State = ProvisioningState.Faulted;
                        return BackendProvisionResult.FaultedResult(
                            TargetDiskPath,
                            ReceiptPath,
                            "Existing unmanaged disk found at " + TargetDiskPath + " without a corresponding receipt (" +
                            ReceiptPath + "). Unmanaged disks cannot be verified and will NOT be overwritten or deleted.",
                            stopwatch.ElapsedMilliseconds);
                    }
                }

                // 2. Disk does not exist: fresh provisioning required
                if (publicStreamOpener == null)
                {
                    throw new ArgumentNullException(
                        nameof(publicStreamOpener),
                        "A public stream opener factory is required when provisioning a fresh disk.");
                }

                // If receipt exists without disk, check whether boot was already attempted
                if (File.Exists(ReceiptPath))
                {
                    ProvisioningReceipt staleReceipt = ProvisioningReceipt.LoadFromFile(ReceiptPath);
                    if (staleReceipt == null || !staleReceipt.Matches(BackendProvisionerConstants.PinnedRawSha512,
                        BackendProvisionerConstants.TargetLogicalSizeBytes))
                        throw new InvalidDataException("Unrecognized existing receipt; preserving it and refusing provisioning.");
                    if (staleReceipt != null && staleReceipt.GuestBootAttempted)
                    {
                        State = ProvisioningState.Faulted;
                        return BackendProvisionResult.FaultedResult(
                            TargetDiskPath,
                            ReceiptPath,
                            "Receipt indicates guest boot was already attempted, but root.raw is missing." +
                            " Refusing to provision to prevent silent guest state loss.",
                            stopwatch.ElapsedMilliseconds);
                    }

                    // Unbooted stale receipt can be cleanly purged before fresh provisioning
                    try { File.Delete(ReceiptPath); } catch { }
                }

                if (!Directory.Exists(RuntimeDirectory))
                {
                    Directory.CreateDirectory(RuntimeDirectory);
                }

                State = ProvisioningState.OpeningSource;
                if (progress != null)
                {
                    progress.Report(new BackendProvisionProgress
                    {
                        State = ProvisioningState.OpeningSource,
                        StatusMessage = "Opening public compressed OS image stream...",
                        PercentComplete = 0.0,
                        ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                    });
                }

                Stream compressedSourceStream = await publicStreamOpener().ConfigureAwait(false);
                if (compressedSourceStream == null)
                {
                    State = ProvisioningState.Faulted;
                    return BackendProvisionResult.FaultedResult(
                        TargetDiskPath,
                        ReceiptPath,
                        "Public stream opener returned null stream.",
                        stopwatch.ElapsedMilliseconds);
                }

                State = ProvisioningState.DecompressingAndAllocating;

                // 3. Direct GZip streaming decompression into SparseRawProvisioner using exact signature
                using (compressedSourceStream)
                using (GZipStream gzipStream = new GZipStream(compressedSourceStream, CompressionMode.Decompress, leaveOpen: false))
                {
                    ProvisionOptions options = new ProvisionOptions
                    {
                        TargetLogicalSizeBytes = BackendProvisionerConstants.TargetLogicalSizeBytes,
                        ExpectedSourceSizeBytes = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                        ExpectedSha512 = BackendProvisionerConstants.PinnedRawSha512,
                        ChunkSizeBytes = BackendProvisionerConstants.ChunkSizeBytes
                    };

                    IProgress<ProvisionProgress> storageProgress = null;
                    if (progress != null)
                    {
                        storageProgress = new Progress<ProvisionProgress>(p =>
                        {
                            progress.Report(new BackendProvisionProgress
                            {
                                State = ProvisioningState.DecompressingAndAllocating,
                                StatusMessage = string.Format(
                                    "Decompressing & punching sparse holes ({0:0.0}%)...",
                                    p.PercentComplete),
                                BytesRead = p.BytesRead,
                                TotalBytes = p.TotalBytes,
                                NonZeroBytesWritten = p.NonZeroBytesWritten,
                                ZeroBytesSkipped = p.ZeroBytesSkipped,
                                PercentComplete = Math.Min(100.0, Math.Max(0.0, p.PercentComplete)),
                                ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                            });
                        });
                    }

                    // Direct invocation of exact stream overload (no runtime reflection)
                    ProvisionResult provisionResult = await Task.Run(() =>
                        _sparseProvisioner.ProvisionRawImage(gzipStream, TargetDiskPath, options, storageProgress, cancellationToken),
                        cancellationToken).ConfigureAwait(false);

                    if (!provisionResult.Success)
                    {
                        State = ProvisioningState.Faulted;
                        return BackendProvisionResult.FaultedResult(
                            TargetDiskPath,
                            ReceiptPath,
                            "Sparse provisioning failed: " + provisionResult.ErrorMessage,
                            stopwatch.ElapsedMilliseconds);
                    }

                    // 4. Atomically record verification receipt
                    State = ProvisioningState.WritingReceipt;
                    ProvisioningReceipt receipt = new ProvisioningReceipt
                    {
                        SourceSha512 = provisionResult.CalculatedSha512 ?? BackendProvisionerConstants.PinnedRawSha512,
                        ExpectedSourceSizeBytes = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                        TargetLogicalBytes = provisionResult.LogicalSizeBytes,
                        RawFormat = BackendProvisionerConstants.RawFormat,
                        CreatedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        LastVerifiedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                        GuestBootAttempted = false,
                        NonZeroBytesWritten = provisionResult.NonZeroBytesWritten,
                        ZeroBytesSkipped = provisionResult.ZeroBytesSkipped,
                        IsSparseConfirmed = provisionResult.IsSparseConfirmed,
                        ChunkSizeBytes = options.ChunkSizeBytes
                    };

                    ProvisioningReceipt.SaveAtomic(ReceiptPath, receipt);
                    State = ProvisioningState.Ready;

                    if (progress != null)
                    {
                        progress.Report(new BackendProvisionProgress
                        {
                            State = ProvisioningState.Ready,
                            StatusMessage = "Backend root disk provisioned and receipt committed successfully.",
                            BytesRead = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                            TotalBytes = BackendProvisionerConstants.ExpectedSourceSizeBytes,
                            NonZeroBytesWritten = receipt.NonZeroBytesWritten,
                            ZeroBytesSkipped = receipt.ZeroBytesSkipped,
                            PercentComplete = 100.0,
                            ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                        });
                    }

                    return BackendProvisionResult.SuccessResult(
                        TargetDiskPath,
                        ReceiptPath,
                        receipt,
                        stopwatch.ElapsedMilliseconds);
                }
            }
            catch (OperationCanceledException)
            {
                State = ProvisioningState.Cancelled;
                return BackendProvisionResult.CancelledResult(
                    TargetDiskPath,
                    ReceiptPath,
                    "Provisioning was cancelled by caller.",
                    stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                State = ProvisioningState.Faulted;
                return BackendProvisionResult.FaultedResult(
                    TargetDiskPath,
                    ReceiptPath,
                    "Unexpected provisioning error: " + ex.Message,
                    stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                stopwatch.Stop();
                _provisionLock.Release();
            }
        }

        /// <inheritdoc />
        public async Task<bool> MarkBootAttemptedAsync(CancellationToken cancellationToken = default(CancellationToken))
        {
            await _provisionLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                return MarkBootAttemptedCore();
            }
            finally
            {
                _provisionLock.Release();
            }
        }

        /// <inheritdoc />
        public bool MarkBootAttempted()
        {
            _provisionLock.Wait();
            try
            {
                return MarkBootAttemptedCore();
            }
            finally
            {
                _provisionLock.Release();
            }
        }

        private bool MarkBootAttemptedCore()
        {
            if (!File.Exists(TargetDiskPath) || !File.Exists(ReceiptPath))
            {
                return false;
            }

            try
            {
                ProvisioningReceipt receipt = ProvisioningReceipt.LoadFromFile(ReceiptPath);
                if (receipt == null || !receipt.Matches(BackendProvisionerConstants.PinnedRawSha512,
                    BackendProvisionerConstants.TargetLogicalSizeBytes) ||
                    new FileInfo(TargetDiskPath).Length != BackendProvisionerConstants.TargetLogicalSizeBytes) return false;

                receipt.GuestBootAttempted = true;
                receipt.LastBootAttemptUtc = DateTimeOffset.UtcNow.ToString("O");
                ProvisioningReceipt.SaveAtomic(ReceiptPath, receipt);

                // Verify that the bootflag was successfully persisted to disk before returning true
                ProvisioningReceipt verified = ProvisioningReceipt.LoadFromFile(ReceiptPath);
                if (verified == null || !verified.GuestBootAttempted)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
