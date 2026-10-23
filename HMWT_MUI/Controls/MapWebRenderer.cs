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
        private int _stateMessages;

        public event Action Ready;
        public event Action Failed;
        public event Action<int, double, double> TokenMoved;
        public event Action<int, double, double, List<Net.MapTrailPoint>> TokenMovedWithTrail;
        public event Action<double, double> ShapePlaced;

        /// <summary>ПКМ по пустому месту: поставить точку интереса.</summary>
        public event Action<double, double> PointPlaced;

        /// <summary>Перенос группы жетонов: пары (id, x, y) одним пакетом.</summary>
        public event Action<List<(int Id, double X, double Y)>> TokensMoved;
        public event Action<double, double> Pinged;

        /// <summary>Жетон тянут: промежуточное положение без записи в базу.</summary>
        public event Action<int, double, double> TokenMovedLive;

        /// <summary>Фигуру двигают. commit = false — только показ, true — отпустили.</summary>
        public event Action<int, double, double, bool> ShapeMoved;

        /// <summary>Готовый штрих кисти (или след ластика) из WebView.</summary>
        public event Action<List<Net.MapTrailPoint>, string, double, bool> StrokeAdded;

        /// <summary>Протянутая стена или дверь (координаты в системе картинки).</summary>
        public event Action<double, double, double, double, string> WallAdded;

        /// <summary>Пункт меню жетона: размер, обзор, поворот, рамка, удаление.</summary>
        public event Action<int, string, string> TokenMenuAction;

        /// <summary>Жетон выбрали на карте — от него зависит ползунок «Обзор».</summary>
        public event Action<int> TokenSelected;

        /// <summary>Начали новый маршрут — старый след этого жетона нужно стереть.</summary>
        public event Action<int> TrailCleared;

        /// <summary>Действие с фигурой: поворот, размер, цвет, удаление.</summary>
        public event Action<int, string, string> ShapeAction;

        /// <summary>Поворот взгляда из WebView: абсолютный угол и признак «отпустили».</summary>
        public event Action<int, double, bool> FacingChanged;

        /// <summary>Действие со стеной: открыть/закрыть дверь, убрать.</summary>
        public event Action<int, string> WallAction;

        /// <summary>Страница просит картинку карты заново (после перезагрузки WebView).</summary>
        public event Action ImageNeeded;

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
                _core.Navigate("https://hmwt.map/index.html?v=" + DateTime.Now.Ticks);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MapWeb] init: " + ex.Message);
                _web.Visibility = Visibility.Collapsed;
                IsHitTestVisible = false;
                Failed?.Invoke();
            }
        }

        /// <summary>
        /// Разовое сообщение (пинг, вспышка), которое не является состоянием:
        /// если WebView ещё не готов, оно просто теряется и не подменяет
        /// ожидающее состояние сцены.
        /// </summary>
        public void SendEphemeral(string json)
        {
            if (string.IsNullOrEmpty(json) || !IsReady || _core == null) return;
            try { _core.PostWebMessageAsJson(json); }
            catch (Exception ex) { Debug.WriteLine("[MapWeb] ephemeral: " + ex.Message); }
        }

        public void SendState(string json)
        {
            if (string.IsNullOrEmpty(json)) return;
            _pending = json;
            if (!IsReady || _core == null) return;

            try
            {
                _core.PostWebMessageAsJson(json);
                // Журнал не должен расти на каждом кадре: карта шлёт состояние
                // несколько раз в секунду. Пишем редкие отметки для отладки.
                _stateMessages++;
                if (_stateMessages % 50 == 0)
                    WriteLog("state #" + _stateMessages + " bytes=" + json.Length);
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
                    WriteLog("ready");
                    Ready?.Invoke();
                    return;
                }

                if (type == "jsLog" || type == "jsError")
                {
                    string text = root.TryGetProperty("text", out var logEl) ? logEl.GetString() : "";
                    WriteLog((type == "jsError" ? "JS ERROR " : "js ") + text);
                    return;
                }

                if (type == "needImage")
                {
                    ImageNeeded?.Invoke();
                    return;
                }

                if (type == "placeShape" &&
                    root.TryGetProperty("x", out var sx) &&
                    root.TryGetProperty("y", out var sy))
                {
                    ShapePlaced?.Invoke(sx.GetDouble(), sy.GetDouble());
                    return;
                }

                // Точка интереса по правой кнопке на карте
                if (type == "placePoint" &&
                    root.TryGetProperty("x", out var px) &&
                    root.TryGetProperty("y", out var py))
                {
                    PointPlaced?.Invoke(px.GetDouble(), py.GetDouble());
                    return;
                }

                // Перенос группы жетонов
                if (type == "moveTokens" &&
                    root.TryGetProperty("moves", out var moves) &&
                    moves.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var list = new List<(int, double, double)>();
                    foreach (var m in moves.EnumerateArray())
                    {
                        if (!m.TryGetProperty("id", out var mid)) continue;
                        if (!m.TryGetProperty("x", out var mx)) continue;
                        if (!m.TryGetProperty("y", out var my)) continue;
                        list.Add((mid.GetInt32(), mx.GetDouble(), my.GetDouble()));
                    }
                    if (list.Count > 0) TokensMoved?.Invoke(list);
                    return;
                }

                if (type == "ping" &&
                    root.TryGetProperty("x", out var pingX) &&
                    root.TryGetProperty("y", out var pingY))
                {
                    Pinged?.Invoke(pingX.GetDouble(), pingY.GetDouble());
                    return;
                }

                if (type == "moveTokenLive" &&
                    root.TryGetProperty("id", out var liveId) &&
                    root.TryGetProperty("x", out var liveX) &&
                    root.TryGetProperty("y", out var liveY))
                {
                    TokenMovedLive?.Invoke(liveId.GetInt32(), liveX.GetDouble(), liveY.GetDouble());
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
                    var trail = ReadPoints(root, "points");
                    TokenMoved?.Invoke(id, x, y);
                    TokenMovedWithTrail?.Invoke(id, x, y, trail);
                    return;
                }

                if (type == "moveShape" &&
                    root.TryGetProperty("id", out var shapeId) &&
                    root.TryGetProperty("x", out var shapeX) &&
                    root.TryGetProperty("y", out var shapeY))
                {
                    bool commit = root.TryGetProperty("commit", out var commitEl) &&
                                  commitEl.ValueKind == JsonValueKind.True;
                    ShapeMoved?.Invoke(shapeId.GetInt32(), shapeX.GetDouble(), shapeY.GetDouble(), commit);
                    return;
                }

                if (type == "stroke")
                {
                    var points = ReadPoints(root, "points");
                    string color = root.TryGetProperty("color", out var colorEl) ? colorEl.GetString() : "";
                    double thickness = root.TryGetProperty("thickness", out var thEl) ? thEl.GetDouble() : 4;
                    bool eraser = root.TryGetProperty("eraser", out var erEl) && erEl.ValueKind == JsonValueKind.True;
                    if (points.Count > 0) StrokeAdded?.Invoke(points, color ?? "", thickness, eraser);
                    return;
                }

                if (type == "wall" &&
                    root.TryGetProperty("x1", out var w1x) &&
                    root.TryGetProperty("y1", out var w1y) &&
                    root.TryGetProperty("x2", out var w2x) &&
                    root.TryGetProperty("y2", out var w2y))
                {
                    string kind = root.TryGetProperty("kind", out var kindEl) ? kindEl.GetString() : "wall";
                    WallAdded?.Invoke(w1x.GetDouble(), w1y.GetDouble(),
                        w2x.GetDouble(), w2y.GetDouble(), kind ?? "wall");
                    return;
                }

                if (type == "tokenMenuAction" &&
                    root.TryGetProperty("id", out var menuId) &&
                    root.TryGetProperty("action", out var actionEl))
                {
                    string action = actionEl.GetString() ?? "";
                    string value = root.TryGetProperty("value", out var valueEl) ? valueEl.GetString() : "";
                    TokenMenuAction?.Invoke(menuId.GetInt32(), action, value ?? "");
                    return;
                }

                if (type == "selectToken" && root.TryGetProperty("id", out var selectId))
                {
                    TokenSelected?.Invoke(selectId.GetInt32());
                    return;
                }

                if (type == "trailClear" && root.TryGetProperty("id", out var trailId))
                {
                    TrailCleared?.Invoke(trailId.GetInt32());
                    return;
                }

                if (type == "shapeAction" &&
                    root.TryGetProperty("id", out var shapeActionId) &&
                    root.TryGetProperty("action", out var shapeActionEl))
                {
                    string value = root.TryGetProperty("value", out var shapeValueEl)
                        ? shapeValueEl.GetString() : "";
                    ShapeAction?.Invoke(shapeActionId.GetInt32(),
                        shapeActionEl.GetString() ?? "", value ?? "");
                    return;
                }

                if (type == "rotate" &&
                    root.TryGetProperty("id", out var rotateId) &&
                    root.TryGetProperty("facing", out var facingEl))
                {
                    bool commit = root.TryGetProperty("commit", out var commitEl) &&
                                  commitEl.ValueKind == JsonValueKind.True;
                    FacingChanged?.Invoke(rotateId.GetInt32(), facingEl.GetDouble(), commit);
                    return;
                }

                if (type == "wallAction" &&
                    root.TryGetProperty("id", out var wallId) &&
                    root.TryGetProperty("action", out var wallActionEl))
                {
                    WallAction?.Invoke(wallId.GetInt32(), wallActionEl.GetString() ?? "");
                    return;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MapWeb] message: " + ex.Message);
                WriteLog("message error: " + ex.Message);
            }
        }

        private static List<Net.MapTrailPoint> ReadPoints(JsonElement root, string name)
        {
            var list = new List<Net.MapTrailPoint>();
            if (!root.TryGetProperty(name, out var pts) || pts.ValueKind != JsonValueKind.Array) return list;

            foreach (var p in pts.EnumerateArray())
            {
                if (p.TryGetProperty("x", out var px) && p.TryGetProperty("y", out var py))
                    list.Add(new Net.MapTrailPoint { X = px.GetDouble(), Y = py.GetDouble() });
            }
            return list;
        }

        /// <summary>Журнал моста WebView → C#: без него ошибки JS видны только в отладчике.</summary>
        internal static void WriteLog(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "hmwt-map-web.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
            }
            catch { }
        }
    }
}
