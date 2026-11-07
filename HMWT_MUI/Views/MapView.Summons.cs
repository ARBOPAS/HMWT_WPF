using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Призывы на карте: игрок ставит жетон своего существа и двигает его сам.
    ///
    /// У ведущего жетон создаётся сразу. У игрока — через комнату: сцену
    /// ведёт хост, и записать жетон в базу может только он. Владельцем
    /// записывается персонаж игрока, поэтому сервер разрешает ему движение
    /// (и запрещает трогать чужие жетоны).
    /// </summary>
    public sealed partial class MapView
    {
        private const int MaxSummonPhotoBytes = 6 * 1024 * 1024;

        private void OpenSummonLibrary()
        {
            if (_scene == null)
            {
                DmRolls.Toast(this, "Сначала откройте сцену", true);
                return;
            }

            // У игрока призывы принадлежат его персонажу, у ведущего — тому,
            // чей лист открыт в приложении
            int characterId = _remote && SelfCharacterId > 0
                ? SelfCharacterId
                : (_characterId > 0 ? _characterId : SelfCharacterId);
            if (characterId <= 0)
            {
                DmRolls.Toast(this, "Сначала выберите персонажа", true);
                return;
            }

            try
            {
                var window = new Windows.SummonWindow(characterId) { Owner = Window.GetWindow(this) };
                if (window.ShowDialog() != true || window.Result == null) return;

                var summon = window.Result;
                string photo = PhotoHelper.ResolvePhotoPath(summon.PhotoPath);

                if (_remote)
                {
                    SendSummonToHost(summon, photo);
                    return;
                }

                CreateSummonToken(summon, characterId, photo);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] призывы: {ex}");
                DmRolls.Toast(this, "Не удалось открыть призывы: " + ex.Message, true);
            }
        }

        /// <summary>Жетон призыва на своей карте: ведущий ставит его сразу.</summary>
        private void CreateSummonToken(SummonData summon, int characterId, string photoFull)
        {
            if (_scene == null || summon == null) return;

            double cell = _scene.GridPx > 1 ? _scene.GridPx : 70;

            var token = new MapTokenData
            {
                SceneId = _scene.Id,
                CombatantId = 0,
                OwnerCharacterId = characterId,
                Label = string.IsNullOrWhiteSpace(summon.Name) ? "Призыв" : summon.Name.Trim(),
                SizeCells = Math.Max(0.5, Math.Min(10, summon.SizeCells)),
                Hidden = false,
                X = _scene.WidthPx > 0 ? _scene.WidthPx / 2.0 : cell * 4,
                Y = _scene.HeightPx > 0 ? _scene.HeightPx / 2.0 : cell * 4,
                RevealCells = 0,
                ViewAngle = 360
            };

            string relative = MapTokenLibrary.Import(photoFull);
            if (!string.IsNullOrEmpty(relative))
            {
                token.PhotoPath = relative;
                string full = PhotoHelper.ResolvePhotoPath(relative);
                token.PhotoHash = string.IsNullOrEmpty(full) ? "" : HashFile(full);
            }

            if (MapService.SaveToken(token) <= 0)
            {
                DmRolls.Toast(this, "Жетон призыва не сохранился", true);
                return;
            }

            _scene.Tokens.Add(token);
            RebuildTokens();
            PublishMap();
            DmRolls.Toast(this, $"«{token.Label}» на карте — двигайте его сами");
        }

        /// <summary>Игрок: отправляем лист и картинку хосту, он создаст жетон.</summary>
        private void SendSummonToHost(SummonData summon, string photoFull)
        {
            var request = new HMWT_MUI.Net.MapSummonRequest
            {
                Label = summon.Name ?? "",
                CreatureType = summon.CreatureType ?? "",
                SizeCells = summon.SizeCells,
                Hp = summon.Hp,
                MaxHp = summon.MaxHp,
                Ac = summon.Ac,
                Speed = summon.Speed ?? "",
                Attacks = summon.Attacks ?? "",
                Abilities = summon.Abilities ?? ""
            };

            // Картинка едет вместе с листом: у хоста нет файла игрока
            if (!string.IsNullOrEmpty(photoFull))
            {
                try
                {
                    var info = new FileInfo(photoFull);
                    if (info.Length > 0 && info.Length <= MaxSummonPhotoBytes)
                    {
                        request.Extension = Path.GetExtension(photoFull) ?? ".png";
                        request.PhotoBase64 = Convert.ToBase64String(File.ReadAllBytes(photoFull));
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[MapView] картинка призыва: {ex.Message}");
                }
            }

            if (HMWT_MUI.Net.RoomSession.Instance.PlaceSummon(request))
                DmRolls.Toast(this, "Призыв отправлен мастеру");
            else
                DmRolls.Toast(this, "Нет связи с мастером — призыв не поставлен", true);
        }

        /// <summary>
        /// Ведущий получил призыв от игрока: сервер уже создал жетон в базе,
        /// нужно перечитать сцену, иначе в окне ведущего он не появится.
        /// </summary>
        private void OnSummonAdded(MapTokenData token)
        {
            if (!_isDm || token == null) return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnSummonAdded(token)));
                return;
            }

            if (_scene == null || token.SceneId != _scene.Id) return;

            try
            {
                _scene = MapService.LoadScene(_scene.Id) ?? _scene;
                RebuildTokens();
                PushWebMap();
                DmRolls.Toast(this, $"Игрок поставил призыв «{token.Label}»");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[MapView] призыв от игрока: {ex.Message}");
            }
        }
    }
}
