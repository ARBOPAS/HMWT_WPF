using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Звук уведомлений: свой WAV из настроек или встроенный.
    ///
    /// Отдельный сервис, потому что им пользуется и комната, и вкладка
    /// настроек; держать SoundPlayer внутри RoomNotificationService значило
    /// тянуть в проверки интерфейса всё окно уведомлений.
    /// </summary>
    public static class NotificationSoundService
    {
        private static SoundPlayer _sound;
        private static string _loadedPath = "";

        public static void Reload()
        {
            try { _sound?.Dispose(); } catch { }
            _sound = null;
            _loadedPath = "";
        }

        public static void Preview() => Play(force: true);

        public static void Play(bool force = false)
        {
            try
            {
                if (!force && !UiPreferences.NotificationSoundEnabled) return;

                string custom = UiPreferences.NotificationSoundPath;
                string customFull = string.IsNullOrWhiteSpace(custom)
                    ? null
                    : (PhotoHelper.ResolvePhotoPath(custom) ??
                       (File.Exists(custom) ? custom : null));

                string wantedPath = customFull ?? "<embedded>";
                if (_sound == null || !string.Equals(_loadedPath, wantedPath, StringComparison.OrdinalIgnoreCase))
                {
                    Reload();

                    if (customFull != null)
                    {
                        _sound = new SoundPlayer(customFull);
                        _sound.Load();
                        _loadedPath = wantedPath;
                    }
                    else
                    {
                        var assembly = typeof(NotificationSoundService).Assembly;
                        string name = assembly.GetManifestResourceNames()
                            .FirstOrDefault(n => n.EndsWith("notify.wav", StringComparison.OrdinalIgnoreCase));

                        if (name != null)
                        {
                            using var stream = assembly.GetManifestResourceStream(name);
                            if (stream != null)
                            {
                                var buffer = new MemoryStream();
                                stream.CopyTo(buffer);
                                buffer.Position = 0;

                                _sound = new SoundPlayer(buffer);
                                _sound.Load();
                                _loadedPath = wantedPath;
                            }
                        }
                    }
                }

                if (_sound != null) _sound.Play();
                else SystemSounds.Asterisk.Play();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[NotificationSound] {ex.Message}");
                try { SystemSounds.Asterisk.Play(); } catch { }
            }
        }
    }
}
