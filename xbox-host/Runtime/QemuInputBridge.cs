using System;
using System.Collections.Generic;
using Windows.System;
using Windows.UI.Core;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Bridges UWP keyboard and pointer events into QEMU native input exports
    /// (qemu_dll_key, qemu_dll_mouse, and qemu_dll_abs).
    /// </summary>
    public sealed class QemuInputBridge
    {
        private static readonly Dictionary<VirtualKey, int> FallbackVkMap = InitializeFallbackVkMap();
        private readonly object _syncLock = new object();

        // Mouse button bitmask: 1 = Left, 2 = Right, 4 = Middle
        private int _mouseButtons;

        // Absolute tablet state (normalized 0..32767)
        private int _absX;
        private int _absY;
        private int _absWheel;
        private bool _absDirty;

        // Diagnostics
        private long _keysDispatched;
        private long _relativeMouseDispatched;
        private long _absMouseDispatched;

        public long KeysDispatched => _keysDispatched;
        public long RelativeMouseDispatched => _relativeMouseDispatched;
        public long AbsoluteMouseDispatched => _absMouseDispatched;
        public int PendingAbsoluteX => _absX;
        public int PendingAbsoluteY => _absY;
        public bool IsAbsoluteDirty => _absDirty;

        /// <summary>
        /// Translates a UWP VirtualKey and physical key status into a QEMU scancode.
        /// Returns 0 if key should be ignored (e.g. controller buttons).
        /// </summary>
        public static int ResolveScancode(VirtualKey vkey, CorePhysicalKeyStatus status)
        {
            int vk = (int)vkey;
            // Filter Xbox controller buttons mapped to virtual keys (195 - 218)
            if (vk >= 195 && vk <= 218)
            {
                return 0;
            }

            uint sc = status.ScanCode;
            if (sc != 0 && sc < 0x80)
            {
                return (int)sc | (status.IsExtendedKey ? 0x80 : 0);
            }

            if (FallbackVkMap.TryGetValue(vkey, out int mapped))
            {
                return mapped;
            }

            return 0;
        }

        /// <summary>
        /// Processes a key event from UWP CoreDispatcher AcceleratorKeyActivated.
        /// Filters key repeats on KeyDown to let guest Linux kernel handle autorepeat.
        /// </summary>
        public bool ProcessKeyEvent(VirtualKey vkey, CorePhysicalKeyStatus status, bool isDown)
        {
            // Drop duplicate down events (guest OS handles key repeat)
            if (isDown && status.WasKeyDown)
            {
                return false;
            }

            int scancode = ResolveScancode(vkey, status);
            if (scancode == 0)
            {
                return false;
            }

            try
            {
                NativeQemuBindings.qemu_dll_key(scancode, isDown ? 1 : 0);
                System.Threading.Interlocked.Increment(ref _keysDispatched);
                return true;
            }
            catch (DllNotFoundException)
            {
                return false;
            }
        }

        /// <summary>
        /// Sends raw relative mouse displacement (e.g. from USB mouse MouseDevice.MouseMoved).
        /// </summary>
        public void SendRelativeMouse(int dx, int dy, int wheel, int buttons)
        {
            _mouseButtons = buttons;
            try
            {
                NativeQemuBindings.qemu_dll_mouse(dx, dy, wheel, buttons);
                System.Threading.Interlocked.Increment(ref _relativeMouseDispatched);
            }
            catch (DllNotFoundException)
            {
            }
        }

        /// <summary>
        /// Updates button state without relative motion.
        /// </summary>
        public void UpdateMouseButtons(int buttons)
        {
            _mouseButtons = buttons;
            try
            {
                NativeQemuBindings.qemu_dll_mouse(0, 0, 0, buttons);
            }
            catch (DllNotFoundException)
            {
            }
        }

        /// <summary>
        /// Pure mathematical mapping of container pointer coordinates to normalized virtio-tablet coordinates (0..32767).
        /// Handles aspect-ratio preservation (letterbox/pillarbox) and bounds clamping.
        /// </summary>
        public static bool CalculateNormalizedCoordinates(
            double pointerX,
            double pointerY,
            double containerWidth,
            double containerHeight,
            int virtualDisplayWidth,
            int virtualDisplayHeight,
            out int tabletX,
            out int tabletY)
        {
            tabletX = 0;
            tabletY = 0;
            if (containerWidth <= 0 || containerHeight <= 0 || virtualDisplayWidth <= 0 || virtualDisplayHeight <= 0)
            {
                return false;
            }

            double scale = Math.Min(containerWidth / virtualDisplayWidth, containerHeight / virtualDisplayHeight);
            double displayW = virtualDisplayWidth * scale;
            double displayH = virtualDisplayHeight * scale;

            double offsetX = (containerWidth - displayW) / 2.0;
            double offsetY = (containerHeight - displayH) / 2.0;

            double normalizedX = (pointerX - offsetX) / displayW;
            double normalizedY = (pointerY - offsetY) / displayH;

            // Clamp to guest display surface [0.0, 1.0]
            normalizedX = Math.Max(0.0, Math.Min(1.0, normalizedX));
            normalizedY = Math.Max(0.0, Math.Min(1.0, normalizedY));

            tabletX = (int)(normalizedX * 32767);
            tabletY = (int)(normalizedY * 32767);
            return true;
        }

        /// <summary>
        /// Calculates letterboxed/pillarboxed absolute virtio-tablet coordinates (0..32767).
        /// Latches coordinates into pending state to be flushed periodically.
        /// </summary>
        public void UpdateAbsolutePointer(
            double pointerX,
            double pointerY,
            double containerWidth,
            double containerHeight,
            int virtualDisplayWidth,
            int virtualDisplayHeight,
            int wheel,
            int buttons)
        {
            int tx, ty;
            if (!CalculateNormalizedCoordinates(pointerX, pointerY, containerWidth, containerHeight, virtualDisplayWidth, virtualDisplayHeight, out tx, out ty))
            {
                return;
            }

            lock (_syncLock)
            {
                _absX = tx;
                _absY = ty;
                _mouseButtons = buttons;
                if (wheel != 0) _absWheel = wheel;
                _absDirty = true;
            }
        }

        /// <summary>
        /// Flushes pending absolute tablet pointer coordinates to QEMU.
        /// Call at 60 Hz or on display frame ticks to prevent flooding TCG CPU.
        /// </summary>
        public void FlushAbsolute()
        {
            int x, y, wheel, buttons;
            bool dirty;

            lock (_syncLock)
            {
                dirty = _absDirty;
                if (!dirty) return;
                _absDirty = false;
                x = _absX;
                y = _absY;
                wheel = _absWheel;
                _absWheel = 0;
                buttons = _mouseButtons;
            }

            try
            {
                NativeQemuBindings.qemu_dll_abs(x, y, wheel, buttons);
                System.Threading.Interlocked.Increment(ref _absMouseDispatched);
            }
            catch (DllNotFoundException)
            {
            }
        }

        private static Dictionary<VirtualKey, int> InitializeFallbackVkMap()
        {
            var map = new Dictionary<VirtualKey, int>();

            string[] rows = { "qwertyuiop", "asdfghjkl", "zxcvbnm", "1234567890" };
            int[] firstScancodes = { 0x10, 0x1E, 0x2C, 0x02 };

            for (int r = 0; r < rows.Length; r++)
            {
                string row = rows[r];
                for (int i = 0; i < row.Length; i++)
                {
                    VirtualKey vk = (VirtualKey)char.ToUpperInvariant(row[i]);
                    map[vk] = firstScancodes[r] + i;
                }
            }

            map[VirtualKey.Escape] = 0x01;
            map[VirtualKey.Back] = 0x0E;
            map[VirtualKey.Tab] = 0x0F;
            map[VirtualKey.Enter] = 0x1C;
            map[VirtualKey.Space] = 0x39;
            map[VirtualKey.Shift] = 0x2A;
            map[VirtualKey.Control] = 0x1D;
            map[VirtualKey.Menu] = 0x38; // Alt
            map[VirtualKey.Up] = 0xC8;
            map[VirtualKey.Left] = 0xCB;
            map[VirtualKey.Right] = 0xCD;
            map[VirtualKey.Down] = 0xD0;
            map[VirtualKey.Delete] = 0xD3;

            for (int f = 0; f < 10; f++)
            {
                map[(VirtualKey)((int)VirtualKey.F1 + f)] = 0x3B + f;
            }
            map[VirtualKey.F11] = 0x57;
            map[VirtualKey.F12] = 0x58;

            return map;
        }
    }
}
