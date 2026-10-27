using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Core C# 7.3 UWP native QEMU backend adapter.
    /// Manages the unmanaged QEMU lifecycle, 16 MiB dedicated thread, memory-safe argv cleanup,
    /// bounded diagnostic state, and fixed ACPI power controls behind DroidDeck UI.
    /// </summary>
    public sealed class QemuBackendAdapter : IDisposable
    {
        // Enforce strict single start per process lifecycle
        private static int _globalProcessStarted = 0;

        private const int MaxLogRingCapacity = 50;
        private const int MaxLogLineLength = 256;

        private readonly object _syncLock = new object();
        private readonly List<string> _logRing = new List<string>();
        private readonly Stopwatch _uptimeStopwatch = new Stopwatch();

        private QemuConfiguration _config;
        private Thread _vmThread;
        private volatile bool _stopRequested;
        private volatile int _exitCode = -999;
        private QemuAdapterState _state = QemuAdapterState.Uninitialized;
        private string _lastErrorMessage = string.Empty;
        private bool _disposed;

        public QemuAdapterState State
        {
            get { lock (_syncLock) return _state; }
            private set
            {
                lock (_syncLock) _state = value;
                StateChanged?.Invoke(this, value);
            }
        }

        public int ExitCode => _exitCode;
        public bool IsRunning => State == QemuAdapterState.Running || State == QemuAdapterState.Paused;
        public QemuInputBridge InputBridge { get; } = new QemuInputBridge();
        public QemuDisplayBridge DisplayBridge { get; private set; }

        public event EventHandler<QemuAdapterState> StateChanged;
        public event EventHandler<QemuLogEventArgs> LogEmitted;

        /// <summary>
        /// Configures and launches the native QEMU engine on a dedicated 16 MiB stack thread.
        /// Guaranteed: strictly one start allowed per process lifecycle.
        /// </summary>
        public void Start(QemuConfiguration config, string packageDirectory = null)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            config.Validate();

            // Strict single-start enforcement per process
            if (Interlocked.CompareExchange(ref _globalProcessStarted, 1, 0) != 0)
            {
                string msg = "Single-start policy violation: QEMU has already executed in this process. " +
                             "Xbox Dev Mode process must be restarted before another VM session can be initiated.";
                RecordError(msg);
                throw new InvalidOperationException(msg);
            }

            lock (_syncLock)
            {
                if (_state != QemuAdapterState.Uninitialized)
                {
                    throw new InvalidOperationException("Adapter cannot be started from current state: " + _state);
                }

                _config = config;
                DisplayBridge = new QemuDisplayBridge(config.GpuMode == QemuGpuMode.GpuDirectAngle);
                State = QemuAdapterState.Configured;
            }

            // Set up ANGLE environment variable before native library calls
            if (config.GpuMode == QemuGpuMode.GpuDirectAngle && !string.IsNullOrEmpty(config.AngleSuffix))
            {
                try
                {
                    NativeQemuBindings.SetEnvironmentVariable("UWP_ANGLE_SUFFIX", config.AngleSuffix);
                    AddLog("Configured UWP_ANGLE_SUFFIX=" + config.AngleSuffix);
                }
                catch (Exception ex)
                {
                    AddLog("Failed to set UWP_ANGLE_SUFFIX: " + ex.Message);
                }
            }

            State = QemuAdapterState.Starting;
            AddLog("Spawning dedicated 16 MiB stack thread for QEMU main loop...");

            // Dedicated 16 MiB stack thread
            _vmThread = new Thread(RunVmThreadProc, 16 * 1024 * 1024)
            {
                Name = "QemuNativeWorker",
                IsBackground = true
            };

            // Main loop priority: Highest in GPU mode (graphics first), AboveNormal in CPU mode
            try
            {
                _vmThread.Priority = (config.GpuMode == QemuGpuMode.GpuDirectAngle)
                    ? ThreadPriority.Highest
                    : ThreadPriority.AboveNormal;
            }
            catch
            {
                // Ignore failure to raise priority on restricted sandboxes
            }

            _uptimeStopwatch.Restart();
            _vmThread.Start();
        }

        private void RunVmThreadProc()
        {
            AddLog("QEMU worker thread entered. Building managed argv...");

            AllocatedNativeArgv nativeArgv = null;
            try
            {
                nativeArgv = QemuArgvBuilder.BuildNativeArgv(_config);
                AddLog("Managed argv allocated (" + nativeArgv.Argc + " arguments). Calling qemu_dll_main...");

                State = QemuAdapterState.Running;

                // Unmanaged call to QEMU main loop
                _exitCode = NativeQemuBindings.qemu_dll_main(
                    nativeArgv.OutFilePathPointer,
                    nativeArgv.Argc,
                    nativeArgv.ArgvPointer);

                AddLog("qemu_dll_main returned code: " + _exitCode);
                State = (_exitCode == 0) ? QemuAdapterState.Terminated : QemuAdapterState.Faulted;
            }
            catch (Exception ex)
            {
                _exitCode = -998;
                RecordError("Native QEMU exception: " + ex.GetType().Name + ": " + ex.Message);
                State = QemuAdapterState.Faulted;
            }
            finally
            {
                // Guaranteed managed argv pointer lifetime cleanup
                if (nativeArgv != null)
                {
                    nativeArgv.Dispose();
                    AddLog("Native argv unmanaged pointers successfully freed.");
                }
                _uptimeStopwatch.Stop();
            }
        }

        /// <summary>
        /// Requests graceful guest shutdown via fixed ACPI powerdown event.
        /// </summary>
        public bool RequestPowerDown(int timeoutMs = 5000)
        {
            AddLog("Sending ACPI system_powerdown to guest...");
            State = QemuAdapterState.Stopping;
            string response = ExecuteFixedHmp("system_powerdown", timeoutMs);
            return response != null;
        }

        /// <summary>
        /// Pauses virtual CPU execution via fixed monitor stop command.
        /// </summary>
        public bool Pause(int timeoutMs = 5000)
        {
            AddLog("Pausing virtual CPUs (stop)...");
            string response = ExecuteFixedHmp("stop", timeoutMs);
            if (response != null)
            {
                State = QemuAdapterState.Paused;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Resumes virtual CPU execution via fixed monitor cont command.
        /// </summary>
        public bool Resume(int timeoutMs = 5000)
        {
            AddLog("Resuming virtual CPUs (cont)...");
            string response = ExecuteFixedHmp("cont", timeoutMs);
            if (response != null)
            {
                State = QemuAdapterState.Running;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Emergency unmanaged engine stop via native export qemu_dll_stop.
        /// </summary>
        public void EmergencyStop()
        {
            _stopRequested = true;
            State = QemuAdapterState.Stopping;
            AddLog("Invoking qemu_dll_stop for immediate engine shutdown...");
            try
            {
                NativeQemuBindings.qemu_dll_stop();
            }
            catch (Exception ex)
            {
                AddLog("qemu_dll_stop threw: " + ex.Message);
            }
        }

        /// <summary>
        /// Executes strictly whitelisted monitor commands. Arbitrary command injection is prohibited.
        /// </summary>
        private string ExecuteFixedHmp(string fixedCommand, int timeoutMs)
        {
            if (fixedCommand != "system_powerdown" &&
                fixedCommand != "stop" &&
                fixedCommand != "cont" &&
                fixedCommand != "info status")
            {
                throw new InvalidOperationException("Security policy violation: Arbitrary HMP command rejected: " + fixedCommand);
            }

            try
            {
                var sb = new StringBuilder(1024);
                int rc = NativeQemuBindings.qemu_dll_hmp(fixedCommand, sb, sb.Capacity, timeoutMs);
                if (rc == 0)
                {
                    return sb.ToString();
                }
                AddLog("HMP command '" + fixedCommand + "' timed out or failed (rc=" + rc + ").");
                return null;
            }
            catch (Exception ex)
            {
                AddLog("HMP command '" + fixedCommand + "' exception: " + ex.Message);
                return null;
            }
        }

        /// <summary>
        /// Produces a safe, bounded diagnostic snapshot. Strictly excludes private credentials or guest data.
        /// </summary>
        public QemuDiagnosticInfo GetDiagnosticSnapshot()
        {
            lock (_syncLock)
            {
                return new QemuDiagnosticInfo
                {
                    State = _state,
                    ExitCode = _exitCode,
                    IsRunning = IsRunning,
                    IsDirectPanelAttached = DisplayBridge?.IsPanelAttached ?? false,
                    DirectFrameCount = DisplayBridge?.DirectFrameCount ?? 0,
                    FrameWidth = DisplayBridge?.LastDirectWidth ?? (_config?.DisplayWidth ?? 0),
                    FrameHeight = DisplayBridge?.LastDirectHeight ?? (_config?.DisplayHeight ?? 0),
                    UptimeMilliseconds = _uptimeStopwatch.ElapsedMilliseconds,
                    CpuModel = _config?.CpuModel ?? "max",
                    CpuCount = _config?.CpuCount ?? 4,
                    RamMegabytes = _config?.RamMegabytes ?? 2048,
                    TbCacheMegabytes = _config?.TbCacheMegabytes ?? 512,
                    SshHostPort = _config?.SshHostPort ?? 2223,
                    GpuMode = _config?.GpuMode ?? QemuGpuMode.GpuDirectAngle,
                    LastErrorMessage = _lastErrorMessage,
                    RecentLogMessages = _logRing.ToArray()
                };
            }
        }

        private void AddLog(string message)
        {
            if (string.IsNullOrEmpty(message)) return;

            // Bounded message truncation
            if (message.Length > MaxLogLineLength)
            {
                message = message.Substring(0, MaxLogLineLength - 3) + "...";
            }

            string timestamped = "[" + DateTime.UtcNow.ToString("HH:mm:ss.fff") + "] " + message;

            lock (_syncLock)
            {
                _logRing.Add(timestamped);
                if (_logRing.Count > MaxLogRingCapacity)
                {
                    _logRing.RemoveAt(0);
                }
            }

            LogEmitted?.Invoke(this, new QemuLogEventArgs(message));
        }

        private void RecordError(string error)
        {
            _lastErrorMessage = error ?? "Unknown error";
            AddLog("[ERROR] " + _lastErrorMessage);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (IsRunning)
                {
                    EmergencyStop();
                }
            }
            catch { }

            DisplayBridge?.Dispose();
            DisplayBridge = null;
        }
    }
}
