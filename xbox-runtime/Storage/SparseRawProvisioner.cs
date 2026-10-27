// ------------------------------------------------------------------------------------------------
// DroidDeck Xbox Platform Backend: Sparse Raw Storage Provisioner
//
// DeviceIoControl P/Invoke targeting api-ms-win-core-io-l1-1-1.dll and FSCTL_SET_SPARSE (0x000900C4)
// adapted under GPL-2.0 from:
// https://github.com/YoCanIGetUhhhh/XboxInstallOtherOS-dev commit 426235db19fc1ae17f8d50e8702015efbb676496 (MainPage.xaml.cs).
// ------------------------------------------------------------------------------------------------

using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace DroidDeck.XboxRuntime.Storage
{
    /// <summary>
    /// Safe public-image sparse raw disk provisioner for DroidDeck Xbox platform backend.
    /// Provisions official public Linux RAW images into NTFS sparse virtual disks.
    /// </summary>
    /// <remarks>
    /// SECURITY CONTRACT:
    /// The caller must provide a pinned, verified public OS asset only.
    /// The host application environment is responsible for supplying the packaged public OS source
    /// image and must never supply a booted guest disk, session state, or user credentials.
    /// </remarks>
    public sealed class SparseRawProvisioner : ISparseProvisioner
    {
        private const uint FSCTL_SET_SPARSE = 0x000900C4;

        [DllImport("api-ms-win-core-io-l1-1-1.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
        private static extern bool DeviceIoControl(
            SafeFileHandle hDevice,
            uint dwIoControlCode,
            IntPtr lpInBuffer,
            uint nInBufferSize,
            IntPtr lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        /// <summary>
        /// Marks the destination file handle as sparse using FSCTL_SET_SPARSE.
        /// </summary>
        public static bool SetSparse(SafeFileHandle handle, out int win32Error)
        {
            win32Error = 0;
            try
            {
                uint bytesReturned;
                bool success = DeviceIoControl(
                    handle,
                    FSCTL_SET_SPARSE,
                    IntPtr.Zero,
                    0,
                    IntPtr.Zero,
                    0,
                    out bytesReturned,
                    IntPtr.Zero);

                if (!success)
                {
                    win32Error = Marshal.GetLastWin32Error();
                }
                return success;
            }
            catch (Exception ex)
            {
                win32Error = Marshal.GetHRForException(ex);
                return false;
            }
        }

        /// <summary>
        /// Evaluates whether a byte buffer consists entirely of zero bytes via 64-bit word scanning.
        /// </summary>
        public static bool IsChunkAllZero(byte[] buffer, int count)
        {
            if (buffer == null || count <= 0) return true;

            int ulongCount = count / 8;
            for (int i = 0; i < ulongCount; i++)
            {
                if (BitConverter.ToUInt64(buffer, i * 8) != 0UL)
                {
                    return false;
                }
            }

            int remainderStart = ulongCount * 8;
            for (int i = remainderStart; i < count; i++)
            {
                if (buffer[i] != 0)
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsHex(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        private static void SafeDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // Best-effort cleanup of temporary file
            }
        }

        private static int ReadFullBlock(Stream stream, byte[] buffer, int count, CancellationToken cancellationToken)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = stream.Read(buffer, totalRead, count - totalRead);
                if (read == 0)
                {
                    break;
                }
                totalRead += read;
            }
            return totalRead;
        }

        private static string BytesToHex(byte[] bytes)
        {
            if (bytes == null) return string.Empty;
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            for (int i = 0; i < bytes.Length; i++)
            {
                sb.Append(bytes[i].ToString("x2"));
            }
            return sb.ToString();
        }

        /// <inheritdoc />
        public ProvisionResult ProvisionRawImage(
            string sourceImagePath,
            string destinationImagePath,
            ProvisionOptions options = null,
            IProgress<ProvisionProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ProvisionRawImageCore(sourceImagePath, null, destinationImagePath, options, progress, cancellationToken);
        }

        /// <summary>Consumes a pinned public stream without seeking or closing the caller's stream.</summary>
        public ProvisionResult ProvisionRawImage(
            Stream publicSource,
            string destinationImagePath,
            ProvisionOptions options = null,
            IProgress<ProvisionProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return ProvisionRawImageCore(null, publicSource, destinationImagePath, options, progress, cancellationToken);
        }

        private ProvisionResult ProvisionRawImageCore(
            string sourceImagePath, Stream publicSource, string destinationImagePath,
            ProvisionOptions options, IProgress<ProvisionProgress> progress, CancellationToken cancellationToken)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            string tempFilePath = null;
            FileStream destStream = null;
            bool ownsTemporaryFile = false;

            long totalRead = 0;
            long nonZeroBytesWritten = 0;
            long zeroBytesSkipped = 0;
            int nonZeroChunks = 0;
            int zeroChunks = 0;
            string computedSha512 = null;
            bool hashVerified = false;
            bool isSparse = false;

            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (options == null)
                {
                    throw new ArgumentNullException(nameof(options), "ProvisionOptions must be provided.");
                }

                if (publicSource == null && string.IsNullOrEmpty(sourceImagePath))
                {
                    throw new ArgumentException("Source image path cannot be null or empty.", nameof(sourceImagePath));
                }

                if (publicSource != null && !publicSource.CanRead)
                    throw new ArgumentException("Public source stream must be readable.", nameof(publicSource));

                if (string.IsNullOrEmpty(destinationImagePath))
                {
                    throw new ArgumentException("Destination image path cannot be null or empty.", nameof(destinationImagePath));
                }

                if (string.IsNullOrWhiteSpace(options.ExpectedSha512))
                {
                    throw new ArgumentException("ExpectedSha512 is required and cannot be null, empty, or whitespace.", nameof(options));
                }

                string trimmedExpectedHash = options.ExpectedSha512.Trim();
                if (trimmedExpectedHash.Length != 128 || !IsHex(trimmedExpectedHash))
                {
                    throw new ArgumentException("ExpectedSha512 must be a valid 128-character hexadecimal string.", nameof(options));
                }

                if (options.ExpectedSourceSizeBytes <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(options.ExpectedSourceSizeBytes), "ExpectedSourceSizeBytes must be strictly positive.");
                }

                if (options.ChunkSizeBytes <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(options.ChunkSizeBytes), "ChunkSizeBytes must be strictly positive.");
                }

                if (options.TargetLogicalSizeBytes <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(options.TargetLogicalSizeBytes), "TargetLogicalSizeBytes must be strictly positive.");
                }

                if (options.ExpectedSourceSizeBytes > options.TargetLogicalSizeBytes)
                {
                    throw new ArgumentException(
                        "ExpectedSourceSizeBytes (" + options.ExpectedSourceSizeBytes + 
                        ") cannot exceed TargetLogicalSizeBytes (" + options.TargetLogicalSizeBytes + ").",
                        nameof(options));
                }

                if (File.Exists(destinationImagePath))
                {
                    throw new InvalidOperationException(
                        "Destination disk already exists: " + destinationImagePath + 
                        ". Overwriting or deleting existing destination is strictly prohibited.");
                }

                if (publicSource == null)
                {
                    if (!File.Exists(sourceImagePath))
                        throw new FileNotFoundException("Source public raw image does not exist: " + sourceImagePath);
                    FileInfo sourceInfo = new FileInfo(sourceImagePath);
                    if (sourceInfo.Length != options.ExpectedSourceSizeBytes)
                        throw new InvalidDataException("Source file length does not match ExpectedSourceSizeBytes.");
                }

                string destDir = Path.GetDirectoryName(destinationImagePath);
                if (string.IsNullOrEmpty(destDir))
                {
                    destDir = Directory.GetCurrentDirectory();
                }
                else if (!Directory.Exists(destDir))
                {
                    Directory.CreateDirectory(destDir);
                }

                string tempFileName = Path.GetFileName(destinationImagePath) + ".tmp." + Guid.NewGuid().ToString("N");
                tempFilePath = Path.Combine(destDir, tempFileName);

                destStream = new FileStream(tempFilePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.None);
                ownsTemporaryFile = true;

                int win32Error;
                isSparse = SetSparse(destStream.SafeFileHandle, out win32Error);
                if (!isSparse)
                {
                    throw new NotSupportedException(
                        "FSCTL_SET_SPARSE failed (Win32 error: " + win32Error + 
                        "). Sparse allocation is strictly required. Full-allocation fallback is prohibited.");
                }

                destStream.SetLength(options.TargetLogicalSizeBytes);

                byte[] buffer = new byte[options.ChunkSizeBytes];

                using (Stream ownedSource = publicSource == null
                    ? new FileStream(sourceImagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan) : null)
                using (SHA512 sha512 = SHA512.Create())
                {
                    Stream sourceStream = publicSource ?? ownedSource;
                    while (true)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        long remainingExpected = options.ExpectedSourceSizeBytes - totalRead;
                        if (remainingExpected <= 0)
                        {
                            break;
                        }

                        int toRead = options.ChunkSizeBytes;
                        if (remainingExpected < toRead)
                        {
                            toRead = (int)remainingExpected;
                        }

                        int bytesRead = ReadFullBlock(sourceStream, buffer, toRead, cancellationToken);
                        if (bytesRead == 0)
                        {
                            break;
                        }

                        if (totalRead + bytesRead > options.ExpectedSourceSizeBytes)
                        {
                            throw new InvalidDataException(
                                "Source stream exceeds ExpectedSourceSizeBytes (" + options.ExpectedSourceSizeBytes + "). Aborting before writing excess.");
                        }

                        totalRead += bytesRead;

                        sha512.TransformBlock(buffer, 0, bytesRead, null, 0);

                        if (IsChunkAllZero(buffer, bytesRead))
                        {
                            destStream.Seek(bytesRead, SeekOrigin.Current);
                            zeroBytesSkipped += bytesRead;
                            zeroChunks++;
                        }
                        else
                        {
                            destStream.Write(buffer, 0, bytesRead);
                            nonZeroBytesWritten += bytesRead;
                            nonZeroChunks++;
                        }

                        if (progress != null)
                        {
                            progress.Report(new ProvisionProgress
                            {
                                BytesRead = totalRead,
                                TotalBytes = options.ExpectedSourceSizeBytes,
                                NonZeroBytesWritten = nonZeroBytesWritten,
                                ZeroBytesSkipped = zeroBytesSkipped,
                                PercentComplete = options.ExpectedSourceSizeBytes > 0
                                    ? ((double)totalRead / options.ExpectedSourceSizeBytes) * 100.0
                                    : 0.0
                            });
                        }
                    }

                    // Check for trailing excess data beyond expected length
                    byte[] probeExtra = new byte[1];
                    if (sourceStream.Read(probeExtra, 0, 1) > 0)
                    {
                        throw new InvalidDataException(
                            "Source stream contains excess bytes beyond ExpectedSourceSizeBytes (" + options.ExpectedSourceSizeBytes + ").");
                    }

                    if (totalRead != options.ExpectedSourceSizeBytes)
                    {
                        throw new InvalidDataException(
                            "Source image byte count mismatch! Expected: " + options.ExpectedSourceSizeBytes + 
                            ", Read: " + totalRead + ". Temporary file cleaned up.");
                    }

                    byte[] empty = new byte[0];
                    sha512.TransformFinalBlock(empty, 0, 0);
                    computedSha512 = BytesToHex(sha512.Hash);

                    hashVerified = string.Equals(computedSha512, trimmedExpectedHash, StringComparison.OrdinalIgnoreCase);
                    if (!hashVerified)
                    {
                        throw new InvalidDataException(
                            "SHA-512 verification failed! Expected: " + trimmedExpectedHash + 
                            ", Calculated: " + computedSha512 + ". Temporary file cleaned up.");
                    }
                }

                destStream.Flush(true);
                destStream.Dispose();
                destStream = null;

                cancellationToken.ThrowIfCancellationRequested();
                File.Move(tempFilePath, destinationImagePath);
                tempFilePath = null;
                stopwatch.Stop();

                return new ProvisionResult
                {
                    Success = true,
                    SourcePath = sourceImagePath,
                    DestinationPath = destinationImagePath,
                    TemporaryPath = null,
                    LogicalSizeBytes = options.TargetLogicalSizeBytes,
                    TotalSourceBytesRead = totalRead,
                    NonZeroBytesWritten = nonZeroBytesWritten,
                    ZeroBytesSkipped = zeroBytesSkipped,
                    NonZeroChunksCount = nonZeroChunks,
                    ZeroChunksCount = zeroChunks,
                    CalculatedSha512 = computedSha512,
                    Sha512Verified = hashVerified,
                    IsSparseConfirmed = isSparse,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds
                };
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                if (destStream != null)
                {
                    try { destStream.Dispose(); } catch { }
                    destStream = null;
                }

                if (ownsTemporaryFile) SafeDelete(tempFilePath);

                return new ProvisionResult
                {
                    Success = false,
                    SourcePath = sourceImagePath,
                    DestinationPath = destinationImagePath,
                    TemporaryPath = tempFilePath,
                    LogicalSizeBytes = options != null ? options.TargetLogicalSizeBytes : 0,
                    TotalSourceBytesRead = totalRead,
                    NonZeroBytesWritten = nonZeroBytesWritten,
                    ZeroBytesSkipped = zeroBytesSkipped,
                    NonZeroChunksCount = nonZeroChunks,
                    ZeroChunksCount = zeroChunks,
                    CalculatedSha512 = computedSha512,
                    Sha512Verified = hashVerified,
                    IsSparseConfirmed = isSparse,
                    ElapsedMilliseconds = stopwatch.ElapsedMilliseconds,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <inheritdoc />
        public Task<ProvisionResult> ProvisionRawImageAsync(
            string sourceImagePath,
            string destinationImagePath,
            ProvisionOptions options = null,
            IProgress<ProvisionProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return Task.Run(function: () => ProvisionRawImage(
                sourceImagePath,
                destinationImagePath,
                options,
                progress,
                cancellationToken), cancellationToken: cancellationToken);
        }
    }
}
