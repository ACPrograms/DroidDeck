using System;
using System.IO;
using System.Text.Json;
using Windows.ApplicationModel;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.UI.Core;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Navigation;
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

        public DiagnosticState Diagnostics => _diagnostics;

        public MainPage()
        {
            this.InitializeComponent();

            // Disable TV overscan borders on Xbox displays
            ApplicationView.GetForCurrentView().SetDesiredBoundsMode(ApplicationViewBoundsMode.UseCoreWindow);

            // Handle Xbox gamepad B-button navigation
            SystemNavigationManager.GetForCurrentView().BackRequested += OnBackRequested;

            HostWebView.CoreWebView2Initialized += OnCoreWebView2Initialized;
            HostWebView.Loaded += OnPageLoaded;
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
                        }
                        else
                        {
                            _diagnostics.AddMessage("Received message of type: " + messageType);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _diagnostics.AddMessage("Failed to process web message: " + ex.Message);
            }
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
            if (HostWebView != null && HostWebView.CoreWebView2 != null && HostWebView.CanGoBack)
            {
                HostWebView.GoBack();
                e.Handled = true;
            }
        }
    }
}
