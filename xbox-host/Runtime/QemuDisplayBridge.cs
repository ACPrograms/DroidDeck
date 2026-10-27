using System;
using System.Runtime.InteropServices;

namespace DroidDeckXboxHost.Runtime
{
    /// <summary>
    /// Bridges the guest display rendering to UWP presentation surfaces.
    /// Supports direct SwapChainPanel GPU presentation (Direct3D 11 via ANGLE)
    /// and software memory framebuffer readback fallback.
    /// </summary>
    public sealed class QemuDisplayBridge : IDisposable
    {
        private const int MaxFramebufferWidth = 2560;
        private const int MaxFramebufferHeight = 1600;
        private const int MaxFramebufferBytes = MaxFramebufferWidth * MaxFramebufferHeight * 4;

        private readonly object _syncLock = new object();
        private IntPtr _panelIUnknown = IntPtr.Zero;
        private bool _isPanelAttached;
        private int _directFrameCount;
        private int _lastDirectWidth;
        private int _lastDirectHeight;
        private int _frameSequence;
        private byte[] _sharedFrameBuffer;
        private bool _disposed;

        public bool IsDirectModeRequested { get; private set; }
        public bool IsPanelAttached => _isPanelAttached;
        public int DirectFrameCount => _directFrameCount;
        public int LastDirectWidth => _lastDirectWidth;
        public int LastDirectHeight => _lastDirectHeight;
        public int FrameSequence => _frameSequence;

        public QemuDisplayBridge(bool isDirectMode)
        {
            IsDirectModeRequested = isDirectMode;
        }

        /// <summary>
        /// Binds a UWP SwapChainPanel for direct GPU swapchain presentation.
        /// Must be called before or during VM startup.
        /// </summary>
        public void BindSwapChainPanel(object swapChainPanel, double actualWidth, double actualHeight, float scaleX, float scaleY)
        {
            if (swapChainPanel == null) throw new ArgumentNullException(nameof(swapChainPanel));

            lock (_syncLock)
            {
                UnbindPanelInternal();

                _panelIUnknown = Marshal.GetIUnknownForObject(swapChainPanel);
                NativeQemuBindings.qemu_dll_set_panel(_panelIUnknown);

                int pixelW = (int)Math.Round(Math.Max(actualWidth, 16) * scaleX);
                int pixelH = (int)Math.Round(Math.Max(actualHeight, 16) * scaleY);

                NativeQemuBindings.qemu_dll_set_panel_size(pixelW, pixelH, scaleX, scaleY);
                _isPanelAttached = false;
                _directFrameCount = 0;
            }
        }

        /// <summary>
        /// Attaches the native swap chain to the SwapChainPanel on the UI/Composition thread.
        /// Should be called on each render tick until attach succeeds (returns true).
        /// </summary>
        public bool TryAttachPanel()
        {
            lock (_syncLock)
            {
                if (_panelIUnknown == IntPtr.Zero) return false;
                if (_isPanelAttached) return true;

                int result = NativeQemuBindings.qemu_dll_attach_panel();
                _isPanelAttached = (result == 1);
                return _isPanelAttached;
            }
        }

        /// <summary>
        /// Queries the direct GPU presentation frame count and display resolution.
        /// Returns true if a new direct frame was presented.
        /// </summary>
        public bool CheckDirectFrameUpdate(out int width, out int height)
        {
            width = 0;
            height = 0;

            lock (_syncLock)
            {
                if (_panelIUnknown == IntPtr.Zero) return false;

                int dw, dh;
                int count = NativeQemuBindings.qemu_dll_direct_info(out dw, out dh);
                if (count != _directFrameCount && dw > 0 && dh > 0)
                {
                    _directFrameCount = count;
                    _lastDirectWidth = dw;
                    _lastDirectHeight = dh;
                    width = dw;
                    height = dh;
                    return true;
                }

                width = _lastDirectWidth;
                height = _lastDirectHeight;
                return false;
            }
        }

        /// <summary>
        /// Reads back a frame from the public software framebuffer (fallback mode).
        /// Returns true if a new frame sequence was retrieved.
        /// </summary>
        public bool TryGetFallbackFrame(byte[] destinationBuffer, out int width, out int height)
        {
            width = 0;
            height = 0;

            if (destinationBuffer == null || destinationBuffer.Length < MaxFramebufferBytes)
            {
                return false;
            }

            int seq = _frameSequence;
            int w, h;
            int ret = NativeQemuBindings.qemu_dll_get_frame(destinationBuffer, destinationBuffer.Length, out w, out h, ref seq);
            if (ret != 0 && w > 0 && h > 0)
            {
                _frameSequence = seq;
                width = w;
                height = h;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Requests a snapshot copy from the GPU direct swapchain into the software frame buffer.
        /// </summary>
        public void RequestSnapshot()
        {
            NativeQemuBindings.qemu_dll_request_snapshot();
        }

        /// <summary>
        /// Allocates or returns a reusable pinned byte buffer for software readback.
        /// </summary>
        public byte[] GetSharedFramebuffer()
        {
            if (_sharedFrameBuffer == null)
            {
                _sharedFrameBuffer = new byte[MaxFramebufferBytes];
            }
            return _sharedFrameBuffer;
        }

        public void UnbindPanel()
        {
            lock (_syncLock)
            {
                UnbindPanelInternal();
            }
        }

        private void UnbindPanelInternal()
        {
            if (_panelIUnknown != IntPtr.Zero)
            {
                NativeQemuBindings.qemu_dll_set_panel(IntPtr.Zero);
                Marshal.Release(_panelIUnknown);
                _panelIUnknown = IntPtr.Zero;
            }
            _isPanelAttached = false;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            UnbindPanel();
            _sharedFrameBuffer = null;
        }
    }
}
