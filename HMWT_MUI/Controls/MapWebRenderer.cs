using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace HMWT_MUI.Controls
{
    /// <summary>Карта на WebView2 + PixiJS. WPF остаётся оболочкой и панелью,
    /// а вся отрисовка карты уходит в WebGL.</summary>
    public sealed class MapWebRenderer : UserControl
    {
        private readonly WebView2 _web = new WebView2 { Visibility = Visibility.Collapsed };
        private static readonly List<WeakReference> _live = new List<WeakReference>();
        private static int _dialogDepth;
        private bool _started;
        private CoreWebView2 _core;
        private string _pending = "";

        public event Action Ready;
        public event Action Failed;
        public event Action<int, double, double> TokenMoved;
        public event Action<int, double, double, List<Net.MapTrailPoint>> TokenMovedWithTrail;

        public bool IsReady { get; private set; }

        /// <summary>Модальный WPF-диалог должен быть выше HWND WebView2.</summary>
        public static void DialogOpened()
        {
            _dialogDepth++;
            foreach (var view in Alive()) view.ApplyWebVisibility();
        }

        public static void DialogClosed()
        {
            if (_dialogDepth > 0) _dialogDepth--;
            foreach (var view in Alive()) view.ApplyWebVisibility();
        }

        /// <summary>Показывает уведомление поверх WebGL-карты, а не под ней.</summary>
        public static void ShowToast(string text, bool error)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            foreach (var view in Alive())
            {
                if (!view.IsReady || view._core == null) continue;
                try
                {
                    view._core.ExecuteScriptAsync(
                        "window.__hmwtToast(" + JsonSerializer.Serialize(text) + "," +
                        (error ? "true" : "false") + ")");
                }
                catch { }
            }
        }

        private static IEnumerable<MapWebRenderer> Alive()
        {
            foreach (var weak in _live.ToList())
            {
                MapWebRenderer view;
                try { view = weak.Target as MapWebRenderer; } catch { view = null; }
                if (view == null) _live.Remove(weak);
                else yield return view;
            }
        }

        private void ApplyWebVisibility()
        {
            _web.Visibility = IsVisible && IsReady && _dialogDepth == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        public MapWebRenderer()
        {
            HorizontalAlignment = HorizontalAlignment.Stretch;
            VerticalAlignment = VerticalAlignment.Stretch;
            HorizontalContentAlignment = HorizontalAlignment.Stretch;
            VerticalContentAlignment = VerticalAlignment.Stretch;
            Background = System.Windows.Media.Brushes.Transparent;
            IsHitTestVisible = false;
            Content = new Grid { Children = { _web } };
            Loaded += (s, e) =>
            {
                if (!_live.Any(w => ReferenceEquals(w.Target, this)))
                    _live.Add(new WeakReference(this));
                if (IsVisible) Start();
            };
            Unloaded += (s, e) =>
            {
                _live.RemoveAll(w => ReferenceEquals(w.Target, this));
                _web.Visibility = Visibility.Collapsed;
            };
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible) Start();
                ApplyWebVisibility();
            };
        }

        private async void Start()
        {
            if (_started) return;
            _started = true;

            try
            {
                string root = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Web", "map");
                if (!File.Exists(Path.Combine(root, "index.html")))
                    throw new FileNotFoundException("Web/map/index.html не найден");

                var environment = await Helpers.WebView2Host.GetEnvironmentAsync();
                // WebView2 с Visibility=Collapsed не получает HWND и может
                // зависнуть на EnsureCoreWebView2Async. Показываем контрол,
                // но клики до ready отсекает IsHitTestVisible=false.
                _web.Visibility = IsVisible && _dialogDepth == 0
                    ? Visibility.Visible : Visibility.Collapsed;
                await _web.EnsureCoreWebView2Async(environment);
                if (_web.CoreWebView2 == null) throw new InvalidOperationException("WebView2 не создан");

                _core = _web.CoreWebView2;
                try
                {
                    // Старый map.js мог остаться в дисковом кэше WebView2 и
                    // перекрывать новую разметку/мост. Чистим кэш перед загрузкой.
                    await _core.Profile.ClearBrowsingDataAsync(
                        CoreWebView2BrowsingDataKinds.DiskCache);
                }
                catch (Exception cacheEx) { Debug.WriteLine("[MapWeb] cache: " + cacheEx.Message); }
                _core.Settings.AreDefaultContextMenusEnabled = false;
                _core.Settings.IsStatusBarEnabled = false;
                _core.Settings.AreDevToolsEnabled = false;
                _core.Settings.IsZoomControlEnabled = false;
                _core.Settings.IsSwipeNavigationEnabled = false;
                _core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                _core.Settings.IsWebMessageEnabled = true;
                _core.WebMessageReceived += OnWebMessage;
                _core.SetVirtualHostNameToFolderMapping("hmwt.map",
                    root, CoreWebView2HostResourceAccessKind.Allow);
                // Картинки карты и аватарки лежат в AppData. Отдаём их
                // локальным виртуальным хостом, а не многомегабайтным base64
                // через postMessage: 7 МБ JSON не доходили до WebView.
                _core.SetVirtualHostNameToFolderMapping("hmwt.assets",
                    HMWT_MUI.PhotoHelper.GetWritableRoot(), CoreWebView2HostResourceAccessKind.Allow);

                _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0B, 0x0D, 0x16);
                _web.Visibility = Visibility.Visible;
                _core.Navigate("https://hmwt.map/index.html?v=1326");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MapWeb] init: " + ex.Message);
                _web.Visibility = Visibility.Collapsed;
                IsHitTestVisible = false;
                Failed?.Invoke();
            }
        }

        public void SendState(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            _pending = json;
            if (!IsReady || _core == null) return;

            try
            {
                _core.PostWebMessageAsJson(json);
                _pending = "";
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MapWeb] send: " + ex.Message);
                try
                {
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "hmwt-map-web.log"),
                        DateTime.Now.ToString("HH:mm:ss.fff") + " send error: " + ex + Environment.NewLine);
                }
                catch { }
            }
        }

        private void OnWebMessage(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var t) ? t.GetString() : "";

                if (type == "ready")
                {
                    IsReady = true;
                    IsHitTestVisible = true;
                    ApplyWebVisibility();
                    if (!string.IsNullOrEmpty(_pending))
                    {
                        _core.PostWebMessageAsJson(_pending);
                        _pending = "";
                    }
                    Ready?.Invoke();
                    return;
                }

                if (type == "moveToken" &&
                    root.TryGetProperty("id", out var idEl) &&
                    root.TryGetProperty("x", out var xEl) &&
                    root.TryGetProperty("y", out var yEl))
                {
                    int id = idEl.GetInt32();
                    double x = xEl.GetDouble();
                    double y = yEl.GetDouble();
                    var trail = new List<Net.MapTrailPoint>();
                    if (root.TryGetProperty("points", out var pts) && pts.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var p in pts.EnumerateArray())
                        {
                            if (p.TryGetProperty("x", out var px) && p.TryGetProperty("y", out var py))
                                trail.Add(new Net.MapTrailPoint { X = px.GetDouble(), Y = py.GetDouble() });
                        }
                    }
                    TokenMoved?.Invoke(id, x, y);
                    TokenMovedWithTrail?.Invoke(id, x, y, trail);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MapWeb] message: " + ex.Message);
            }
        }
    }
}
