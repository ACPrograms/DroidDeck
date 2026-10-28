using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Windows.Gaming.Input;

namespace DroidDeckXboxHost.Runtime
{
    // One standard controller, transported over a guest port bound ONLY to host loopback.
    // No game-specific bindings, credentials, commands, or guest data are transmitted.
    internal sealed class GamepadTransport : IDisposable
    {
        private readonly CancellationTokenSource _stop = new CancellationTokenSource();
        private TcpClient _client;
        private Task _worker;

        public void Start()
        {
            if (_worker != null) return;
            _worker = Task.Run(RunAsync);
        }

        private static short Axis(double value) => (short)Math.Round(Math.Max(-1, Math.Min(1, value)) * 32767);

        private async Task RunAsync()
        {
            uint sequence = 0;
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    using (_client = new TcpClient())
                    {
                        await _client.ConnectAsync("127.0.0.1", 2224);
                        _client.NoDelay = true;
                        using (var stream = _client.GetStream())
                        using (var packet = new MemoryStream(24))
                        using (var writer = new BinaryWriter(packet))
                        {
                            var lastState = new byte[16];
                            var lastSend = Stopwatch.StartNew();
                            bool sent = false;
                            while (!_stop.IsCancellationRequested)
                            {
                                var pads = Gamepad.Gamepads;
                                GamepadReading reading = pads.Count > 0 ? pads[0].GetCurrentReading() : default(GamepadReading);
                                packet.Position = 0;
                                writer.Write(0x31504444u); // DDP1, little endian
                                writer.Write(++sequence);
                                writer.Write((uint)reading.Buttons & 0x3FFF); // Paddles do not invalidate standard pad packets.
                                writer.Write(Axis(reading.LeftThumbstickX));
                                writer.Write(Axis(-reading.LeftThumbstickY));
                                writer.Write(Axis(reading.RightThumbstickX));
                                writer.Write(Axis(-reading.RightThumbstickY));
                                writer.Write(Axis(reading.LeftTrigger));
                                writer.Write(Axis(reading.RightTrigger));
                                byte[] bytes = packet.GetBuffer();
                                bool changed = !sent;
                                for (int i = 0; i < lastState.Length; i++)
                                    if (lastState[i] != bytes[i + 8]) changed = true;
                                // Preserve 60 Hz input changes without flooding SLIRP
                                // with identical packets. Keep below the guest's 500 ms timeout.
                                if (changed || lastSend.ElapsedMilliseconds >= 200)
                                {
                                    await stream.WriteAsync(bytes, 0, 24, _stop.Token);
                                    Buffer.BlockCopy(bytes, 8, lastState, 0, lastState.Length);
                                    sent = true;
                                    lastSend.Restart();
                                }
                                await Task.Delay(16, _stop.Token);
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { return; }
                catch (Exception)
                {
                    try { await Task.Delay(1000, _stop.Token); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _client?.Dispose();
        }
    }
}
