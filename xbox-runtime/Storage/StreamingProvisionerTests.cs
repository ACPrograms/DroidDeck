using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace DroidDeck.XboxRuntime.Storage
{
    internal static class StreamingProvisionerTests
    {
        private sealed class Fragments : Stream
        {
            private readonly MemoryStream _data;
            public bool Closed;
            public Action AfterRead;
            public int Reads;
            public Fragments(byte[] data) { _data = new MemoryStream(data); }
            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override int Read(byte[] data, int offset, int count)
            {
                Reads++;
                int read = _data.Read(data, offset, Math.Min(count, 17));
                AfterRead?.Invoke();
                return read;
            }
            protected override void Dispose(bool disposing) { Closed = true; _data.Dispose(); base.Dispose(disposing); }
            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] data, int offset, int count) => throw new NotSupportedException();
        }

        private static void Check(bool ok, string message)
        {
            if (!ok) throw new InvalidOperationException(message);
        }

        public static int RunAllTests()
        {
            string dir = Path.Combine(Path.GetTempPath(), "droiddeck-stream-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                byte[] payload = new byte[65568];
                payload[0] = 0xA5;
                payload[payload.Length - 1] = 0xFF;
                string hash;
                using (var sha = SHA512.Create()) hash = BitConverter.ToString(sha.ComputeHash(payload)).Replace("-", "").ToLowerInvariant();
                var provisioner = new SparseRawProvisioner();
                foreach (string scenario in new[] { "exact", "short", "excess", "cancel", "checksum", "existing" })
                {
                    byte[] input = scenario == "excess" ? new byte[payload.Length + 1] : payload;
                    if (scenario == "excess") Buffer.BlockCopy(payload, 0, input, 0, payload.Length);
                    var options = new ProvisionOptions
                    {
                        ExpectedSha512 = scenario == "checksum" ? new string('0', 128) : hash,
                        ExpectedSourceSizeBytes = payload.Length + (scenario == "short" ? 1 : 0),
                        TargetLogicalSizeBytes = 8 * 1024 * 1024
                    };
                    string destination = Path.Combine(dir, scenario + ".raw");
                    using (var source = new Fragments(input))
                    using (var cancel = new CancellationTokenSource())
                    {
                        if (scenario == "cancel") source.AfterRead = () => { if (source.Reads == 3) cancel.Cancel(); };
                        if (scenario == "existing") File.WriteAllBytes(destination, new byte[] { 42 });
                        var result = provisioner.ProvisionRawImage(source, destination, options, null, cancel.Token);
                        Check(!source.Closed, scenario + ": caller stream was closed");
                        if (scenario == "exact")
                        {
                            Check(result.Success && result.Sha512Verified && result.IsSparseConfirmed, "Fragmented nonseekable stream failed");
                            using (var file = File.OpenRead(destination))
                            {
                                Check(file.Length == options.TargetLogicalSizeBytes, "Wrong capacity");
                                byte[] copy = new byte[payload.Length];
                                file.ReadExactly(copy, 0, copy.Length);
                                using (var sha = SHA512.Create()) Check(BitConverter.ToString(sha.ComputeHash(copy)) == BitConverter.ToString(sha.ComputeHash(payload)), "Wrong payload");
                            }
                        }
                        else
                        {
                            Check(!result.Success, scenario + ": invalid stream accepted");
                            if (scenario == "existing")
                                Check(source.Reads == 0 && File.ReadAllBytes(destination)[0] == 42, "Existing destination changed");
                            else Check(!File.Exists(destination), scenario + ": partial output committed");
                            Check(result.TotalSourceBytesRead <= options.ExpectedSourceSizeBytes, "Excess data written");
                        }
                        Check(Directory.GetFiles(dir, "*.tmp.*").Length == 0, scenario + ": owned temporary leaked");
                        Console.WriteLine("PASS stream " + scenario);
                    }
                }
                return 0;
            }
            finally { Directory.Delete(dir, true); }
        }
    }
}
