using System;
using System.Threading;
using System.Threading.Tasks;

namespace DroidDeck.XboxRuntime.Storage
{
    /// <summary>
    /// Contract for safe public-image sparse raw disk provisioning on Xbox Dev Mode platform.
    /// </summary>
    public interface ISparseProvisioner
    {
        /// <summary>
        /// Provisions a sparse raw disk from an official public source image synchronously.
        /// </summary>
        ProvisionResult ProvisionRawImage(
            string sourceImagePath,
            string destinationImagePath,
            ProvisionOptions options = null,
            IProgress<ProvisionProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken));

        /// <summary>
        /// Provisions a sparse raw disk from an official public source image asynchronously.
        /// </summary>
        Task<ProvisionResult> ProvisionRawImageAsync(
            string sourceImagePath,
            string destinationImagePath,
            ProvisionOptions options = null,
            IProgress<ProvisionProgress> progress = null,
            CancellationToken cancellationToken = default(CancellationToken));
    }
}
