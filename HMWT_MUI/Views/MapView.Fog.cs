using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Туман и обзор: геометрия видимости, разведанное, LOS и секторы.
    /// Вынесено из MapView.cs (partial).
    /// </summary>
    public sealed partial class MapView
    {
        private Geometry BuildVisionGeometry(MapTokenData token, Matrix matrix)
        {
            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double reveal = token.RevealCells > 0 ? token.RevealCells : 6;
            double radius = cell * Math.Max(1, reveal);

            var center = new Point(token.X, token.Y);
            bool full = token.ViewAngle >= 359.9;
            double half = full ? 180.0 : Math.Max(2.5, Math.Min(180.0, token.ViewAngle / 2.0));
            double startDeg = full ? 0 : token.Facing - half;
            double endDeg = full ? 360 : token.Facing + half;

            var walls = _scene.Walls;
            var figure = new PathFigure { StartPoint = center, IsClosed = true };

            // Лучи: равномерные + отдельные в углы стен. Без лучей в углы обзор
            // «просачивался» между соседними лучами и игрок видел за стеной.
            var angles = new List<double>();
            const int rays = 96;
            for (int i = 0; i <= rays; i++) angles.Add(startDeg + (endDeg - startDeg) * i / rays);

            foreach (var wall in walls)
            {
                if (wall.IsOpen) continue;

                foreach (var corner in new[] { new Point(wall.X1, wall.Y1), new Point(wall.X2, wall.Y2) })
                {
                    double dx = corner.X - center.X, dy = corner.Y - center.Y;
                    if (Math.Sqrt(dx * dx + dy * dy) > radius) continue;

                    double cornerAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;

                    // Сравниваем не абсолютный угол, а отклонение от взгляда:
                    // конус, смотрящий на 0°, раньше «терял» углы стен на 350°.
                    double rel = NormalizeDeg(cornerAngle - token.Facing);
                    if (rel < -half || rel > half) continue;

                    // Для полного круга угол приводим к 0..360, для конуса —
                    // к непрерывной шкале вокруг направления взгляда.
                    double angle = full ? NormalizeDeg(cornerAngle) : token.Facing + rel;

                    angles.Add(angle - 0.35);
                    angles.Add(angle);
                    angles.Add(angle + 0.35);
                }
            }

            angles.Sort();
            foreach (double angle in angles)
            {
                double distance = radius;

                foreach (var wall in walls)
                {
                    if (wall.IsOpen) continue;      // открытая дверь не мешает
                    double hit = RayHitDistance(center, angle, wall, radius);
                    if (hit < distance) distance = hit;
                }

                figure.Segments.Add(new LineSegment(PointOnCircle(center, distance, angle), false));
            }

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Transform = new MatrixTransform(matrix);
            return geometry;
        }

        /// <summary>Приводит угол к диапазону 0..360.</summary>
        private static double NormalizeDeg(double deg)
        {
            deg %= 360.0;
            if (deg < 0) deg += 360.0;
            return deg;
        }

        /// <summary>Расстояние до стены по лучу (или maxDist, если стена не мешает).</summary>
        private static double RayHitDistance(Point origin, double degrees, MapWallData wall, double maxDist)
        {
            double rad = degrees * Math.PI / 180.0;
            double dx = Math.Cos(rad), dy = Math.Sin(rad);

            double x3 = wall.X1, y3 = wall.Y1;
            double sx = wall.X2 - x3, sy = wall.Y2 - y3;

            // Пересечение луча p + t·r и отрезка q + u·s.
            // Раньше знак u был перевёрнут, и проверка 0..1 отбрасывала
            // все настоящие пересечения — стены не перекрывали обзор вовсе.
            double denom = dx * sy - dy * sx;
            if (Math.Abs(denom) < 1e-9) return maxDist;

            double t = ((x3 - origin.X) * sy - (y3 - origin.Y) * sx) / denom;
            double u = ((x3 - origin.X) * dy - (y3 - origin.Y) * dx) / denom;

            if (u < 0 || u > 1 || t <= 0) return maxDist;
            return Math.Min(t, maxDist);
        }

        /// <summary>Радиус обзора жетона: сколько видно и какой круг рассеивает туман.</summary>
        private void SetRevealCells(MapTokenData token, double cells)
        {
            // 0 — «обзор выключен»: жетон не развеивает туман и не рисует сектор
            token.RevealCells = Math.Max(0, Math.Min(40, cells));
            MapService.SaveToken(token);
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
        }
        private void ShowWallMenu(FrameworkElement target, MapWallData wall)
        {
            var menu = new ContextMenu();
            ApplyDarkStyle(menu, "DarkMenu");

            bool door = string.Equals(wall.Kind, "door", StringComparison.OrdinalIgnoreCase);

            if (door)
            {
                AddMenuItem(menu, wall.IsOpen ? "Закрыть дверь" : "Открыть дверь", () =>
                {
                    wall.IsOpen = !wall.IsOpen;
                    MapService.UpdateWall(wall);
                    RenderWalls();
                    PublishMap();
                    BuildFogMask();
                });
                AddMenuItem(menu, "Сделать стеной", () =>
                {
                    wall.Kind = "wall";
                    wall.IsOpen = false;
                    MapService.UpdateWall(wall);
                    RenderWalls();
                    PublishMap();
                    BuildFogMask();
                });
            }
            else
            {
                AddMenuItem(menu, "Сделать дверью", () =>
                {
                    wall.Kind = "door";
                    MapService.UpdateWall(wall);
                    RenderWalls();
                    PublishMap();
                    BuildFogMask();
                });
            }

            menu.Items.Add(BuildSeparator());
            AddMenuItem(menu, "Убрать стену", () =>
            {
                MapService.DeleteWall(wall.Id);
                _scene.Walls.RemoveAll(w => w.Id == wall.Id);
                RenderWalls();
                PublishMap();
                BuildFogMask();
            });

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        /// <summary>Полигон разведанного: точки через «;», координаты через запятую.</summary>
        private static Geometry SeenToGeometry(string points, Matrix matrix)
        {
            var points2 = ParsePoints(points);
            if (points2.Count < 3) return null;

            var figure = new PathFigure { StartPoint = points2[0], IsClosed = true };
            for (int i = 1; i < points2.Count; i++) figure.Segments.Add(new LineSegment(points2[i], false));

            var geometry = new PathGeometry();
            geometry.Figures.Add(figure);
            geometry.Transform = new MatrixTransform(matrix);
            return geometry;
        }

        /// <summary>
        /// Запоминает то, что игроки видят сейчас: эти области останутся открытыми.
        /// persist = false — только в память (идёт поворот или драг): запись в
        /// SQLite и рассылка сделаются одним пакетом в конце, иначе каждый шаг
        /// тормозит UI-поток на диске и сети.
        /// </summary>
        private void RememberCurrentVision(bool persist = true)
        {
            var perfWatch = System.Diagnostics.Stopwatch.StartNew();

            if (_scene == null || !_scene.FogOn) return;

            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);
            var geometries = new List<Geometry>();
            var newSeen = new List<string>();

            foreach (var token in _scene.Tokens)
            {
                if (!RevealsFog(token, combined)) continue;

                // в координатах картинки: при показе применится поворот карты
                var geometry = BuildVisionGeometry(token, Matrix.Identity);
                if (geometry != null) geometries.Add(geometry);
            }

            foreach (var geometry in geometries)
            {
                try
                {
                    var path = geometry as PathGeometry;
                    if (path == null || path.Figures.Count == 0) continue;

                    // Центр — это StartPoint фигуры, а не точка сегмента. Без него
                    // сохранённый полигон замыкался хордой и превращался в «шапку»
                    // сектора: при движении жетона оставались куски, а не конус.
                    var points = new List<Point> { path.Figures[0].StartPoint };
                    foreach (var segment in path.Figures[0].Segments)
                        if (segment is LineSegment line) points.Add(line.Point);

                    if (points.Count < 4) continue;

                    string text = PointsToString(points);

                    // Такой участок уже запоминали: не плодим одинаковые строки
                    // при каждом обновлении сцены от мастера
                    if (!AddSeenPoint(text)) continue;
                    newSeen.Add(text);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MapView] разведанное: {ex.Message}");
                }
            }

            if (!persist)
            {
                _pendingSeenWrites.AddRange(newSeen);

                // Не копим бесконечно: при долгом движении сбрасываем порциями
                if (_pendingSeenWrites.Count >= 24) FlushPendingSeen();
            }
            else
            {
                FlushPendingSeen();

                if (newSeen.Count > 0)
                {
                    try { MapService.AddSeenBatch(_scene.Id, newSeen); }
                    catch (Exception ex) { Debug.WriteLine($"[MapView] запись разведанного: {ex.Message}"); }

                    // Общий туман: новый сектор уходит ведущему и остальным.
                    // В режиме «свой туман» разведанное остаётся личным.
                    if (_inRoom && combined)
                        foreach (string text in newSeen) RoomSession.Instance.ShareMapSeen(text);
                }
            }

            PerfLog("разведано", perfWatch.ElapsedMilliseconds,
                $"всего={_seenPoints.Count}, жетонов={_scene.Tokens.Count}");
        }

        /// <summary>Сбрасывает накопленное разведанное в SQLite и в комнату.</summary>
        private void FlushPendingSeen()
        {
            if (_pendingSeenWrites.Count == 0 || _scene == null) return;

            var batch = _pendingSeenWrites.ToList();
            _pendingSeenWrites.Clear();

            try { MapService.AddSeenBatch(_scene.Id, batch); }
            catch (Exception ex) { Debug.WriteLine($"[MapView] запись разведанного: {ex.Message}"); }

            if (_inRoom && !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase))
            {
                foreach (string text in batch)
                    RoomSession.Instance.ShareMapSeen(text);
            }
        }

        // ══════════════════════ ТУМАН ВОЙНЫ ══════════════════════

        /// <summary>
        /// Маска тумана: закрыто всё, кроме того, что видят жетоны игроков.
        /// Круговой обзор вырезает круг, конус — сектор. Мастеру туман
        /// полупрозрачный (видно, что скрыто), игрокам — сплошной чёрный.
        ///
        /// Режим «общий»: игроки видят суммарный обзор отряда, «свой» — только
        /// свой. Так мастер включает «не вижу, что за спиной у соседа».
        /// </summary>
        /// <summary>Загружает разведанное с жёстким пределом последних секторов.</summary>
        private void LoadSeenPoints(int sceneId)
        {
            _seenPoints.Clear();
            _seenSet.Clear();

            var loaded = MapService.LoadSeen(sceneId);
            int from = Math.Max(0, loaded.Count - MaxSeenSectors);
            for (int i = from; i < loaded.Count; i++)
            {
                _seenPoints.Add(loaded[i]);
                _seenSet.Add(loaded[i]);
            }

            _seenLoadedSceneId = sceneId;
            _seenVersion++;
        }

        private void ClearSeenPoints()
        {
            _seenPoints.Clear();
            _seenSet.Clear();
            _seenVersion++;
        }

        /// <summary>Добавляет сектор с дедупликацией и пределом; true — если он новый.</summary>
        private bool AddSeenPoint(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!_seenSet.Add(text)) return false;

            _seenPoints.Add(text);
            _seenVersion++;

            if (_seenPoints.Count > MaxSeenSectors)
            {
                int remove = _seenPoints.Count - MaxSeenSectors;
                for (int i = 0; i < remove; i++) _seenSet.Remove(_seenPoints[i]);
                _seenPoints.RemoveRange(0, remove);
            }

            return true;
        }

        /// <summary>
        /// Геометрия разведанного по одной на сектор. Старые сектора не
        /// разбираются заново: добавляем только новый полигон, а при
        /// повороте/масштабе карты кэш пересобирается целиком.
        /// </summary>
        private List<Geometry> GetSeenGeometries(Matrix matrix)
        {
            if (!matrix.Equals(_seenGeometryMatrix))
            {
                _seenGeometries.Clear();
                _seenGeometryMatrix = matrix;
            }

            var alive = new HashSet<string>(_seenPoints, StringComparer.Ordinal);
            foreach (string stale in _seenGeometries.Keys.Where(k => !alive.Contains(k)).ToList())
                _seenGeometries.Remove(stale);

            var result = new List<Geometry>(_seenPoints.Count);

            foreach (string text in _seenPoints)
            {
                if (!_seenGeometries.TryGetValue(text, out var geometry))
                {
                    geometry = SeenToGeometry(text, matrix);
                    if (geometry == null) continue;

                    try { geometry.Freeze(); }
                    catch { continue; }

                    _seenGeometries[text] = geometry;
                }

                result.Add(geometry);
            }

            return result;
        }

        private void BuildFogMask()
        {
            if (_webMapReady) return;
            var perfWatch = System.Diagnostics.Stopwatch.StartNew();

            if (_scene == null) return;

            if (!_scene.FogOn)
            {
                _fog.Data = null;
                _fog.Visibility = Visibility.Collapsed;
                return;
            }

            _fog.Visibility = Visibility.Visible;

            double worldW = Math.Max(1, _world.Width);
            double worldH = Math.Max(1, _world.Height);
            var outer = new RectangleGeometry(new Rect(0, 0, worldW, worldH));

            var vision = new GeometryGroup { FillRule = FillRule.Nonzero };
            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);
            Matrix matrix = _content.RenderTransform is MatrixTransform mt ? mt.Matrix : Matrix.Identity;

            foreach (var token in _scene.Tokens)
            {
                if (token.Hidden && !_isDm) continue;
                if (!RevealsFog(token, combined)) continue;

                var geometry = BuildVisionGeometry(token, matrix);
                if (geometry != null) vision.Children.Add(geometry);
            }

            // разведанные области: то, что уже видели, туманом не закрываем.
            // Геометрия кэшируется по секторам, иначе 240 полигонов разбирались
            // на каждый кадр.
            var seenGeometries = GetSeenGeometries(matrix);

            if (_seenUnion == null || _seenUnionVersion != _seenVersion ||
                !_seenUnionMatrix.Equals(matrix))
            {
                try
                {
                    // Не булево объединение, а группа: Combine на сотнях
                    // полигонов мог потерять кусок и сам по себе стоил дорого.
                    var group = new GeometryGroup { FillRule = FillRule.Nonzero };
                    foreach (var geometry in seenGeometries)
                    {
                        if (geometry != null) group.Children.Add(geometry);
                    }

                    if (group.Children.Count > 0)
                    {
                        try { group.Freeze(); } catch { }
                        _seenUnion = group;
                    }
                    else
                    {
                        _seenUnion = null;
                    }
                }
                catch
                {
                    _seenUnion = null;
                }

                _seenUnionVersion = _seenVersion;
                _seenUnionMatrix = matrix;
            }

            var known = new GeometryGroup { FillRule = FillRule.Nonzero };
            foreach (var child in vision.Children) known.Children.Add(child);
            if (_seenUnion != null) known.Children.Add(_seenUnion);

            // Один слой «неизвестного»: и мастер, и игрок видят открытым всё,
            // что уже разведано. Раньше у игрока плёнка вырезала только текущий
            // обзор, а разведанное закрывал второй чёрный слой — поэтому карта
            // у клиента оставалась чёрной, хотя хост видел её целиком.
            _fog.Data = known.Children.Count == 0
                ? (Geometry)outer
                : new CombinedGeometry(GeometryCombineMode.Exclude, outer, known);

            // Второй слой больше не нужен: всё «неизвестное» уже закрыто выше.
            // Оставляем его пустым, чтобы не двоить заливку.
            _fogDark.Data = null;

            DumpMapState("после построения тумана");
            PerfLog("туман", perfWatch.ElapsedMilliseconds,
                $"разведано={_seenPoints.Count}, жетонов={_scene.Tokens.Count}, стен={_scene.Walls.Count}, " +
                $"штрихов={_scene.Strokes.Count}");
        }

        /// <summary>
        /// Диагностика тормозов: в map-perf.log пишутся только операции дольше
        /// 40 мс — по ним видно, растёт ли туман, следы или пересборка карты.
        /// </summary>
        private static void PerfLog(string what, long ms, string detail)
        {
            if (ms < 40) return;

            try
            {
                string dir = HMWT_MUI.PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "map-perf.log"),
                    $"{DateTime.Now:HH:mm:ss.fff} {what}: {ms} мс · {detail}\r\n");
            }
            catch { }
        }

        /// <summary>
        /// Куда на экране попали полотно карты и жетоны. Нужно проверкам
        /// интерфейса: нажать на жетон или провести ластиком можно только зная
        /// настоящие координаты, а Border в дереве автоматизации не виден.
        /// Пишется только при HMWT_MAP_DUMP=1.
        /// </summary>
        private void DumpUiGeometry()
        {
            try
            {
                if (Environment.GetEnvironmentVariable("HMWT_MAP_DUMP") != "1") return;
                if (_scene == null || !IsVisible || _viewport.ActualWidth < 10) return;

                Point tl = _viewport.PointToScreen(new Point(0, 0));
                Point br = _viewport.PointToScreen(new Point(_viewport.ActualWidth, _viewport.ActualHeight));

                var text = new System.Text.StringBuilder();
                text.AppendLine("=== где карта на экране ===");
                text.AppendLine($"viewport: {tl.X:0},{tl.Y:0} - {br.X:0},{br.Y:0} " +
                                $"({_viewport.ActualWidth:0}x{_viewport.ActualHeight:0})");
                text.AppendLine($"scale: {_scale:0.####}; tx: {_tx:0.##}; ty: {_ty:0.##}; " +
                                $"scene: {_scene.WidthPx}x{_scene.HeightPx}");

                foreach (var child in _tokenLayer.Children)
                {
                    if (!(child is Border host) || !(host.Tag is MapTokenData token)) continue;
                    if (host.ActualWidth <= 0 || host.ActualHeight <= 0) continue;

                    Point c = host.PointToScreen(new Point(host.ActualWidth / 2, host.ActualHeight / 2));
                    text.AppendLine($"token {token.Id} «{token.Label}»: {c.X:0},{c.Y:0} " +
                                    $"({host.ActualWidth:0}x{host.ActualHeight:0})");
                }

                string dir = PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "map-ui.txt"), text.ToString(), new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] геометрия для проверки: {ex.Message}");
            }
        }

        /// <summary>
        /// Диагностика: при HMWT_MAP_DUMP=1 состояние карты пишется в
        /// map-dump.txt. Помогает разбирать «у игрока не рассеивается туман».
        /// </summary>
        private void DumpMapState(string where)
        {
            try
            {
                if (Environment.GetEnvironmentVariable("HMWT_MAP_DUMP") != "1") return;
                if (_scene == null) return;

                string dir = PhotoHelper.GetWritableRoot();
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "map-dump.txt");

                var text = new System.Text.StringBuilder();
                text.AppendLine("=== карта: " + where + " ===");
                text.AppendLine("время: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
                text.AppendLine("версия: " + NetVersions.AppVersion + ", режим: " + (_remote ? "игрок" : _isDm ? "мастер" : "просмотр"));
                text.AppendLine("сцена: " + _scene.Name + " " + _scene.WidthPx + "x" + _scene.HeightPx +
                                ", клетка " + _scene.GridPx + ", туман " + _scene.FogOn + ", режим тумана " + _scene.FogMode);
                text.AppendLine("жетонов: " + _scene.Tokens.Count + ", стен: " + _scene.Walls.Count +
                                ", штрихов: " + _scene.Strokes.Count + ", фигур: " + _scene.Shapes.Count);
                text.AppendLine("разведано участков: " + _seenPoints.Count);

                foreach (var token in _scene.Tokens)
                {
                    text.AppendLine($"  жетон {token.Id}: «{token.Label}», игрок={token.IsPlayer}, " +
                                    $"бой={token.CombatantId}, обзор={token.ViewAngle}°, дальность={token.RevealCells}, " +
                                    $"поз=({token.X:0},{token.Y:0}), скрыт={token.Hidden}");
                }

                text.AppendLine("слои тумана: плёнка=" + (_fog.Data != null) + ", чёрный=" + (_fogDark.Data != null));

                File.WriteAllText(file, text.ToString(), new System.Text.UTF8Encoding(true));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] дамп: {ex.Message}");
            }
        }
        /// <summary>Чей обзор рассеивает туман у этого зрителя.</summary>
        private bool RevealsFog(MapTokenData token, bool combined)
        {
            // У игрока своей базы боёв нет: признак «это жетон игрока» приходит
            // вместе с картой, иначе туман у него не рассеивался вообще.
            bool player = token.IsPlayer;
            if (!player && _combatants.TryGetValue(token.CombatantId, out var linked) &&
                linked.Combatant != null && linked.Combatant.Kind == CombatantKind.Player)
                player = true;

            if (!player) return false;          // монстры туман не рассеивают
            if (_isDm) return true;              // у мастера — обзор всего отряда
            if (combined) return true;           // общий туман: обзор всех игроков
            return CanMoveToken(token);          // свой туман: только собственный
        }

        /// <summary>
        /// Виден ли чужой жетон из-за стены. Туман закрывает область, но сам
        /// жетон рисовался поверх и «просвечивал» через стену. Проверяем
        /// прямую от каждого своего/отрядного жетона до центра и углов цели:
        /// если хоть одна прямая не пересекает закрытую стену — виден.
        /// </summary>
        private bool IsTokenVisibleByLos(MapTokenData token, bool combined)
        {
            if (token == null || _scene == null) return true;
            if (_showPlayersCheck.IsChecked == true && token.IsPlayer && !token.Hidden) return true;
            if (_isDm) return true;              // мастер видит всех
            if (CanMoveToken(token)) return true; // свой жетон виден всегда

            var viewers = _scene.Tokens
                .Where(t => t.Id != token.Id && RevealsFog(t, combined))
                .ToList();

            if (viewers.Count == 0) return false;

            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double half = Math.Max(cell * 0.25, cell * 0.35 * Math.Max(1, token.SizeCells));

            var targets = new[]
            {
                new Point(token.X, token.Y),
                new Point(token.X - half, token.Y - half),
                new Point(token.X + half, token.Y - half),
                new Point(token.X - half, token.Y + half),
                new Point(token.X + half, token.Y + half)
            };

            foreach (var viewer in viewers)
            {
                var from = new Point(viewer.X, viewer.Y);
                foreach (var target in targets)
                    if (HasClearLine(from, target)) return true;
            }

            return false;
        }

        private bool HasClearLine(Point from, Point to)
        {
            if (_scene?.Walls == null || _scene.Walls.Count == 0) return true;

            foreach (var wall in _scene.Walls)
            {
                if (wall == null || wall.IsOpen) continue;
                if (SegmentsCross(from, to,
                        new Point(wall.X1, wall.Y1), new Point(wall.X2, wall.Y2)))
                    return false;
            }
            return true;
        }

        private static bool SegmentsCross(Point a, Point b, Point c, Point d)
        {
            double d1 = Cross(c, d, a);
            double d2 = Cross(c, d, b);
            double d3 = Cross(a, b, c);
            double d4 = Cross(a, b, d);

            return ((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) &&
                   ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0));
        }

        private static double Cross(Point origin, Point a, Point b)
            => (a.X - origin.X) * (b.Y - origin.Y) - (a.Y - origin.Y) * (b.X - origin.X);

        /// <summary>Прячет чужие жетоны, закрытые стеной от всех своих жетонов.</summary>
        private void RefreshTokenVisibility()
        {
            if (_webMapReady) return;
            if (_scene == null) return;

            bool combined = !string.Equals(_scene.FogMode, "own", StringComparison.OrdinalIgnoreCase);

            foreach (var child in _tokenLayer.Children)
            {
                if (!(child is Border host) || !(host.Tag is MapTokenData token)) continue;
                host.Visibility = IsTokenVisibleByLos(token, combined)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }
        }

        /// <summary>Круг или сектор обзора жетона в координатах мира.</summary>
        private Geometry BuildRevealGeometry(MapTokenData token, Matrix matrix)
        {
            double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
            double radius = cell * 6;
            var center = new Point(token.X, token.Y);

            Geometry geometry;

            if (token.ViewAngle >= 359.9)
            {
                geometry = new EllipseGeometry(center, radius, radius);
            }
            else
            {
                double half = Math.Max(5, Math.Min(180, token.ViewAngle / 2));
                Point start = PointOnCircle(center, radius, token.Facing - half);
                Point end = PointOnCircle(center, radius, token.Facing + half);

                var figure = new PathFigure { StartPoint = center, IsClosed = true };
                figure.Segments.Add(new LineSegment(start, true));
                figure.Segments.Add(new ArcSegment
                {
                    Point = end,
                    Size = new Size(radius, radius),
                    SweepDirection = SweepDirection.Clockwise,
                    IsLargeArc = half * 2 > 180
                });
                figure.Segments.Add(new LineSegment(center, true));

                var path = new PathGeometry();
                path.Figures.Add(figure);
                geometry = path;
            }

            geometry.Transform = new MatrixTransform(matrix);
            return geometry;
        }

        /// <summary>Режим тумана: общий обзор отряда или у каждого свой.</summary>
        private void SetFogMode(string mode)
        {
            if (_scene == null || !_isDm) return;

            _scene.FogMode = string.Equals(mode, "own", StringComparison.OrdinalIgnoreCase) ? "own" : "combined";
            MapService.SaveScene(_scene);
            PublishMap();
            BuildFogMask();
        }

        // ══════════════════════ ПОЛЕ ЗРЕНИЯ ══════════════════════

        private void SetViewAngle(MapTokenData token, double angle)
        {
            token.ViewAngle = angle;
            MapService.SaveToken(token);
            PublishMap();
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
        }

        private void RotateFacing(MapTokenData token, double delta)
        {
            token.Facing = ((token.Facing + delta) % 360 + 360) % 360;
            MapService.SaveToken(token);
            PublishMap();
            RenderFov();
            RememberCurrentVision();
            BuildFogMask();
        }

        private void RenderFov()
        {
            if (_webMapReady) return;
            _fovLayer.Children.Clear();
            if (_scene == null) return;

            foreach (var token in _scene.Tokens)
            {
                if (token.Hidden && !_isDm) continue;
                if (!_isDm && !CanMoveToken(token)) continue;

                if (token.ViewAngle >= 359.9)
                {
                    // Круговой обзор: полупрозрачный круг дальности, чтобы игрок
                    // видел, докуда достаёт его взгляд
                    double cell = _scene.GridPx > 4 ? _scene.GridPx : 70;
                    double radius = Math.Max(cell, cell * Math.Max(1, token.RevealCells));

                    var circle = new System.Windows.Shapes.Ellipse
                    {
                        Width = radius * 2,
                        Height = radius * 2,
                        Fill = new SolidColorBrush(Color.FromArgb(0x16, 0x5F, 0xD4, 0xDD)),
                        Stroke = new SolidColorBrush(Color.FromArgb(0x66, 0x5F, 0xD4, 0xDD)),
                        StrokeThickness = 1.2,
                        IsHitTestVisible = false
                    };

                    Canvas.SetLeft(circle, token.X - radius);
                    Canvas.SetTop(circle, token.Y - radius);
                    _fovLayer.Children.Add(circle);
                    continue;
                }

                var sector = BuildSectorVisual(token);
                if (sector != null) _fovLayer.Children.Add(sector);
            }
        }
    }
}
