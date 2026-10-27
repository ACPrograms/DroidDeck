using System;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Storage;
using DroidDeckXboxHost.Runtime.Provisioning;

namespace DroidDeckXboxHost.Runtime
{
    // The source URL is a one-time development input for the pinned PUBLIC base image.
    // Disk contents are never exported; all subsequent checks use the receipt and file metadata.
    internal sealed class RuntimeSession
    {
        private readonly string _local = ApplicationData.Current.LocalFolder.Path;
        private readonly Action<string, int, bool> _report;
        private int _started;
        private long _lastProgressTick;
        private QemuBackendAdapter _backend;
        private GamepadTransport _gamepad;
        private readonly SemaphoreSlim _saveLock = new SemaphoreSlim(1, 1);

        public RuntimeSession(Action<string, int, bool> report) { _report = report; }
        public QemuBackendAdapter Backend => _backend;

        public Task PauseAsync() => Task.Run(() =>
        {
            _gamepad?.Dispose();
            _gamepad = null;
            if (_backend != null && _backend.IsRunning) _backend.Pause(1000);
        });

        public Task ResumeAsync() => Task.Run(() =>
        {
            if (_backend == null || _backend.State != QemuAdapterState.Paused) return;
            if (_backend.Resume(1000))
            {
                _gamepad = new GamepadTransport();
                _gamepad.Start();
            }
        });

        public async Task StartIfRequestedAsync()
        {
            string request = Path.Combine(_local, "runtime-source.request");
            if (!File.Exists(request) || Interlocked.Exchange(ref _started, 1) != 0) return;
            try
            {
                if (new FileInfo(request).Length > 512) throw new InvalidDataException("Source request too large.");
                Uri uri;
                string source = File.ReadAllText(request).Trim();
                if (!Uri.TryCreate(source, UriKind.Absolute, out uri) ||
                    uri.Scheme != "http" || uri.Port != 8765 || uri.AbsolutePath != "/image.raw.gz" ||
                    !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) ||
                    !System.Net.IPAddress.TryParse(uri.Host, out var address) ||
                    address.GetAddressBytes().Length != 4 || address.GetAddressBytes()[0] != 10)
                    throw new InvalidDataException("Expected the public-image LAN provisioning endpoint.");

                var provisioner = new BackendProvisioner(Path.Combine(_local, "DroidDeckRuntime"));
                var progress = new Progress<BackendProvisionProgress>(p =>
                {
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (now - Interlocked.Read(ref _lastProgressTick) < 1000) return;
                    Interlocked.Exchange(ref _lastProgressTick, now);
                    _report("Preparing runtime", (int)p.PercentComplete, true);
                    _ = SaveStateAsync("Provisioning " + (int)p.PercentComplete + "%", null);
                });
                _report("Preparing runtime", 0, true);
                using (var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                {
                    var result = await provisioner.EnsureProvisionedAsync(async () =>
                        await client.GetStreamAsync(uri), progress);
                    if (!result.Success) throw new InvalidOperationException(result.ErrorMessage);
                }

                string seed = Path.Combine(_local, "boot-seed.iso");
                if (!File.Exists(seed)) throw new FileNotFoundException("Task-created boot seed is required.");
                var config = new QemuConfiguration
                {
                    RootDiskPath = provisioner.TargetDiskPath,
                    ProvisioningReceiptPath = provisioner.ReceiptPath,
                    SeedIsoPath = seed,
                    BiosDirectory = Path.Combine(Package.Current.InstalledLocation.Path, "pc-bios"),
                    GpuMode = File.Exists(Path.Combine(_local, "runtime-gpu-angle.request"))
                        ? QemuGpuMode.GpuDirectAngle : QemuGpuMode.SoftwareFramebuffer,
                    RamMegabytes = 2048, CpuCount = 4, TbCacheMegabytes = 256,
                    DisplayWidth = 1024, DisplayHeight = 768,
                    OutLogPath = Path.Combine(_local, "droiddeck-qemu-boot.log")
                };
                var validation = QemuPackageVerifier.Validate(Package.Current.InstalledLocation.Path, config, true);
                if (!validation.IsValid) throw new InvalidOperationException(validation.ErrorMessage);
                // Persist the protection flag BEFORE entering any native guest code.
                if (!await provisioner.MarkBootAttemptedAsync())
                    throw new IOException("Cannot persist guest boot protection.");
                _backend = new QemuBackendAdapter();
                _backend.StateChanged += async (sender, state) =>
                {
                    if (state == QemuAdapterState.Terminated || state == QemuAdapterState.Faulted) _gamepad?.Dispose();
                    await SaveStateAsync(state.ToString(), null);
                };
                _backend.Start(config, Package.Current.InstalledLocation.Path);
                _gamepad = new GamepadTransport();
                _gamepad.Start();
                _report("Starting runtime", -1, true);
                await SaveStateAsync("Starting", null);
            }
            catch (Exception ex)
            {
                _report("Runtime unavailable", -1, false);
                await SaveStateAsync("Faulted", ex.GetType().Name + ": " + ex.Message);
            }
        }

        public async Task RefreshStatusAsync()
        {
            if (_backend == null) return;
            string poweroff = Path.Combine(_local, "runtime-poweroff.request");
            if (File.Exists(poweroff))
            {
                File.Delete(poweroff);
                await Task.Run(() => _backend.RequestPowerDown());
            }
            await SaveStateAsync(_backend.State.ToString(), null);
        }

        private async Task SaveStateAsync(string stage, string error)
        {
            await _saveLock.WaitAsync();
            try
            {
                string json = JsonSerializer.Serialize(new
                {
                    Stage = stage, Error = error,
                    NativeState = _backend == null ? "NotStarted" : _backend.State.ToString(),
                    ExitCode = _backend == null ? (int?)null : _backend.ExitCode,
                    FrameSequence = _backend?.DisplayBridge?.FrameSequence ?? 0,
                    NativeError = _backend?.GetDiagnosticSnapshot().LastErrorMessage,
                    Utc = DateTimeOffset.UtcNow
                });
                File.WriteAllText(Path.Combine(_local, "droiddeck-runtime-state.json"), json);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { _saveLock.Release(); }
        }
    }
}
