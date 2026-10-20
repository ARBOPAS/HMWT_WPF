using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using System.Windows;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Windows;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Уведомления отряда: бросок или сообщение всплывает карточкой со звуком,
    /// если игрок сейчас не смотрит на вкладку «Отряд».
    ///
    /// Подписка живёт на уровне приложения, а не вкладки: вкладку «Отряд»
    /// можно закрыть, свернуть окно в трей — уведомления всё равно придут,
    /// пока приложение запущено.
    /// </summary>
    public static class RoomNotificationService
    {
        private sealed class Notice
        {
            public string Heading;
            public string Author;
            public string Text;
            public string Detail;
        }

        private static bool _initialized;
        private static NotificationWindow _current;
        private static SoundPlayer _sound;

        public static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;

            RoomSession.Instance.FeedReceived += OnFeedReceived;
        }

        public static void Shutdown()
        {
            if (!_initialized) return;
            _initialized = false;

            try { RoomSession.Instance.FeedReceived -= OnFeedReceived; } catch { }

            var current = _current;
            _current = null;
            try { current?.Close(); } catch { }
        }

        private static void OnFeedReceived(FeedEntry entry)
        {
            if (entry == null) return;

            var app = Application.Current;
            if (app == null) return;

            if (app.Dispatcher.CheckAccess())
                Handle(entry);
            else
                app.Dispatcher.BeginInvoke(new Action(() => Handle(entry)));
        }

        private static void Handle(FeedEntry entry)
        {
            var session = RoomSession.Instance;
            if (!session.IsActive) return;

            // Свои сообщения и броски не уведомляем
            if (!string.IsNullOrEmpty(entry.AuthorId) &&
                string.Equals(entry.AuthorId, session.GetMyPlayerId(), StringComparison.Ordinal))
                return;

            // Скрытые броски ведущего и системные записи — не для всплывашек
            if (entry.Hidden) return;
            if (entry.Kind != FeedKind.Chat && entry.Kind != FeedKind.Roll) return;

            // История, пришедшая при входе, не должна всплывать пачкой
            if (Math.Abs((DateTime.Now - entry.At).TotalMinutes) > 5) return;

            // Если игрок прямо сейчас смотрит на ленту отряда — не мешаем
            if (IsRoomFeedActive()) return;

            var notice = entry.Kind == FeedKind.Roll
                ? new Notice
                {
                    Heading = "Бросок",
                    Author = entry.AuthorName,
                    Text = string.IsNullOrWhiteSpace(entry.Text) ? "Бросок" : entry.Text,
                    Detail = BuildRollDetail(entry)
                }
                : new Notice
                {
                    Heading = "Сообщение",
                    Author = entry.AuthorName,
                    Text = entry.Text ?? "",
                    Detail = ""
                };

            if (string.IsNullOrWhiteSpace(notice.Text)) return;

            Show(notice);
        }

        private static string BuildRollDetail(FeedEntry entry)
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(entry.Formula)) parts.Add(entry.Formula.Trim());
            parts.Add("итог " + entry.Total);

            return string.Join(" · ", parts);
        }

        private static bool IsRoomFeedActive()
        {
            var app = Application.Current;
            if (app == null) return false;

            return app.Windows.OfType<CharacterInfoWindow>()
                .Any(w => w.IsActive && w.IsRoomViewVisible);
        }

        /// <summary>
        /// Показывает уведомление сразу, закрывая предыдущее: важно само
        /// событие, а не очередь. Три быстрых броска — три вспышки подряд,
        /// последняя остаётся на экране.
        /// </summary>
        private static void Show(Notice notice)
        {
            var previous = _current;
            _current = null;

            try { previous?.Close(); } catch { }

            var window = new NotificationWindow(notice.Heading, notice.Author, notice.Text, notice.Detail);
            _current = window;

            window.Closed += (s, e) =>
            {
                // Старое окно, закрытое новым, не должно обнулять новое
                if (ReferenceEquals(_current, window)) _current = null;
            };

            window.Clicked += OpenRoom;

            PlaySound();
            window.Show();
        }

        /// <summary>Короткий «дзынь» из встроенного wav, с запасным системным звуком.</summary>
        private static void PlaySound()
        {
            try
            {
                if (_sound == null)
                {
                    var assembly = typeof(RoomNotificationService).Assembly;
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
                        }
                    }
                }

                if (_sound != null) _sound.Play();
                else SystemSounds.Asterisk.Play();
            }
            catch
            {
                try { SystemSounds.Asterisk.Play(); } catch { }
            }
        }

        private static void OpenRoom()
        {
            try
            {
                var character = Application.Current?.Windows.OfType<CharacterInfoWindow>()
                    .FirstOrDefault(w => w.IsLoaded);

                if (character != null)
                {
                    character.ShowRoomTab();
                    return;
                }

                var main = Application.Current?.MainWindow;
                if (main == null) return;

                if (main is MainWindow window)
                {
                    window.RestoreFromTray();
                }
                else
                {
                    main.Show();
                    if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
                    main.Activate();
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[RoomNotification] открыть отряд: {ex.Message}");
            }
        }
    }
}
