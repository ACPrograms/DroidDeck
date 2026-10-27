using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Windows.Storage;

namespace DroidDeckXboxHost
{
    public enum HostLifecycleState
    {
        NotInitialized,
        InitializingWebView2,
        AssetsMapped,
        Navigating,
        UiReady,
        Faulted
    }

    public sealed class DiagnosticState
    {
        private static readonly object _syncLock = new object();
        private const int MaxLogEntries = 20;

        [DllImport("api-ms-win-core-file-l1-1-0.dll", EntryPoint = "GetDiskFreeSpaceExW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool GetDiskFreeSpaceEx(string directory, out ulong available, out ulong total, out ulong free);

        public ulong? AppVolumeFreeBytes
        {
            get
            {
                ulong available, total, free;
                return GetDiskFreeSpaceEx(ApplicationData.Current.LocalFolder.Path, out available, out total, out free)
                    ? (ulong?)available : null;
            }
        }

        public HostLifecycleState State { get; private set; } = HostLifecycleState.NotInitialized;
        public bool IsUiReady => State == HostLifecycleState.UiReady;
        public DateTimeOffset InitializationTimestamp { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? ReadyTimestamp { get; private set; }
        public long TimeFromInitToReadyMs { get; private set; } = -1;
        public int WasmMimeInterceptions { get; private set; } = 0;
        public int TotalRequestsServed { get; private set; } = 0;
        public string LastError { get; private set; } = string.Empty;
        public List<string> RecentMessages { get; } = new List<string>();

        public void TransitionTo(HostLifecycleState newState, string message = null)
        {
            lock (_syncLock)
            {
                State = newState;
                if (newState == HostLifecycleState.UiReady && !ReadyTimestamp.HasValue)
                {
                    ReadyTimestamp = DateTimeOffset.UtcNow;
                    TimeFromInitToReadyMs = (long)(ReadyTimestamp.Value - InitializationTimestamp).TotalMilliseconds;
                }

                if (!string.IsNullOrEmpty(message))
                {
                    AddMessage($"[{newState}] {message}");
                }
            }
        }

        public void RecordInterception(bool isWasm)
        {
            lock (_syncLock)
            {
                TotalRequestsServed++;
                if (isWasm)
                {
                    WasmMimeInterceptions++;
                }
            }
        }

        public void RecordError(string error)
        {
            lock (_syncLock)
            {
                State = HostLifecycleState.Faulted;
                LastError = error ?? "Unknown error";
                AddMessage($"[ERROR] {LastError}");
            }
        }

        public void AddMessage(string msg)
        {
            lock (_syncLock)
            {
                string timestamped = $"[{DateTimeOffset.UtcNow:HH:mm:ss.fff}] {msg}";
                RecentMessages.Add(timestamped);
                if (RecentMessages.Count > MaxLogEntries)
                {
                    RecentMessages.RemoveAt(0);
                }
            }
        }

        public string ToJson()
        {
            lock (_syncLock)
            {
                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                return JsonSerializer.Serialize(new
                {
                    State = State.ToString(),
                    AppVolumeFreeBytes,
                    IsUiReady,
                    InitializationTimestamp,
                    ReadyTimestamp,
                    TimeFromInitToReadyMs,
                    WasmMimeInterceptions,
                    TotalRequestsServed,
                    LastError,
                    RecentMessages
                }, options);
            }
        }

        public async Task SaveToDiskAsync()
        {
            try
            {
                string json = ToJson();
                StorageFolder localFolder = ApplicationData.Current.LocalFolder;
                StorageFile file = await localFolder.CreateFileAsync(
                    "droiddeck-host-state.json",
                    CreationCollisionOption.ReplaceExisting
                );
                await FileIO.WriteTextAsync(file, json);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[DiagnosticState] Failed to persist state: {ex.Message}");
            }
        }
    }
}
