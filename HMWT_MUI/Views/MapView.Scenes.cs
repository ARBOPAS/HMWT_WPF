using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Сцены и картинка карты: открытие сцены/снимка от мастера, загрузка
    /// картинки, окно сцен. Вынесено из MapView.cs (partial).
    /// </summary>
    public sealed partial class MapView
    {
        private void OpenRemoteScene(MapSceneData remote)
        {
            int previousSceneId = _scene?.Id ?? 0;
            _pendingLiveMoves.Clear();
            _liveMoveTimer.Stop();
            ClearMeasure();
            _scene = remote;

            _gridCheck.IsChecked = _scene.GridOn;
            _snapCheck.IsChecked = _scene.SnapToGrid;
            _cellField.Text = ((int)Math.Round(_scene.GridPx <= 4 ? 70 : _scene.GridPx)).ToString();
            _gridThicknessSlider.Value = Math.Max(_gridThicknessSlider.Minimum,
                Math.Min(_gridThicknessSlider.Maximum, _scene.GridThickness));
            _suppressFogEvents = true;
            try { _fogCheck.IsChecked = _scene.FogOn; }
            finally { _suppressFogEvents = false; }

            string path = string.IsNullOrWhiteSpace(_scene.ImageHash)
                ? null
                : RemoteImagePath(_scene.ImageHash);

            if (string.IsNullOrWhiteSpace(_scene.ImageHash))
            {
                // Сцена есть, а картинки в ней нет: просить нечего
                _mapImage.Source = null;
                _mapImage.Width = 0;
                _mapImage.Height = 0;
                _status.Text = "Мастер ещё не загрузил карту";
            }
            else if (path == null)
            {
                RequestMapAsset(_scene.ImageHash);
                _mapImage.Source = null;
                _mapImage.Width = 0;
                _mapImage.Height = 0;
                _status.Text = "Мастер прислал карту, ждём картинку…";
            }
            else
            {
                if (!string.Equals(path, _loadedImagePath, StringComparison.OrdinalIgnoreCase) ||
                    _mapImage.Source == null)
                    LoadImageFromFile(path);

                _status.Text = $"{_scene.Name}: {_scene.WidthPx}×{_scene.HeightPx}, жетонов {_scene.Tokens.Count}";
            }

            // недостающие портреты жетонов запрашивает таймер повторов:
            // он же лечит потерянные ответы
            foreach (var token in _scene.Tokens)
            {
                if (string.IsNullOrWhiteSpace(token.PhotoHash)) continue;
                if (RemoteImagePath(token.PhotoHash) != null) continue;

                RequestMapAsset(token.PhotoHash);
            }

            BuildGrid();
            RenderStrokes();
            RenderShapes();
            RenderWalls();
            RebuildTokens();

            // Разведанное у каждого клиента своё и лежит локально. Раньше для
            // сцены мастера его даже не читали из базы — поэтому у игрока карта
            // каждый раз была закрыта, сколько бы он ни ходил по ней.
            // Читаем только при смене сцены: иначе каждый снимок комнаты
            // заново разбирал 240 полигонов и сбрасывал кэш тумана.
            if (_seenLoadedSceneId != _scene.Id || _seenPoints.Count == 0)
                LoadSeenPoints(_scene.Id);

            RememberCurrentVision();

            RenderTrails();
            DumpMapState("карта от мастера");
            ApplyOrientation();

            // Сцена сменилась — заставляем WebView перерисовать всё с нуля:
            // иначе он сравнивал хэши с прежней картой и оставлял старое.
            if (previousSceneId != _scene.Id)
            {
                ResetWebCache();
                _seenLoadedSceneId = 0;
                LoadSeenPoints(_scene.Id);
                WebLog($"scene switch {previousSceneId} -> {_scene.Id}");
            }

            PushWebMap();
        }

        private static string RemoteImagePath(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return null;

            try
            {
                string dir = Path.Combine(PhotoHelper.GetWritableRoot(), "Maps");
                if (!Directory.Exists(dir)) return null;

                foreach (string candidate in Directory.GetFiles(dir, "remote_" + hash + ".*"))
                    if (File.Exists(candidate)) return candidate;
            }
            catch { }

            return null;
        }

        private void LoadImageFromFile(string path)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);

                int longSide = Math.Max(_scene.WidthPx, _scene.HeightPx);
                if (longSide > 4096)
                {
                    double k = 4096.0 / longSide;
                    bitmap.DecodePixelWidth = Math.Max(1, (int)Math.Round(_scene.WidthPx * k));
                }

                bitmap.EndInit();
                bitmap.Freeze();

                _mapImage.Source = bitmap;
                _mapImage.Width = _scene.WidthPx;
                _mapImage.Height = _scene.HeightPx;
                _loadedImagePath = path;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] картинка карты: {ex.Message}");
            }
        }

        /// <summary>Карта изменилась: у игрока — пришла от мастера, у ведущего — от игрока.</summary>
        private void OnMapChanged()
        {
            if (!_inRoom) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(OnMapChanged));
                return;
            }

            // Скрытая копия вкладки не должна пересобирать сцену: при возврате
            // IsVisibleChanged сделает Refresh.
            if (!IsVisible) return;

            // Пока жетон держат мышью, карту не пересобираем: иначе объект,
            // который тащат, заменится и перетаскивание оборвётся
            if (_dragToken != null) return;

            if (!_remote)
            {
                var fromServer = RoomSession.Instance.GetMapScene();
                if (fromServer != null && _scene != null && fromServer.Id == _scene.Id)
                {
                    // Полный снимок важнее очереди промежуточных шагов
                    _pendingLiveMoves.Clear();
                    _liveMoveTimer.Stop();
                    TrackTokenMoves(_scene, fromServer);

                    // Игрок поменял состояния своего жетона — переносим их
                    // в участника боя, чтобы трекер и карта не расходились.
                    foreach (var incoming in fromServer.Tokens)
                    {
                        var local = _scene.Tokens.FirstOrDefault(t => t.Id == incoming.Id);
                        if (local == null) continue;
                        if (string.Equals(local.Conditions ?? "", incoming.Conditions ?? "",
                                StringComparison.Ordinal)) continue;

                        if (incoming.CombatantId > 0 &&
                            _combatants.TryGetValue(incoming.CombatantId, out var cond) &&
                            cond.Combatant != null)
                        {
                            cond.Combatant.Conditions = incoming.Conditions ?? "";
                            try { Services.InitiativeService.SaveCombatant(cond.Encounter, cond.Combatant); }
                            catch { }
                        }
                    }

                    _scene = fromServer;
                    BuildGrid();
                    RenderStrokes();
                    RenderShapes();
                    RebuildTokens();
                    RenderTrails();

                    // Туман мог измениться вместе с картой (стена, жетон, рисунок)
                    BuildFogMask();
                }
                return;
            }

            int keepId = _scene?.Id ?? 0;
            double scale = _scale, tx = _tx, ty = _ty;
            bool zoomed = _userZoomed;

            Refresh();

            if (_scene != null && _scene.Id == keepId && zoomed)
            {
                _scale = scale;
                _tx = tx;
                _ty = ty;
                _userZoomed = true;
                ApplyTransform();
            }
        }

        /// <summary>Просит картинку один раз и не дублирует передачу каждые две секунды.</summary>
        private void RequestMapAsset(string hash)
        {
            if (string.IsNullOrWhiteSpace(hash)) return;

            DateTime now = DateTime.UtcNow;
            if (_mapAssetAsked.TryGetValue(hash, out var asked) &&
                (now - asked).TotalSeconds < MapAssetRetrySeconds)
                return;

            _mapAssetAsked[hash] = now;
            RoomSession.Instance.RequestMapImage(hash);
        }

        private void OnMapImageReady(string hash, string path)
        {
            if (!_remote) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnMapImageReady(hash, path)));
                return;
            }

            if (!IsVisible) return;
            _mapAssetAsked.Remove(hash);
            if (_scene == null) return;

            // Картинка карты — показываем её; портрет жетона — перерисовываем
            // жетоны. Раньше портреты приходили, но экран о них не узнавал,
            // и фото появлялось только после следующего изменения карты.
            if (string.Equals(hash, _scene.ImageHash, StringComparison.OrdinalIgnoreCase))
            {
                LoadImageFromFile(path);
                ApplyOrientation();
                _status.Text = $"{_scene.Name}: {_scene.WidthPx}×{_scene.HeightPx}, жетонов {_scene.Tokens.Count}";
                return;
            }

            if (_scene.Tokens.Any(t => string.Equals(t.PhotoHash, hash, StringComparison.OrdinalIgnoreCase)))
            {
                RebuildTokens();
                PushWebMap();   // портрет должен доехать и до WebGL-карты
            }
        }

        private void OpenScene(MapSceneData scene)
        {
            try
            {
                ClearMeasure();
                _scene = MapService.LoadScene(scene.Id) ?? scene;
                _gridCheck.IsChecked = _scene.GridOn;
                _snapCheck.IsChecked = _scene.SnapToGrid;
                _cellField.Text = ((int)Math.Round(_scene.GridPx <= 4 ? 70 : _scene.GridPx)).ToString();
                _cellSlider.Value = Math.Max(_cellSlider.Minimum, Math.Min(_cellSlider.Maximum, _scene.GridPx));
                _gridThicknessSlider.Value = Math.Max(_gridThicknessSlider.Minimum,
                    Math.Min(_gridThicknessSlider.Maximum, _scene.GridThickness));

                string path = MapService.ResolveImagePath(_scene);
                if (path != null)
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path);
                    bitmap.EndInit();
                    bitmap.Freeze();

                    _mapImage.Source = bitmap;
                    _mapImage.Width = _scene.WidthPx;
                    _mapImage.Height = _scene.HeightPx;
                }
                else
                {
                    _mapImage.Source = null;
                    _mapImage.Width = 0;
                    _mapImage.Height = 0;
                }

                // Размер берём из файла: если карту заменили снаружи, сцена не
                // должна растягивать её до старого размера
                if (path != null)
                {
                    try
                    {
                        var decoder = BitmapDecoder.Create(new Uri(path),
                            BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                        int fileW = decoder.Frames[0].PixelWidth;
                        int fileH = decoder.Frames[0].PixelHeight;

                        if (fileW > 0 && fileH > 0 && (fileW != _scene.WidthPx || fileH != _scene.HeightPx))
                        {
                            _scene.WidthPx = fileW;
                            _scene.HeightPx = fileH;
                            MapService.SaveScene(_scene);
            PublishMap();
                            _mapImage.Width = fileW;
                            _mapImage.Height = fileH;
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[MapView] размер картинки: {ex.Message}");
                    }
                }

                _suppressFogEvents = true;
                try { _fogCheck.IsChecked = _scene.FogOn; }
                finally { _suppressFogEvents = false; }
                LoadSeenPoints(_scene.Id);
                BuildGrid();
                RenderStrokes();
                RenderShapes();
                RenderWalls();
                RebuildTokens();
                ApplyOrientation();

                _status.Text = path == null
                    ? (_isDm ? "Карта не загружена — «Открыть карту…»" : "Мастер ещё не загрузил карту")
                    : $"{_scene.Name}: {_scene.WidthPx}×{_scene.HeightPx}, жетонов {_scene.Tokens.Count}";

                // Смена сцены у ведущего должна доехать до игроков: раньше
                // карта уходила только при старте вкладки, и клиенты оставались
                // на прежней сцене даже после перезахода.
                if (_inRoom && !_remote) PublishMap();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] OpenScene: {ex}");
                _status.Text = "Ошибка открытия сцены: " + ex.Message;
            }
        }

        private void PickImage()
        {
            if (!_isDm) { DmRolls.Toast(this, "Карту загружает мастер", true); return; }
            if (_scene == null) { DmRolls.Toast(this, "Сначала создайте сцену кнопкой «+»", true); return; }

            var dialog = new OpenFileDialog
            {
                Title = "Карта боя",
                Filter = "Карта (*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.webm)|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.webm|Все файлы (*.*)|*.*"
            };
            if (dialog.ShowDialog() != true) return;

            if (MapService.ImportImage(_scene, dialog.FileName, out string error))
            {
                OpenScene(_scene);
                // Новая картинка сразу уходит игрокам: без этого они оставались
                // со старой до следующего изменения сцены
                PublishMap();
            }
            else
                DmRolls.Toast(this, "Не удалось открыть картинку: " + error, true);
        }

        private static bool _scenesDebugShown;

        private void OpenScenesWindow()
        {
            if (!_isDm) return;

            try
            {
                var window = new Windows.ScenesWindow(_characterId, _scene?.Id ?? 0, scene =>
                {
                    Refresh();
                    var found = _scenes.FirstOrDefault(s => s.Id == scene.Id);
                    if (found == null) return;
                    _sceneCombo.SelectedItem = found;
                    OpenScene(found);
                    PublishMap();
                })
                {
                    Owner = Window.GetWindow(this)
                };
                window.ShowDialog();
                Refresh();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Карта] окно сцен: {ex}");
                WebLog("scenes window error: " + ex);
                DmRolls.Toast(this, "Окно сцен: " + ex.Message, true);
            }
        }

        private void NewScene()
        {
            if (!_isDm) return;

            string name = Windows.NotePromptWindow.AskText(Window.GetWindow(this),
                "Новая сцена", "Название сцены", "Сцена " + (_scenes.Count + 1), "Создать");
            if (string.IsNullOrWhiteSpace(name)) return;

            var scene = MapService.CreateScene(_characterId, name);
            if (scene == null || scene.Id <= 0)
            {
                DmRolls.Toast(this, "Не удалось создать сцену", true);
                return;
            }

            Refresh();
            var created = _scenes.FirstOrDefault(s => s.Id == scene.Id);
            if (created != null) _sceneCombo.SelectedItem = created;
        }

        private void DeleteScene()
        {
            if (!_isDm || _scene == null) return;

            if (!Windows.MessageDialog.Confirm(this,
                    "Удалить сцену «" + _scene.Name + "» вместе с жетонами?", "Удаление сцены", "Удалить"))
                return;

            MapService.DeleteScene(_scene.Id);
            _scene = null;
            Refresh();
        }
    }
}
