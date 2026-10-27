using System;
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
                            while (!_stop.IsCancellationRequested)
                            {
                                var pads = Gamepad.Gamepads;
                                GamepadReading reading = pads.Count > 0 ? pads[0].GetCurrentReading() : default(GamepadReading);
                                packet.Position = 0;
                                writer.Write(new byte[] { 68, 68, 80, 49 }); // DDP1
                                writer.Write(++sequence);
                                writer.Write((uint)reading.Buttons & 0x3FFF); // Paddles do not invalidate standard pad packets.
                                writer.Write(Axis(reading.LeftThumbstickX));
                                writer.Write(Axis(-reading.LeftThumbstickY));
                                writer.Write(Axis(reading.RightThumbstickX));
                                writer.Write(Axis(-reading.RightThumbstickY));
                                writer.Write(Axis(reading.LeftTrigger));
                                writer.Write(Axis(reading.RightTrigger));
                                await stream.WriteAsync(packet.GetBuffer(), 0, 24, _stop.Token);
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
