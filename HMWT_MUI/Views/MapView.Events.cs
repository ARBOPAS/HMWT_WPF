using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HMWT_MUI.Controls;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Обработчики WebGL-карты: события жетонов, фигур, стен, кисти и меню.
    /// Вынесено из MapView.cs (partial), чтобы ввод/меню не жили в одном
    /// файле со сценами и загрузкой карты.
    /// </summary>
    public sealed partial class MapView
    {
        private void OnWebTokenMovedLive(int tokenId, double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenMovedLive(tokenId, x, y)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null || !CanMoveToken(token)) return;

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                x = Math.Round((x - cell / 2) / cell) * cell + cell / 2;
                y = Math.Round((y - cell / 2) / cell) * cell + cell / 2;
            }

            token.X = Math.Max(0, Math.Min(_scene.WidthPx, x));
            token.Y = Math.Max(0, Math.Min(_scene.HeightPx, y));

            // Отправку делаем ПЕРВОЙ и в try: след и туман — украшение, а
            // из-за исключения в них живой ход раньше мог не уйти вовсе.
            try
            {
                if ((DateTime.UtcNow - _lastLiveMoveSent).TotalMilliseconds >= 50)
                {
                    _lastLiveMoveSent = DateTime.UtcNow;
                    if (_remote)
                    {
                        RoomSession.Instance.SendMapMoveLive(token.Id, token.X, token.Y, token.Facing);
                        if ((DateTime.UtcNow - _lastLiveMoveLog).TotalSeconds >= 1)
                        {
                            _lastLiveMoveLog = DateTime.UtcNow;
                            WebLog($"live send token={token.Id} -> host");
                        }
                    }
                    else if (_inRoom)
                    {
                        RoomSession.Instance.PublishLiveTokenMove(token.Id, token.X, token.Y, token.Facing);
                    }
                }
            }
            catch (Exception ex) { WebLog("live send error: " + ex.Message); }

            try { AppendTrail(token, new Point(token.X, token.Y)); }
            catch (Exception ex) { Debug.WriteLine($"[MapView] след: {ex.Message}"); }

            // Разведка «на ходу»: новый сектор появляется сразу, а не после
            // отпускания кнопки. В базу и сеть пишем не чаще 3 раз в секунду.
            if ((DateTime.UtcNow - _lastLiveFog).TotalMilliseconds >= 320)
            {
                _lastLiveFog = DateTime.UtcNow;
                if (WebFogEnabled())
                {
                    // только в память: в базу и сеть уйдёт одним пакетом
                    // при отпускании, иначе каждый шаг драга тормозит UI
                    RememberCurrentVision(false);
                    PushWebSeen();
                }
            }

            if (!_webMapReady) { BuildFogMask(); RenderFov(); }
        }

        private void OnWebShapeMoved(int shapeId, double x, double y, bool commit)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebShapeMoved(shapeId, x, y, commit)));
                return;
            }

            if (_scene == null || (!_isDm && !_remote)) return;
            var shape = _scene.Shapes.FirstOrDefault(s => s.Id == shapeId);
            if (shape == null) return;

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                x = Math.Round((x - cell / 2) / cell) * cell + cell / 2;
                y = Math.Round((y - cell / 2) / cell) * cell + cell / 2;
            }

            shape.X = x;
            shape.Y = y;
            RenderShapes();
            if (!commit)
            {
                // Фигуру ведут: остальным уходят только координаты, чтобы она
                // ехала плавно, а не прыгала при отпускании.
                SendShapeLive(shape);
                return;
            }

            CommitShape(shape, "update");
            PushWebMap();
        }

        /// <summary>Шлёт текущее положение фигуры (не чаще ~25 раз в секунду).</summary>
        private void SendShapeLive(MapShapeData shape)
        {
            if (shape == null || shape.Id <= 0) return;
            if ((DateTime.UtcNow - _lastShapeLiveSent).TotalMilliseconds < 35) return;
            _lastShapeLiveSent = DateTime.UtcNow;

            try
            {
                if (_remote) RoomSession.Instance.SendShapeMoveLive(shape.Id, shape.X, shape.Y);
                else if (_inRoom) RoomSession.Instance.PublishShapeMoveLive(shape.Id, shape.X, shape.Y);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] ход фигуры: {ex.Message}");
            }
        }

        /// <summary>Другой участник ведёт фигуру — показываем сразу и плавно.</summary>
        private void OnRemoteShapeMoved(int shapeId, double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnRemoteShapeMoved(shapeId, x, y)));
                return;
            }

            if (_scene == null) return;
            var shape = _scene.Shapes.FirstOrDefault(s => s.Id == shapeId);
            if (shape == null) return;

            shape.X = x;
            shape.Y = y;

            if (_webMapReady)
            {
                try
                {
                    _webMap.SendEphemeral(System.Text.Json.JsonSerializer.Serialize(
                        new { type = "shapeLive", id = shapeId, x, y }, WebJsonOptions));
                }
                catch { }
            }
            else
            {
                RenderShapes();
            }
        }

        private void OnWebStroke(List<MapTrailPoint> points, string color, double thickness, bool eraser)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebStroke(points, color, thickness, eraser)));
                return;
            }

            if (_scene == null || points == null || points.Count == 0) return;

            var path = points.Where(p => p != null).Select(p => new Point(p.X, p.Y)).ToList();
            if (path.Count == 0) return;

            if (eraser)
            {
                // Ластик прощает промах: радиус вдвое больше кисти, иначе на
                // карте 5000 px линию толщиной 16 почти невозможно задеть.
                EraseAt(path, Math.Max(12, thickness * 2));
                PushWebMap();
                return;
            }

            var stroke = new MapStrokeData
            {
                SceneId = _scene.Id,
                Color = string.IsNullOrWhiteSpace(color) ? _brushColor : color.Trim(),
                Thickness = Math.Max(1, Math.Min(80, thickness <= 0 ? BrushThickness() : thickness)),
                Points = PointsToString(path)
            };

            if (_remote)
            {
                // У игрока нет базы ведущего: рисуем у себя и просим сохранить.
                stroke.Id = -Math.Abs(Guid.NewGuid().GetHashCode());
                _scene.Strokes.Add(stroke);
                RoomSession.Instance.SendMapStroke(stroke);
            }
            else if (MapService.SaveStroke(stroke) > 0)
            {
                _scene.Strokes.Add(stroke);
                PublishMap();
                var saved = stroke;
                PushUndo(() =>
                {
                    MapService.DeleteStroke(saved.Id);
                    _scene?.Strokes.RemoveAll(s => s.Id == saved.Id);
                    PublishMap();
                    PushWebMap();
                });
            }

            RenderStrokes();
            PushWebMap();
        }

        private void OnWebWall(double x1, double y1, double x2, double y2, string kind)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebWall(x1, y1, x2, y2, kind)));
                return;
            }

            if (_scene == null || !_isDm) return;

            Point start = SnapWallPoint(new Point(x1, y1));
            Point end = SnapWallPoint(new Point(x2, y2));

            // Стена за пределами картинки бесполезна: обзор и туман считаются
            // в её координатах, поэтому прижимаем концы к полю карты.
            double maxX = Math.Max(1, _scene.WidthPx);
            double maxY = Math.Max(1, _scene.HeightPx);
            start = new Point(Math.Max(0, Math.Min(maxX, start.X)),
                              Math.Max(0, Math.Min(maxY, start.Y)));
            end = new Point(Math.Max(0, Math.Min(maxX, end.X)),
                            Math.Max(0, Math.Min(maxY, end.Y)));

            if (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) < 8) return;

            var wall = new MapWallData
            {
                SceneId = _scene.Id,
                X1 = start.X,
                Y1 = start.Y,
                X2 = end.X,
                Y2 = end.Y,
                Kind = string.Equals(kind, "door", StringComparison.OrdinalIgnoreCase) ? "door" : "wall"
            };

            if (MapService.SaveWall(wall) > 0)
            {
                _scene.Walls.Add(wall);
                var saved = wall;
                PushUndo(() =>
                {
                    MapService.DeleteWall(saved.Id);
                    _scene?.Walls.RemoveAll(w => w.Id == saved.Id);
                    PublishMap();
                    PushWebMap();
                });
                PublishMap();
                RenderWalls();
                BuildFogMask();
                PushWebMap();
            }
        }

        private void OnWebWallAction(int wallId, string action)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebWallAction(wallId, action)));
                return;
            }

            if (_scene == null || !_isDm) return;
            var wall = _scene.Walls.FirstOrDefault(w => w.Id == wallId);
            if (wall == null) return;
            WebLog($"wall action {wallId} {action}");

            if (string.Equals(action, "remove", StringComparison.OrdinalIgnoreCase))
            {
                MapService.DeleteWall(wall.Id);
                _scene.Walls.RemoveAll(w => w.Id == wall.Id);
            }
            else
            {
                wall.IsOpen = !wall.IsOpen;
                MapService.UpdateWall(wall);
            }

            PublishMap();
            RenderWalls();
            BuildFogMask();
            PushWebMap();
        }

        /// <summary>Меню жетона в WebView: размер, обзор, поворот, рамка, удаление.</summary>
        private void OnWebTokenMenuAction(int tokenId, string action, string value)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenMenuAction(tokenId, action, value)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null) return;

            // Игроку доступен только свой жетон, и то без ведущих действий.
            bool mine = CanMoveToken(token);
            WebLog($"token menu {tokenId} {action} '{value}' dm={_isDm} mine={mine}");
            if (!_isDm && !mine) return;

            switch ((action ?? "").ToLowerInvariant())
            {
                case "size":
                    if (!int.TryParse(value, out int cells)) return;
                    // До 10 клеток: огромные существа в D&D занимают и больше,
                    // ограничение в 4 клетки обрезало их «на глаз».
                    cells = Math.Max(1, Math.Min(10, cells));
                    if (_remote) SetRemoteTokenSize(token, cells);
                    else SetTokenSize(token, cells);
                    break;

                case "viewangle":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double angle)) return;
                    angle = Math.Max(5, Math.Min(360, angle));
                    if (_isDm) SetViewAngle(token, angle);
                    else SetRemoteViewAngle(token, angle);
                    break;

                case "facing":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double delta)) return;
                    // В WebGL угол доводится плавно, а не прыжком на 45°.
                    // В модели угол фиксируем сразу: иначе ближайший снимок
                    // карты приходит со старым углом и «отматывает» конус.
                    if (_webMapReady)
                    {
                        token.Facing = NormalizeDeg(token.Facing + delta);
                        if (_remote)
                        {
                            RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, token.Facing);
                        }
                        else
                        {
                            MapService.SaveToken(token);
                            PublishMap();
                        }

                        try
                        {
                            _webMap.SendEphemeral(System.Text.Json.JsonSerializer.Serialize(
                                new { type = "rotateBy", id = token.Id, deg = delta }, WebJsonOptions));
                        }
                        catch { }
                    }
                    else if (_isDm) RotateFacing(token, delta);
                    else RotateFacingRemote(token, delta);
                    break;

                case "frame":
                    if (!_isDm) return;
                    SetTokenFrame(token, value);
                    break;

                // Цвет сектора обзора: у каждого игрока свой, иначе все синие
                case "viewcolor":
                    SetTokenViewColor(token, value);
                    break;

                // Состояние из справочника (галочка в меню): включаем/выключаем.
                // Ведущий — любому жетону, игрок — только своему (проверка ниже).
                // Состояния: подменю в меню ПКМ (WebGL). Один ключ за клик,
                // можно отметить несколько подряд, не закрывая меню.
                case "condition":
                    {
                        if (!_isDm && !CanMoveToken(token)) return;

                        string key = (value ?? "").Trim();
                        if (key.Length == 0) return;

                        var keys = ParseTokenConditions(token.Conditions);
                        int removed = keys.RemoveAll(k =>
                            string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                        if (removed == 0) keys.Add(key);

                        ApplyTokenConditions(token, string.Join(", ", keys));
                    }
                    break;

                case "conditionsclear":
                    {
                        if (!_isDm && !CanMoveToken(token)) return;
                        ApplyTokenConditions(token, "");
                    }
                    break;

                // Старый путь — окно с галочками. Меню WebGL теперь показывает
                // подменю и сюда не обращается; оставлено для совместимости.
                case "conditions":
                    {
                        if (!_isDm && !CanMoveToken(token)) return;

                        CombatantData condCombatant = null;
                        EncounterData condEncounter = null;
                        string current = token.Conditions ?? "";
                        if (!_remote && token.CombatantId > 0 &&
                            _combatants.TryGetValue(token.CombatantId, out var cond) &&
                            cond.Combatant != null)
                        {
                            condCombatant = cond.Combatant;
                            condEncounter = cond.Encounter;
                            current = condCombatant.Conditions ?? "";
                        }

                        var picked = Windows.ConditionsWindow.Pick(Window.GetWindow(this),
                            "Состояния: " + (token.Label ?? ""),
                            current.Split(new[] { ',', ';' },
                                StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                        if (picked == null) return;

                        ApplyTokenConditions(token, string.Join(", ", picked));
                    }
                    break;

                case "reveal":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double reveal)) return;
                    // 0 — обзор выключен совсем (у каждого игрока свой)
                    reveal = Math.Max(0, Math.Min(40, reveal));
                    if (_isDm)
                    {
                        SetRevealCells(token, reveal);
                    }
                    else
                    {
                        // Игрок меняет дальность своего обзора — хост применит и разошлёт
                        token.RevealCells = reveal;
                        RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y,
                            double.NaN, -1, -1, reveal);
                        RenderFov();
                        RememberCurrentVision();
                        BuildFogMask();
                    }
                    break;

                case "remove":
                    if (!_isDm) return;
                    MapService.DeleteToken(token.Id);
                    _scene.Tokens.RemoveAll(t => t.Id == token.Id);
                    PublishMap();
                    RebuildTokens();
                    break;

                default:
                    return;
            }

            PushWebMap();
        }

        private static string ZoneScriptLabel(MapShapeData shape)
        {
            if (shape == null || string.IsNullOrWhiteSpace(shape.ScriptKind)) return "";

            string ability = (shape.ScriptAbility ?? "").ToUpperInvariant();
            string shortName = ability switch
            {
                "STR" => "СИЛ", "DEX" => "ЛОВ", "CON" => "ТЕЛ",
                "INT" => "ИНТ", "WIS" => "МДР", "CHA" => "ХАР",
                _ => "ЛОВ"
            };

            string what;
            if (string.Equals(shape.ScriptKind, "save", StringComparison.OrdinalIgnoreCase))
            {
                what = "спас " + shortName;
            }
            else if (!string.IsNullOrWhiteSpace(shape.ScriptSkill))
            {
                var skill = Services.SkillCatalog.All.FirstOrDefault(k =>
                    string.Equals(k.Key, shape.ScriptSkill, StringComparison.OrdinalIgnoreCase));
                what = "проверка " + (string.IsNullOrWhiteSpace(skill?.Russian) ? shape.ScriptSkill : skill.Russian);
            }
            else
            {
                what = "проверка " + shortName;
            }

            if (shape.ScriptDC > 0) what += " · DC " + shape.ScriptDC;
            if (!string.IsNullOrWhiteSpace(shape.ScriptName)) what = shape.ScriptName.Trim() + " · " + what;
            return what;
        }

        private static List<string> ParseTokenConditions(string text)
        {
            var list = new List<string>();
            foreach (string part in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string key = part.Trim();
                if (key.Length == 0) continue;
                if (!list.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
                    list.Add(key);
            }
            return list;
        }

        /// <summary>
        /// Пишет набор состояний жетону и связанному участнику боя. Ведущий —
        /// в базу и в комнату, игрок — хостy сообщением хода: у клиента своей
        /// базы боёв нет, и состояния на карте/в трекере/листе должны совпасть.
        /// </summary>
        private void ApplyTokenConditions(MapTokenData token, string keys)
        {
            if (token == null) return;

            CombatantData combatant = null;
            EncounterData encounter = null;
            if (!_remote && token.CombatantId > 0 &&
                _combatants.TryGetValue(token.CombatantId, out var linked) &&
                linked.Combatant != null)
            {
                combatant = linked.Combatant;
                encounter = linked.Encounter;
                combatant.Conditions = keys;
                Services.InitiativeService.SaveCombatant(encounter, combatant);
            }

            token.Conditions = keys;

            if (_remote)
            {
                RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, token.Facing,
                    conditions: keys);
            }
            else
            {
                MapService.SaveToken(token);
                if (_inRoom)
                {
                    if (encounter != null)
                        RoomSession.Instance.PublishInitiative(
                            Services.InitiativeService.BuildState(encounter));
                    PublishMap();
                }
            }

            PushWebMap();
        }

        private void SetTokenFrame(MapTokenData token, string color)
        {
            if (token == null) return;
            token.FrameColor = string.IsNullOrWhiteSpace(color) ? "" : color.Trim();
            if (!_remote)
            {
                MapService.SaveToken(token);
                PublishMap();
            }
            PushWebMap();
        }

        /// <summary>Свой цвет сектора обзора у жетона (пусто — по стороне).</summary>
        private void SetTokenViewColor(MapTokenData token, string color)
        {
            if (token == null) return;
            token.ViewColor = string.IsNullOrWhiteSpace(color) ? "" : color.Trim();

            if (!_remote)
            {
                MapService.SaveToken(token);
                PublishMap();
            }
            else
            {
                // у игрока своего файла нет: цвет уезжает хосту вместе с ходом
                RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, token.Facing,
                    viewColor: token.ViewColor ?? "");
            }

            PushWebMap();
        }

        /// <summary>
        /// Жетон выбрали на карте WebGL. Ползунок «Обзор» в панели двигает
        /// именно выделенный жетон, поэтому без этого он ничего не делал.
        /// </summary>
        private void OnWebTokenSelected(int tokenId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenSelected(tokenId)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null) return;

            SelectToken(token);
            PushWebMap();
        }

        /// <summary>Жетон начали вести заново — старый маршрут стираем у всех.</summary>
        private void OnWebTrailCleared(int tokenId)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTrailCleared(tokenId)));
                return;
            }

            ClearTrail(tokenId);

            // Пустой список — сигнал «след стёрт»: у клиентов он тоже убирается
            if (_inRoom) RoomSession.Instance.PublishMapTrail(tokenId, new List<MapTrailPoint>());
            PushWebMap();
        }

        /// <summary>
        /// Поворот взгляда из WebView: пока Q/E держат, угол приходит часто и
        /// без записи в базу; при отпускании — окончательный, с сохранением.
        /// </summary>
        private void OnWebRotate(int tokenId, double facing, bool commit)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebRotate(tokenId, facing, commit)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null || !CanMoveToken(token)) return;

            token.Facing = NormalizeDeg(facing);

            if (!commit)
            {
                // Промежуточный угол: остальным — лёгким live-сообщением.
                if ((DateTime.UtcNow - _lastLiveMoveSent).TotalMilliseconds >= 120)
                {
                    _lastLiveMoveSent = DateTime.UtcNow;
                    if (_remote)
                        RoomSession.Instance.SendMapMoveLive(token.Id, token.X, token.Y, token.Facing);
                    else if (_inRoom)
                        RoomSession.Instance.PublishLiveTokenMove(token.Id, token.X, token.Y, token.Facing);
                }

                // Разведку запоминаем по ходу поворота, а не только в конце:
                // иначе при быстром развороте сектор «перепрыгивает» и часть
                // обзора вообще не попадает в разведанное. Сам угол обратно
                // не шлём: в WebView он уже новее нашего (там идёт анимация).
                if ((DateTime.UtcNow - _lastLiveFog).TotalMilliseconds >= 550)
                {
                    _lastLiveFog = DateTime.UtcNow;
                    if (WebFogEnabled())
                    {
                        RememberCurrentVision(false);
                        PushWebSeen();
                    }
                }
                return;
            }

            // Окончательный угол (WebView закончил анимацию и прислал его).
            _rotating = false;
            _rotateToken = null;
            _rotateWatch?.Stop();

            WebLog($"rotate done {token.Id} facing={token.Facing:0.0}");

            if (_remote)
                RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y, token.Facing);
            else
            {
                MapService.SaveToken(token);
                PublishMap();
            }

            // persist = true: всё, что накопили за поворот, уходит в базу
            // и в комнату одним пакетом
            RememberCurrentVision();
            RenderFov();
            BuildFogMask();
            PushWebMap();
        }

        /// <summary>Поворот, размер, цвет и удаление фигуры из меню WebGL.</summary>
        private void OnWebShapeAction(int shapeId, string action, string value)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebShapeAction(shapeId, action, value)));
                return;
            }

            if (_scene == null || (!_isDm && !_remote)) return;
            var shape = _scene.Shapes.FirstOrDefault(s => s.Id == shapeId);
            if (shape == null) return;

            WebLog($"shape action {shapeId} {action} '{value}'");

            switch ((action ?? "").ToLowerInvariant())
            {
                case "rotate":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double delta)) return;
                    shape.Angle = ((shape.Angle + delta) % 360 + 360) % 360;
                    break;

                case "radius":
                    if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out double step)) return;
                    shape.RadiusCells = Math.Max(0.5, Math.Min(20, shape.RadiusCells + step));
                    break;

                // Подпись фигуры или точки интереса
                case "rename":
                    {
                        string current = shape.Label ?? "";
                        string text = Windows.NotePromptWindow.AskText(Window.GetWindow(this),
                            "Подпись", "Название", current, "Сохранить");
                        if (text == null) return;
                        shape.Label = text.Trim();
                    }
                    break;

                case "color":
                    if (string.IsNullOrWhiteSpace(value)) return;
                    shape.Color = value.Trim();
                    break;


                case "script":
                    if (!_isDm) return;
                    if (!Windows.ZoneScriptWindow.Edit(Window.GetWindow(this), shape)) return;
                    break;

                case "delete":
                    CommitShape(shape, "delete");
                    PushWebMap();
                    return;

                default:
                    return;
            }

            CommitShape(shape, "update");
            PushWebMap();
        }

        private void OnWebShapePlaced(double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebShapePlaced(x, y)));
                return;
            }
            if (_scene == null || _pendingShape == null || (!_isDm && !_remote)) return;
            PlaceShape(new Point(x, y));
            PushWebMap();
        }

        /// <summary>Перенос группы жетонов: сохраняем все разом и публикуем один раз.</summary>
        private void OnWebTokensMoved(List<(int Id, double X, double Y)> moves)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokensMoved(moves)));
                return;
            }
            if (_scene == null || moves == null || moves.Count == 0) return;

            WebLog($"moveTokens {moves.Count}");

            int moved = 0;
            foreach (var move in moves)
            {
                var token = _scene.Tokens.FirstOrDefault(t => t.Id == move.Id);
                if (token == null || !CanMoveToken(token)) continue;

                token.X = Math.Max(0, Math.Min(_scene.WidthPx, move.X));
                token.Y = Math.Max(0, Math.Min(_scene.HeightPx, move.Y));
                if (_remote) continue;

                MapService.SaveToken(token);
                moved++;
            }

            if (_remote)
            {
                // у игрока своего файла нет: каждый шаг уходит хосту
                foreach (var move in moves)
                    RoomSession.Instance.SendMapMove(move.Id, move.X, move.Y);
            }
            else if (moved > 0)
            {
                PublishMap();
            }

            RememberCurrentVision();
            PushWebMap();
        }

        /// <summary>ПКМ по пустому месту карты: поставить точку интереса.</summary>
        private void OnWebPointPlaced(double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebPointPlaced(x, y)));
                return;
            }
            if (_scene == null || (!_isDm && !_remote)) return;

            _pendingShape = new MapShapeData { Kind = "point", RadiusCells = 0 };
            PlaceShape(new Point(x, y));
            PushWebMap();
        }

        private void OnWebPinged(double x, double y)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebPinged(x, y)));
                return;
            }
            if (_scene == null) return;
            var point = new Point(x, y);
            ShowPing(point);
            RoomSession.Instance.SendMapPing(x, y);
            _pingCheck.IsChecked = false;
            PushWebMap();
        }

        private string WebAssetUrl(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return "";
            if (_webDataUrls.TryGetValue(path, out var cached)) return cached;

            try
            {
                string root = Path.GetFullPath(PhotoHelper.GetWritableRoot());
                string full = Path.GetFullPath(path);
                if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    return "";

                string relative = full.Substring(root.Length).TrimStart('\\', '/');
                string url = "https://hmwt.assets/" + string.Join("/",
                    relative.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(Uri.EscapeDataString));
                _webDataUrls[path] = url;
                return url;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[MapWeb] asset: " + ex.Message);
                return "";
            }
        }

        private void OnWebTokenMoved(int tokenId, double x, double y, List<MapTrailPoint> trail)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnWebTokenMoved(tokenId, x, y, trail)));
                return;
            }

            if (_scene == null) return;
            var token = _scene.Tokens.FirstOrDefault(t => t.Id == tokenId);
            if (token == null || !CanMoveToken(token)) return;

            if (trail != null && trail.Count >= 2)
            {
                _trails[tokenId] = trail
                    .Where(p => p != null)
                    .Select(p => new Point(p.X, p.Y))
                    .ToList();
                PublishLocalTrail(tokenId);
            }

            if (_scene.SnapToGrid)
            {
                double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                x = Math.Round((x - cell / 2) / cell) * cell + cell / 2;
                y = Math.Round((y - cell / 2) / cell) * cell + cell / 2;
            }

            token.X = Math.Max(0, Math.Min(_scene.WidthPx, x));
            token.Y = Math.Max(0, Math.Min(_scene.HeightPx, y));

            if (_remote)
            {
                RoomSession.Instance.SendMapMove(token.Id, token.X, token.Y);
                RememberCurrentVision();
            }
            else
            {
                MapService.SaveToken(token);
                PublishMap();
                RememberCurrentVision();
            }

            PushWebMap();
        }
    }
}
