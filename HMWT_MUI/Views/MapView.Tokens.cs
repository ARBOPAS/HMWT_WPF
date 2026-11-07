using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Жетоны, которых нет в трекере инициативы: НПС, предметы, мебель.
    ///
    /// Раньше любой жетон рождался из участника боя («Токены из боя»), поэтому
    /// «Старик» или бочка попадали в инициативу и путали порядок хода. Здесь
    /// создаётся жетон с CombatantId = 0: он живёт только на карте, двигает его
    /// ведущий, а у игроков он просто виден (или скрыт, если так решил мастер).
    /// </summary>
    public sealed partial class MapView
    {
        /// <summary>
        /// Окно «Жетон» и создание после подтверждения. atX/atY — координаты
        /// картинки, если жетон ставят правой кнопкой в конкретное место.
        /// </summary>
        private void AddFreeTokenFromDialog(double? atX = null, double? atY = null)
        {
            if (!_isDm || _scene == null) return;

            try
            {
                var window = new Windows.TokenEditorWindow { Owner = Window.GetWindow(this) };
                window.LoadValues("", null, 1, false);
                window.SetOkText("Поставить");

                if (window.ShowDialog() != true) return;

                CreateFreeToken(window.TokenLabel, window.PhotoFile, window.SizeCells,
                    window.IsHidden, atX, atY, window.SaveToLibrary);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] новый жетон: {ex}");
            }
        }

        /// <summary>
        /// Ставит жетон без участника боя. Картинка копируется в папку данных:
        /// WebGL отдаёт файлы только оттуда, иначе игроки её не увидят.
        /// </summary>
        private MapTokenData CreateFreeToken(string label, string photoFile, int sizeCells,
            bool hidden, double? atX, double? atY, bool saveToLibrary)
        {
            if (_scene == null) return null;
            if (!_isDm) return null;

            double cell = _scene.GridPx > 1 ? _scene.GridPx : 70;

            var token = new MapTokenData
            {
                SceneId = _scene.Id,
                CombatantId = 0,
                OwnerCharacterId = 0,
                Label = string.IsNullOrWhiteSpace(label) ? "Жетон" : label.Trim(),
                SizeCells = Math.Max(1, Math.Min(10, sizeCells)),
                Hidden = hidden,
                X = atX ?? (_scene.WidthPx > 0 ? _scene.WidthPx / 2.0 : cell * 4),
                Y = atY ?? (_scene.HeightPx > 0 ? _scene.HeightPx / 2.0 : cell * 4),
                // Обзор такому жетону не нужен: он не существо и туман не рассеивает.
                RevealCells = 0,
                ViewAngle = 360
            };

            string relative = MapTokenLibrary.Import(photoFile);
            if (!string.IsNullOrEmpty(relative))
            {
                token.PhotoPath = relative;
                string full = PhotoHelper.ResolvePhotoPath(relative);
                token.PhotoHash = string.IsNullOrEmpty(full) ? "" : HashFile(full);
            }

            if (MapService.SaveToken(token) <= 0)
            {
                Debug.WriteLine("[MapView] жетон не сохранился");
                return null;
            }

            if (saveToLibrary && !string.IsNullOrEmpty(relative))
            {
                string full = PhotoHelper.ResolvePhotoPath(relative);
                if (!string.IsNullOrEmpty(full))
                    MapTokenLibrary.Add(token.Label, full, (int)Math.Round(token.SizeCells));
            }

            _scene.Tokens.Add(token);
            PublishMap();
            RebuildTokens();
            PushWebMap();

            Debug.WriteLine($"[MapView] жетон «{token.Label}» добавлен (id={token.Id})");
            return token;
        }

        /// <summary>Библиотека готовых жетонов: выбрали — сразу ставим в центр карты.</summary>
        private void OpenTokenLibrary()
        {
            if (!_isDm || _scene == null) return;

            try
            {
                var window = new Windows.TokenLibraryWindow { Owner = Window.GetWindow(this) };
                if (window.ShowDialog() != true || window.Selected == null) return;

                var template = window.Selected;
                CreateFreeToken(template.Name, MapTokenLibrary.Resolve(template),
                    template.SizeCells, false, null, null, false);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] библиотека жетонов: {ex}");
            }
        }

        /// <summary>
        /// Правка жетона с карты: имя, картинка, размер и видимость в одном окне.
        /// Картинку берём из текущей — так видно, что меняешь.
        /// </summary>
        private void EditFreeToken(MapTokenData token)
        {
            if (!_isDm || token == null || _scene == null) return;

            try
            {
                var window = new Windows.TokenEditorWindow { Owner = Window.GetWindow(this) };
                window.LoadValues(token.Label, ResolveTokenPhoto(token),
                    (int)Math.Round(token.SizeCells <= 0 ? 1 : token.SizeCells), token.Hidden);
                window.SetOkText("Сохранить");

                if (window.ShowDialog() != true) return;

                string picked = window.PhotoFile;
                if (!string.IsNullOrEmpty(picked))
                {
                    string relative = MapTokenLibrary.Import(picked);
                    if (!string.IsNullOrEmpty(relative))
                    {
                        token.PhotoPath = relative;
                        string full = PhotoHelper.ResolvePhotoPath(relative);
                        token.PhotoHash = string.IsNullOrEmpty(full) ? "" : HashFile(full);
                    }
                }
                else
                {
                    // «Убрать» в окне: жетон остаётся, но без картинки.
                    token.PhotoPath = "";
                    token.PhotoHash = "";
                }

                token.Label = window.TokenLabel;
                token.SizeCells = window.SizeCells;
                token.Hidden = window.IsHidden;

                MapService.SaveToken(token);

                // Кэш «какая картинка уже отправлена» сбрасываем: иначе WebGL
                // не получит новый файл и оставит старый.
                _webTokenPhotoKeys.Remove(token.Id);

                PublishMap();
                RebuildTokens();
                PushWebMap();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] правка жетона: {ex}");
            }
        }

        /// <summary>Скрыть/показать жетон игрокам одним пунктом меню.</summary>
        private void ToggleTokenHidden(MapTokenData token)
        {
            if (!_isDm || token == null) return;

            token.Hidden = !token.Hidden;
            MapService.SaveToken(token);
            PublishMap();
            RebuildTokens();
            PushWebMap();
        }

        /// <summary>Жетон с карты убрать совсем (нужно библиотеке и меню).</summary>
        private void RemoveToken(MapTokenData token)
        {
            if (!_isDm || token == null || _scene == null) return;

            MapService.DeleteToken(token.Id);
            _scene.Tokens.RemoveAll(t => t.Id == token.Id);
            PublishMap();
            RebuildTokens();
            PushWebMap();
        }
    }
}
