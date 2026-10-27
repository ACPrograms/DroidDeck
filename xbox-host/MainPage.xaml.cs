using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media.Imaging;
using Microsoft.Web.WebView2.Core;

namespace DroidDeckXboxHost
{
    public sealed partial class MainPage : Page
    {
        private const string VirtualHost = "droiddeck.local";
        private const string VirtualBaseUri = "https://" + VirtualHost + "/";

        private readonly DiagnosticState _diagnostics = new DiagnosticState();
        private string _uiAssetFolderPath;
        private bool _initializationStarted;
        private int? _webViewInitializationHResult;
        private Runtime.RuntimeSession _runtime;
        private readonly DispatcherTimer _guestTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        private WriteableBitmap _guestBitmap;
        private int _guestTicks;
        private bool _guestPanelBound;
        private bool _guestCapturePending;
        private bool _guestTickPending;
        private bool _frontendSuspendAttempted;
        private DateTimeOffset _guestRetryAfter;

        public DiagnosticState Diagnostics => _diagnostics;
        internal System.Threading.Tasks.Task PauseRuntimeAsync() => _runtime.PauseAsync();
        internal System.Threading.Tasks.Task ResumeRuntimeAsync() => _runtime.ResumeAsync();

        public MainPage()
        {
            this.InitializeComponent();

            // Disable TV overscan borders on Xbox displays
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);

            // Handle Xbox gamepad B-button navigation
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;

            HostWebView.CoreWebView2Initialized += OnCoreWebView2Initialized;
            HostWebView.Loaded += OnPageLoaded;
            _runtime = new Runtime.RuntimeSession(async (stage, percent, busy) =>
                await Dispatcher.RunAsync(CoreDispatcherPriority.Normal, () =>
                    HostWebView.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(new
                    {
                        type = "DROIDDECK_STATUS", ready = false, busy, stage, percent
                    }))));
            _guestTimer.Tick += OnGuestTick;
            _guestTimer.Start();
            Window.Current.CoreWindow.KeyDown += (sender, args) => ForwardFrontKey(args, true);
            Window.Current.CoreWindow.KeyUp += (sender, args) => ForwardFrontKey(args, false);
        }

        private async void ForwardFrontKey(KeyEventArgs args, bool down)
        {
            if (GuestSurface.Visibility == Visibility.Visible)
            {
                var backend = _runtime.Backend;
                if (backend != null && backend.IsRunning)
                    args.Handled = backend.InputBridge.ProcessKeyEvent(args.VirtualKey, args.KeyStatus, down);
                return;
            }
            if (HostWebView.CoreWebView2 == null ||
                !HostWebView.CoreWebView2.Source.StartsWith(VirtualBaseUri, StringComparison.OrdinalIgnoreCase)) return;
            string key;
            int code;
            switch ((int)args.VirtualKey)
            {
                case 13: case 195: key = "Enter"; code = 13; break;
                case 197: key = "x"; code = 88; break;
                case 198: key = "y"; code = 89; break;
                case 32: key = " "; code = 32; break;
                case 27: case 196: key = "Escape"; code = 27; break;
                case 37: case 205: key = "ArrowLeft"; code = 37; break;
                case 38: case 203: key = "ArrowUp"; code = 38; break;
                case 39: case 206: key = "ArrowRight"; code = 39; break;
                case 40: case 204: key = "ArrowDown"; code = 40; break;
                case 9: key = "Tab"; code = 9; break;
                case 200: key = "["; code = 219; break;
                case 199: key = "]"; code = 221; break;
                default: return;
            }
            args.Handled = true;
            if (down && args.KeyStatus.WasKeyDown && (code == 13 || code == 32)) return;
            try
            {
                string type = down ? "keydown" : "keyup";
                string domCode = code == 32 ? "Space" : code == 88 ? "KeyX" : code == 89 ? "KeyY" :
                    code == 219 ? "BracketLeft" : code == 221 ? "BracketRight" : key;
                string script = "(()=>{const r=document.body.shadowRoot||document;const c=r.querySelector('canvas');if(c){c.tabIndex=0;c.focus();c.dispatchEvent(new KeyboardEvent(" +
                    JsonSerializer.Serialize(type) + ",{key:" + JsonSerializer.Serialize(key) + ",code:" + JsonSerializer.Serialize(domCode) +
                    ",keyCode:" + code + ",which:" + code + ",repeat:" + (args.KeyStatus.WasKeyDown ? "true" : "false") +
                    ",bubbles:true,cancelable:true}));}})()";
                await HostWebView.CoreWebView2.ExecuteScriptAsync(script);
                if (!down)
                {
                    _diagnostics.AddMessage("Forwarded platform UI navigation");
                    await _diagnostics.SaveToDiskAsync();
                }
            }
            catch (Exception ex) { _diagnostics.AddMessage("UI input HRESULT=0x" + ex.HResult.ToString("X8")); }
        }

        private async void OnGuestTick(object sender, object args)
        {
            if (_guestTickPending || DateTimeOffset.UtcNow < _guestRetryAfter) return;
            _guestTickPending = true;
            try
            {
                if (++_guestTicks % 150 == 0)
                {
                    try { await _runtime.RefreshStatusAsync(); }
                    catch (Exception ex) { _diagnostics.AddMessage("Runtime status HRESULT=0x" + ex.HResult.ToString("X8")); }
                }
                var backend = _runtime.Backend;
                if (backend == null || !backend.IsRunning)
                {
                    GuestImage.Visibility = Visibility.Collapsed;
                    GuestPanel.Visibility = Visibility.Collapsed;
                    GuestSurface.Visibility = Visibility.Collapsed;
                    HostWebView.IsEnabled = true;
                    if (_frontendSuspendAttempted)
                    {
                        HostWebView.CoreWebView2?.Resume();
                        HostWebView.Visibility = Visibility.Visible;
                        _frontendSuspendAttempted = false;
                    }
                    return;
                }
                try
                {
                    if (!_frontendSuspendAttempted)
                    {
                        _frontendSuspendAttempted = true;
                        HostWebView.Visibility = Visibility.Collapsed;
                        await System.Threading.Tasks.Task.Delay(100);
                        try
                        {
                            bool suspended = await HostWebView.CoreWebView2.TrySuspendAsync();
                            _diagnostics.AddMessage("Launcher suspended for runtime: " + suspended);
                        }
                        catch (Exception ex) { _diagnostics.AddMessage("Launcher suspend HRESULT=0x" + ex.HResult.ToString("X8")); }
                    }
                    var display = backend.DisplayBridge;
                    if (display.IsDirectModeRequested)
                    {
                        if (!_guestPanelBound)
                        {
                            float scale = (float)Windows.Graphics.Display.DisplayInformation.GetForCurrentView().RawPixelsPerViewPixel;
                            GuestPanel.Visibility = Visibility.Visible;
                            GuestSurface.Visibility = Visibility.Visible;
                            display.BindSwapChainPanel(GuestPanel, ActualWidth, ActualHeight, scale, scale);
                            _guestPanelBound = true;
                        }
                        display.TryAttachPanel();
                        int directWidth, directHeight;
                        display.CheckDirectFrameUpdate(out directWidth, out directHeight);
                        if (display.DirectFrameCount > 0)
                        {
                            GuestImage.Visibility = Visibility.Collapsed;
                            HostWebView.IsEnabled = false;
                            return;
                        }
                    }
                    byte[] pixels = display.GetSharedFramebuffer();
                    int width, height;
                    if (!display.TryGetFallbackFrame(pixels, out width, out height))
                    {
                        if (_guestBitmap != null)
                            await CaptureGuestPreviewIfRequestedAsync(pixels, _guestBitmap.PixelWidth, _guestBitmap.PixelHeight);
                        return;
                    }
                    if (width <= 0 || height <= 0 || width > 2560 || height > 1600) return;
                    if (_guestBitmap == null || _guestBitmap.PixelWidth != width || _guestBitmap.PixelHeight != height)
                    {
                        _guestBitmap = new WriteableBitmap(width, height);
                        GuestImage.Source = _guestBitmap;
                    }
                    using (Stream stream = _guestBitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, width * height * 4);
                    _guestBitmap.Invalidate();
                    GuestImage.Visibility = Visibility.Visible;
                    GuestSurface.Visibility = Visibility.Visible;
                    HostWebView.IsEnabled = false;
                    backend.InputBridge.FlushAbsolute();
                    await CaptureGuestPreviewIfRequestedAsync(pixels, width, height);
                }
                catch (Exception ex)
                {
                    _diagnostics.AddMessage("Guest display HRESULT=0x" + ex.HResult.ToString("X8"));
                    if (ex.HResult == unchecked((int)0x800705AA) || ex.HResult == unchecked((int)0x8007000E))
                    {
                        GuestImage.Source = null;
                        _guestBitmap = null;
                        _guestRetryAfter = DateTimeOffset.UtcNow.AddSeconds(2);
                    }
                    else _guestTimer.Stop();
                }
            }
            finally { _guestTickPending = false; }
        }

        // Screenshot of the visible software display only; never exports guest disks or process memory.
        private async System.Threading.Tasks.Task CaptureGuestPreviewIfRequestedAsync(byte[] pixels, int width, int height)
        {
            string marker = Path.Combine(ApplicationData.Current.LocalFolder.Path, "runtime-preview.request");
            if (_guestCapturePending || !File.Exists(marker)) return;
            _guestCapturePending = true;
            try
            {
                File.Delete(marker);
                byte[] copy = new byte[checked(width * height * 4)];
                System.Buffer.BlockCopy(pixels, 0, copy, 0, copy.Length);
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "droiddeck-runtime-preview.png", CreationCollisionOption.ReplaceExisting);
                using (var output = await file.OpenAsync(FileAccessMode.ReadWrite))
                {
                    var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                        Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, output);
                    encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                        Windows.Graphics.Imaging.BitmapAlphaMode.Ignore, (uint)width, (uint)height, 96, 96, copy);
                    await encoder.FlushAsync();
                }
            }
            catch (Exception ex) { _diagnostics.AddMessage("Guest screenshot HRESULT=0x" + ex.HResult.ToString("X8")); }
            finally { _guestCapturePending = false; }
        }

        private void OnGuestPointer(object sender, PointerRoutedEventArgs args)
        {
            var backend = _runtime.Backend;
            var surface = sender as FrameworkElement;
            if (backend == null || !backend.IsRunning || surface == null) return;
            var display = backend.DisplayBridge;
            int width = display.DirectFrameCount > 0 ? display.LastDirectWidth : (_guestBitmap?.PixelWidth ?? 0);
            int height = display.DirectFrameCount > 0 ? display.LastDirectHeight : (_guestBitmap?.PixelHeight ?? 0);
            if (width <= 0 || height <= 0) return;
            var point = args.GetCurrentPoint(surface);
            int buttons = (point.Properties.IsLeftButtonPressed ? 1 : 0) |
                (point.Properties.IsRightButtonPressed ? 2 : 0) | (point.Properties.IsMiddleButtonPressed ? 4 : 0);
            backend.InputBridge.UpdateAbsolutePointer(point.Position.X, point.Position.Y,
                surface.ActualWidth, surface.ActualHeight, width, height,
                point.Properties.MouseWheelDelta / 120, buttons);
            backend.InputBridge.FlushAbsolute();
            args.Handled = true;
        }

        private void OnCoreWebView2Initialized(Microsoft.UI.Xaml.Controls.WebView2 sender,
            Microsoft.UI.Xaml.Controls.CoreWebView2InitializedEventArgs args)
        {
            if (args.Exception != null)
            {
                _webViewInitializationHResult = args.Exception.HResult;
                _diagnostics.AddMessage("CoreWebView2 initialization HRESULT=0x" + args.Exception.HResult.ToString("X8"));
            }
            else _diagnostics.AddMessage("CoreWebView2 initialization event succeeded");
        }

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            if (_initializationStarted) return;
            _initializationStarted = true;
            try { await StoragePreflight.RunIfRequestedAsync(); }
            catch (Exception ex) { _diagnostics.AddMessage("Storage preflight HRESULT=0x" + ex.HResult.ToString("X8")); }
            _diagnostics.TransitionTo(HostLifecycleState.InitializingWebView2, "Starting EnsureCoreWebView2Async");

            try
            {
                // Resolve path to bundled Compose Wasm production assets
                _uiAssetFolderPath = Path.Combine(Package.Current.InstalledLocation.Path, "Assets", "DroidDeckUI");
                _diagnostics.AddMessage("Asset folder configured: " + _uiAssetFolderPath);

                // Initialize WebView2 environment and CoreWebView2
                await HostWebView.EnsureCoreWebView2Async();

                if (HostWebView.CoreWebView2 == null)
                {
                    _diagnostics.RecordError("CoreWebView2 is null; initialization HRESULT=" +
                        (_webViewInitializationHResult.HasValue ? "0x" + _webViewInitializationHResult.Value.ToString("X8") : "unavailable"));
                    await _diagnostics.SaveToDiskAsync();
                    return;
                }

                ConfigureWebView2(HostWebView.CoreWebView2);

                _diagnostics.TransitionTo(HostLifecycleState.Navigating, "Navigating to " + VirtualBaseUri + "index.html");
                HostWebView.CoreWebView2.Navigate(VirtualBaseUri + "index.html");
            }
            catch (Exception ex)
            {
                _diagnostics.RecordError("Initialization failed: " + ex.Message);
                await _diagnostics.SaveToDiskAsync();
            }
        }

        private void ConfigureWebView2(CoreWebView2 core)
        {
            // Configure security and offline mappings
            core.Settings.IsScriptEnabled = true;
            core.Settings.AreDefaultScriptDialogsEnabled = true;
            core.Settings.IsWebMessageEnabled = true;

            // Primary virtual host to folder mapping
            core.SetVirtualHostNameToFolderMapping(
                VirtualHost,
                _uiAssetFolderPath,
                CoreWebView2HostResourceAccessKind.Allow
            );
            _diagnostics.TransitionTo(HostLifecycleState.AssetsMapped, "Virtual host " + VirtualHost + " mapped to " + _uiAssetFolderPath);

            // Explicit MIME type enforcement filter for offline WASM correctness
            // Xbox Dev Mode OS lacks .wasm registry associations; WebResourceRequested guarantees application/wasm
            core.AddWebResourceRequestedFilter(VirtualBaseUri + "*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnWebResourceRequested;

            // Web message handling for UI readiness signaling
            core.WebMessageReceived += OnWebMessageReceived;

            // Diagnostics and process lifecycle monitoring
            core.NavigationCompleted += OnNavigationCompleted;
            core.ProcessFailed += OnProcessFailed;
        }

        private static string GetMimeType(string extension)
        {
            switch (extension)
            {
                case ".wasm":
                    return "application/wasm";
                case ".js":
                case ".mjs":
                    return "application/javascript";
                case ".html":
                case ".htm":
                    return "text/html; charset=utf-8";
                case ".json":
                case ".map":
                    return "application/json";
                case ".css":
                    return "text/css";
                case ".png":
                    return "image/png";
                case ".svg":
                    return "image/svg+xml";
                default:
                    return "application/octet-stream";
            }
        }

        private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                string requestUri = args.Request.Uri;
                if (!requestUri.StartsWith(VirtualBaseUri, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                string relativePath = requestUri.Substring(VirtualBaseUri.Length).Split('?')[0].TrimStart('/');
                if (string.IsNullOrEmpty(relativePath))
                {
                    relativePath = "index.html";
                }

                // Canonicalize and strictly enforce asset-root-prefix to prevent directory traversal
                string rootPath = Path.GetFullPath(_uiAssetFolderPath);
                if (!rootPath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                {
                    rootPath += Path.DirectorySeparatorChar;
                }

                string safeRelativePath = relativePath.Replace('/', Path.DirectorySeparatorChar);
                string combinedPath = Path.Combine(_uiAssetFolderPath, safeRelativePath);
                string fullPath = Path.GetFullPath(combinedPath);

                if (!fullPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(
                        null,
                        403,
                        "Forbidden",
                        "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *"
                    );
                    return;
                }

                if (!File.Exists(fullPath))
                {
                    args.Response = sender.Environment.CreateWebResourceResponse(
                        null,
                        404,
                        "Not Found",
                        "Content-Type: text/plain\r\nAccess-Control-Allow-Origin: *"
                    );
                    return;
                }

                string ext = Path.GetExtension(fullPath).ToLowerInvariant();
                bool isWasm = (ext == ".wasm");
                string mimeType = GetMimeType(ext);

                _diagnostics.RecordInterception(isWasm);

                // In WinRT projection (UWP WinUI 2), CreateWebResourceResponse requires Windows.Storage.Streams.IRandomAccessStream
                FileStream fileStream = File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                IRandomAccessStream randomAccessStream = fileStream.AsRandomAccessStream();
                string headers = "Content-Type: " + mimeType + "\r\nAccess-Control-Allow-Origin: *\r\nCache-Control: no-cache";

                args.Response = sender.Environment.CreateWebResourceResponse(
                    randomAccessStream,
                    200,
                    "OK",
                    headers
                );
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("[WebResourceRequested] Error: " + ex.Message);
            }
        }

        private async void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        {
            try
            {
                // Verify message origin matches our virtual host
                string sourceOrigin = args.Source;
                if (string.IsNullOrEmpty(sourceOrigin) || !sourceOrigin.StartsWith(VirtualBaseUri, StringComparison.OrdinalIgnoreCase))
                {
                    _diagnostics.AddMessage("Ignored message from unauthorized origin: " + (sourceOrigin ?? "null"));
                    return;
                }

                string rawMessage = args.TryGetWebMessageAsString();
                if (string.IsNullOrEmpty(rawMessage))
                {
                    return;
                }

                // Parse message without logging arbitrary payload
                using (JsonDocument doc = JsonDocument.Parse(rawMessage))
                {
                    JsonElement root = doc.RootElement;
                    if (root.TryGetProperty("type", out JsonElement typeProp))
                    {
                        string messageType = typeProp.GetString();
                        if (string.Equals(messageType, "DROIDDECK_UI_READY", StringComparison.OrdinalIgnoreCase))
                        {
                            HostWebView.Focus(FocusState.Programmatic);
                            _diagnostics.TransitionTo(HostLifecycleState.UiReady, "Compose Wasm dispatched DROIDDECK_UI_READY");
                            await _diagnostics.SaveToDiskAsync();

                            // Send safe acknowledgement back to Web context
                            string ack = JsonSerializer.Serialize(new
                            {
                                type = "HOST_ACK",
                                status = "ready",
                                timestamp = DateTimeOffset.UtcNow,
                                platform = "Xbox_UWP_WebView2"
                            });
                            sender.PostWebMessageAsString(ack);
                            await CaptureUiPreviewAsync(sender);
                            await _runtime.StartIfRequestedAsync();
                        }
                        else if (messageType == "DROIDDECK_PLAY_STEAM")
                        {
                            _diagnostics.AddMessage("Original Play action received");
                            await _diagnostics.SaveToDiskAsync();
                            await _runtime.StartIfRequestedAsync();
                        }
                        else
                        {
                            _diagnostics.AddMessage("Unsupported UI message");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _diagnostics.AddMessage("Failed to process web message: " + ex.Message);
            }
        }

        private async System.Threading.Tasks.Task CaptureUiPreviewAsync(CoreWebView2 core)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(1000);
                var preview = await ApplicationData.Current.LocalFolder.CreateFileAsync(
                    "droiddeck-ui-preview.png", CreationCollisionOption.ReplaceExisting);
                using (var stream = await preview.OpenAsync(FileAccessMode.ReadWrite))
                    await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
                _diagnostics.AddMessage("UI preview captured");
            }
            catch (Exception ex)
            {
                _diagnostics.AddMessage("UI preview HRESULT=0x" + ex.HResult.ToString("X8"));
            }
            await _diagnostics.SaveToDiskAsync();
        }

        private async void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess)
            {
                _diagnostics.AddMessage("Navigation succeeded (HTTP " + args.HttpStatusCode + ")");
            }
            else
            {
                _diagnostics.RecordError("Navigation failed: " + args.WebErrorStatus + " (HTTP " + args.HttpStatusCode + ")");
                await _diagnostics.SaveToDiskAsync();
            }
        }

        private async void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        {
            _diagnostics.RecordError("WebView2 Core Process Failed: " + args.ProcessFailedKind + " (Reason: " + args.Reason + ")");
            await _diagnostics.SaveToDiskAsync();
        }

        private void OnBackRequested(object sender, BackRequestedEventArgs e)
        {
            if (_runtime.Backend != null && _runtime.Backend.IsRunning)
            {
                e.Handled = true;
                return;
            }
            if (HostWebView != null && HostWebView.CoreWebView2 != null && HostWebView.CanGoBack)
            {
                HostWebView.GoBack();
                e.Handled = true;
            }
        }
    }
}
