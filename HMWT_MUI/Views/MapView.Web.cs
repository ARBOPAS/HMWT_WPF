using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Мост карты в WebGL: снимок сцены, обзор, туман и «разведанное».
    /// Вынесено из MapView.cs (partial), чтобы протокол карты не жил в одном
    /// файле с WPF-разметкой и обработчиками мыши.
    /// </summary>
    public sealed partial class MapView
    {
        // ══════════════════════ ДАННЫЕ ══════════════════════

        private void PushWebMap()
        {
            if (!_webMapReady || _scene == null) return;

            try
            {
                string imagePath = string.IsNullOrWhiteSpace(_scene.ImageHash)
                    ? null
                    : (_remote ? RemoteImagePath(_scene.ImageHash) : MapService.ResolveImagePath(_scene));
                string imageHash = _scene.ImageHash ?? "";
                bool sendImage = !string.Equals(imageHash, _webImageHash, StringComparison.OrdinalIgnoreCase);
                string image = sendImage ? WebAssetUrl(imagePath) : null;
                bool includeSeen = _webSeenVersion != _seenVersion;

                var tokens = new List<object>();
                foreach (var t in _scene.Tokens)
                {
                    string photoPath = ResolveTokenPhoto(t);
                    string photoKey = photoPath ?? "";
                    bool sendPhoto = !_webTokenPhotoKeys.TryGetValue(t.Id, out var oldPhoto) ||
                                     !string.Equals(oldPhoto, photoKey, StringComparison.OrdinalIgnoreCase);
                    if (sendPhoto) _webTokenPhotoKeys[t.Id] = photoKey;

                    // Хиты и состояния берём у участника боя: на жетоне они
                    // показываются сверху и снизу от имени.
                    int hp = 0, hpMax = 0;
                    string conditions = "";
                    if (t.CombatantId > 0 && _combatants.TryGetValue(t.CombatantId, out var owner) &&
                        owner.Combatant != null)
                    {
                        hp = owner.Combatant.HitPoints;
                        hpMax = owner.Combatant.MaxHitPoints;
                        conditions = owner.Combatant.Conditions ?? "";
                    }
                    else
                    {
                        // клиент: базы боёв нет, значения пришли с жетоном
                        hp = t.Hp;
                        hpMax = t.HpMax;
                        conditions = t.Conditions ?? "";
                    }

                    tokens.Add(new
                    {
                        id = t.Id,
                        label = t.Label ?? "",
                        x = t.X,
                        y = t.Y,
                        sizeCells = t.SizeCells,
                        color = t.Color ?? "",
                        isPlayer = t.IsPlayer,
                        hidden = t.Hidden,
                        selected = t.Id == _selectedTokenId,
                        canMove = CanMoveToken(t),
                        // свой жетон виден всегда, даже с выключенным обзором
                        mine = !_isDm && CanMoveToken(t),
                        frameColor = t.FrameColor ?? "",
                        viewColor = t.ViewColor ?? "",
                        revealCells = t.RevealCells,
                        // Меню ПКМ ставит галочку на текущем угле обзора:
                        // без этого поля любая сборка показывала «круговой 360».
                        viewAngle = t.ViewAngle,
                        hp,
                        hpMax,
                        conditions,
                        // null — аватар не менялся; "" — аватара нет.
                        photo = sendPhoto ? WebAssetUrl(photoPath) : null
                    });
                }

                var payload = new
                {
                    type = "scene",
                    sceneId = _scene.Id,
                    width = _scene.WidthPx > 0 ? _scene.WidthPx : 1,
                    height = _scene.HeightPx > 0 ? _scene.HeightPx : 1,
                    imageHash,
                    image,
                    isDm = _isDm,
                    // «Токены отряда»: показывать жетоны игроков даже вне обзора
                    // Справочник состояний: ключ + русское название. Ключи те же,
                    // что в трекере инициативы («charmed»), поэтому состояние —
                    // один объект на всё приложение, а не три разных списка.
                    conditionList = Services.CharacterStateService.LoadConditions(0)
                        .Select(c => new { key = c.Key, name = c.Name }).ToList(),
                    showParty = _showPlayersCheck.IsChecked == true,
                    rotation = _scene.Rotation,
                    flipH = _scene.FlipH,
                    flipV = _scene.FlipV,
                    grid = new
                    {
                        on = _scene.GridOn,
                        px = _scene.GridPx,
                        offsetX = _scene.GridOffsetX,
                        offsetY = _scene.GridOffsetY,
                        thickness = _scene.GridThickness,
                        color = _scene.GridColor,
                        snap = _scene.SnapToGrid
                    },
                    fog = new
                    {
                        on = WebFogEnabled(),
                        seen = includeSeen ? _seenPoints.ToList() : null
                    },
                    trailColor = string.IsNullOrWhiteSpace(_scene.TrailColor)
                        ? "#FF9B8BFF" : _scene.TrailColor,
                    fovTokens = WebFovTokens(),
                    tool = new
                    {
                        shape = _pendingShape == null ? null : _pendingShape.Kind,
                        shapeRadius = _pendingShape == null ? 0 : _pendingShape.RadiusCells,
                        ping = _pingCheck.IsChecked == true,
                        measure = _measureCheck.IsChecked == true,
                        brush = _drawCheck.IsChecked == true,
                        eraser = _eraserCheck.IsChecked == true,
                        wall = _wallCheck.IsChecked == true,
                        brushColor = _brushColor,
                        brushThickness = BrushThickness()
                    },
                    strokes = _scene.Strokes.Select(s => new
                    {
                        id = s.Id,
                        color = s.Color,
                        thickness = s.Thickness,
                        points = s.Points
                    }).ToList(),
                    trails = _trails.Select(pair => new
                    {
                        id = pair.Key,
                        points = pair.Value.Select(p => new { x = p.X, y = p.Y }).ToList()
                    }).ToList(),
                    tokens,
                    // Чей сейчас ход: жетон подсвечивается на карте.
                    turnTokenId = CurrentTurnTokenId(),
                    // Тёмную зону игрокам не отправляем вовсе: это инструмент
                    // мастера, и в чужом клиенте ей делать нечего.
                    shapes = _scene.Shapes
                        .Where(s => _isDm || !(s.Kind ?? "").StartsWith("darkzone", StringComparison.OrdinalIgnoreCase))
                        .Select(s => new
                    {
                        id = s.Id,
                        kind = s.Kind,
                        x = s.X,
                        y = s.Y,
                        radius = s.RadiusCells,
                        angle = s.Angle,
                        color = s.Color,
                        label = s.Label ?? "",
                        // Скрипт тёмной зоны: подпись для меню ПКМ.
                        script = s.ScriptKind ?? "",
                        scriptLabel = ZoneScriptLabel(s)
                    }).ToList(),
                    walls = _scene.Walls.Select(w => new
                    {
                        id = w.Id,
                        x1 = w.X1,
                        y1 = w.Y1,
                        x2 = w.X2,
                        y2 = w.Y2,
                        kind = w.Kind,
                        isOpen = w.IsOpen
                    }).ToList()
                };

                string json = System.Text.Json.JsonSerializer.Serialize(payload,
                    new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
                    });

                _webMap.SendState(json);
                if (includeSeen)
                {
                    _webSeenVersion = _seenVersion;
                    _webSeenSent = _seenPoints.Count;
                }
                // Хэш запоминаем только когда картинка реально ушла. Иначе
                // «файл ещё качается» навсегда помечал бы карту отправленной,
                // и WebM/WebP не доезжали до WebGL после загрузки.
                if (sendImage && !string.IsNullOrWhiteSpace(imagePath)) _webImageHash = imageHash;
                else if (sendImage && string.IsNullOrWhiteSpace(imageHash)) _webImageHash = "";
            }
            catch (Exception ex)
            {
                WebLog("state error: " + ex);
                Debug.WriteLine("[MapWeb] state: " + ex.Message);
            }
        }

        private bool WebFogEnabled()
        {
#if DEBUG
            if (Environment.GetEnvironmentVariable("HMWT_DEBUG_NOFOG") == "1") return false;
#endif
            return _scene != null && _scene.FogOn;
        }

        private static void WebLog(string text)
        {
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "hmwt-map-web.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + text + Environment.NewLine);
            }
            catch { }
        }

        private static readonly System.Text.Json.JsonSerializerOptions WebJsonOptions =
            new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

        /// <summary>Жетон текущего участника боя (0 — нет боя).</summary>
        private int CurrentTurnTokenId()
        {
            try
            {
                int currentId = 0;
                int currentCharId = 0;
                string currentName = "";

                if (RoomSession.Instance.IsActive)
                {
                    var state = RoomSession.Instance.GetInitiative();
                    var current = state?.Combatants?.FirstOrDefault(c => c != null && c.IsCurrent);
                    if (current != null)
                    {
                        currentId = current.Id;
                        currentCharId = current.CharacterId;
                        currentName = current.Name ?? "";
                    }
                }
                else
                {
                    // В локальной игре (без комнаты) GetInitiative пуст —
                    // раньше из-за этого подсветка хода не работала вообще.
                    var encounter = Services.InitiativeService.ActiveEncounter();
                    var current = encounter?.Combatants?
                        .FirstOrDefault(c => c != null && c.IsCurrent);
                    if (current != null)
                    {
                        currentId = current.Id;
                        currentCharId = current.CharacterId;
                        currentName = current.Name ?? "";
                    }
                }

                if (currentId <= 0) return 0;

                var token = _scene?.Tokens?.FirstOrDefault(t =>
                    t.CombatantId == currentId ||
                    (currentCharId > 0 && t.OwnerCharacterId == currentCharId) ||
                    (t.CombatantId == 0 && !string.IsNullOrWhiteSpace(currentName) &&
                     string.Equals((t.Label ?? "").Trim(), currentName.Trim(),
                         StringComparison.OrdinalIgnoreCase)));

                return token?.Id ?? 0;
            }
            catch { return 0; }
        }

        private List<object> WebFovTokens()
        {
            var list = new List<object>();
            if (_scene?.Tokens == null) return list;

            // «Токены отряда» управляет и обзором: выключили — виден только
            // свой конус, включили — общий обзор отряда. У мастера всегда общий.
            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase) &&
                            (_isDm || _showPlayersCheck.IsChecked == true);

            foreach (var t in _scene.Tokens)
            {
                if (t == null || t.Hidden || t.RevealCells <= 0) continue;
                list.Add(new
                {
                    id = t.Id,
                    x = t.X,
                    y = t.Y,
                    facing = t.Facing,
                    viewAngle = t.ViewAngle,
                    revealCells = t.RevealCells,
                    isPlayer = t.IsPlayer,
                    // свой цвет сектора обзора
                    viewColor = t.ViewColor ?? "",
                    // Развеивает ли этот жетон туман именно для смотрящего:
                    // у игрока — отряд или только свой, у мастера — отряд.
                    reveals = RevealsFog(t, combined),
                    // «Свой» жетон: у мастера своих нет (он видит всё, но
                    // приглушает скрытое), у игрока — только его персонаж.
                    mine = !_isDm && CanMoveToken(t)
                });
            }

            return list;
        }

        /// <summary>
        /// Лёгкое обновление тумана: только «разведанное» и текущий обзор.
        /// Полную сцену на каждом шаге перетаскивания слать нельзя — там следы,
        /// жетоны и портреты, а нужно лишь новое вырезанное пятно.
        /// </summary>
        private void PushWebFog()
        {
            if (!_webMapReady || _scene == null) return;

            try
            {
                var fov = WebFovTokens();
                var payload = new
                {
                    type = "fog",
                    sceneId = _scene.Id,
                    isDm = _isDm,
                    grid = new { px = _scene.GridPx },
                    fog = new { on = WebFogEnabled(), seen = _seenPoints.ToList() },
                    fovTokens = fov
                };

                _webMap.SendState(System.Text.Json.JsonSerializer.Serialize(payload, WebJsonOptions));
                _webSeenVersion = _seenVersion;
                _webSeenSent = _seenPoints.Count;
            }
            catch (Exception ex)
            {
                WebLog("fog state error: " + ex.Message);
            }
        }

        /// <summary>
        /// Досылает только новые разведанные сектора (обычно 1–3 полигона).
        /// Полный список на каждом шаге поворота или драга — это десятки
        /// килобайт и заметная нагрузка; а без досылки туман «развеивался»
        /// только после отпускания мыши.
        /// </summary>
        private void PushWebSeen()
        {
            if (!_webMapReady || _scene == null) return;

            // Очистка «разведанного» могла уменьшить список — тогда шлём заново
            if (_webSeenSent > _seenPoints.Count) _webSeenSent = 0;
            if (_webSeenSent == _seenPoints.Count) return;

            try
            {
                var fresh = _seenPoints.Skip(_webSeenSent).ToList();
                _webSeenSent = _seenPoints.Count;

                var payload = new
                {
                    type = "seenAdd",
                    sceneId = _scene.Id,
                    seen = fresh
                };

                _webMap.SendState(System.Text.Json.JsonSerializer.Serialize(payload, WebJsonOptions));
            }
            catch (Exception ex)
            {
                WebLog("seen add error: " + ex.Message);
            }
        }

        /// <summary>
        /// Только угол обзора — без «разведанного». Такой пакет маленький и
        /// уходит до 30 раз в секунду: поворот рисуется плавно, а тяжёлый
        /// список разведанных секторов не гоняется по мосту на каждый кадр.
        /// </summary>
        private void PushWebFacing(bool stop = false)
        {
            if (!_webMapReady || _scene == null) return;

            try
            {
                var payload = new
                {
                    type = "facing",
                    sceneId = _scene.Id,
                    isDm = _isDm,
                    grid = new { px = _scene.GridPx },
                    fog = new { on = WebFogEnabled() },
                    stop = stop,
                    fovTokens = WebFovTokens()
                };

                _webMap.SendState(System.Text.Json.JsonSerializer.Serialize(payload, WebJsonOptions));
            }
            catch (Exception ex)
            {
                WebLog("facing state error: " + ex.Message);
            }
        }

        /// <summary>После перезагрузки WebView всё, что «уже отправлено», нужно слать заново.</summary>
        private void ResetWebCache()
        {
            _webImageHash = "";
            _webTokenPhotoKeys.Clear();
            _webDataUrls.Clear();
            _webSeenVersion = -1;
            _webSeenSent = 0;
        }

        /// <summary>Полотно WebView переехало или изменило размер — пишем его экранные координаты.</summary>
        private void OnWebMapSizeChanged(object sender, SizeChangedEventArgs e)
        {
            try
            {
                Point origin = _webMap.PointToScreen(new Point(0, 0));
                WebLog($"web origin {origin.X:0},{origin.Y:0} " +
                       $"size {_webMap.ActualWidth:0}x{_webMap.ActualHeight:0}");
            }
            catch { }
        }

        private void OnWebImageNeeded()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnWebImageNeeded));
                return;
            }

            ResetWebCache();
            PushWebMap();
        }
    }
}
