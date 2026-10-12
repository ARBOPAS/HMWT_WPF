using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HMWT_MUI.Helpers;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Масштаб интерфейса для всех окон приложения.
    ///
    /// Почему отдельный менеджер: масштаб нужно применять не только к главному окну,
    /// но и к окну персонажа, заклинаний, инвентаря и остальным. Окно регистрируется
    /// здесь, а менеджер следит, чтобы у всех был одинаковый масштаб, и рассылает
    /// изменения уже открытым окнам.
    ///
    /// Масштаб применяется через LayoutTransform: размеры пересчитываются, поэтому
    /// вёрстка действительно влезает, а не просто рисуется мельче.
    /// </summary>
    public static class UiScaleManager
    {
        private const string ScaleKey = "UiScalePercent";

        /// <summary>Размер, на который рассчитана вёрстка.</summary>
        private const double DesignWidth = 1280;
        private const double DesignHeight = 800;

        /// <summary>Открытые окна: само окно и его трансформация.</summary>
        private static readonly List<(WeakReference Window, ScaleTransform Transform)> Registered =
            new List<(WeakReference, ScaleTransform)>();

        private static int _currentPercent = 100;

        /// <summary>Текущий масштаб в процентах.</summary>
        public static int CurrentPercent => _currentPercent;

        /// <summary>
        /// Применяет масштаб из настроек. Если настройки нет, подбирает по экрану.
        /// Вызывается один раз при запуске приложения.
        /// </summary>
        public static void Initialize()
        {
            int percent = 0;
            try
            {
                string saved = DatabaseHelper.GetSetting(ScaleKey, "");
                if (!string.IsNullOrWhiteSpace(saved)) int.TryParse(saved, out percent);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UiScale] чтение настройки: {ex.Message}");
            }

            if (percent <= 0) percent = GuessForScreen();

            // Сохраняем, чтобы следующее окно сразу получило то же значение
            _currentPercent = Clamp(percent);
        }

        /// <summary>
        /// Подбирает масштаб по экрану.
        ///
        /// Только уменьшает и никогда не превышает 100%: вёрстка рассчитана на
        /// 1280×800, и растягивать её на большом мониторе не нужно — там и так
        /// всё помещается. А вот на 1366×768 надо сжимать.
        ///
        /// Ограничение сверху важно и технически: раньше на Full HD (1920×1080)
        /// коэффициент выходил 1,3, интерфейс растягивался до 130 %, и логического
        /// места оставалось ровно столько же, сколько на 1477×800 — из-за этого
        /// вкладка «Инвентарь» переставала помещаться и содержимое обрезалось.
        /// Кому нужно крупнее — тот выставит масштаб ползунком в настройках.
        /// </summary>
        public static int GuessForScreen()
        {
            try
            {
                double width = SystemParameters.WorkArea.Width;
                double height = SystemParameters.WorkArea.Height;

                double factor = Math.Min(width / DesignWidth, height / DesignHeight);
                int percent = (int)(Math.Floor(factor * 100.0 / 5.0) * 5);

                // На большом экране не увеличиваем: см. пояснение выше
                if (percent > 100) percent = 100;

                return Clamp(percent);
            }
            catch
            {
                return 100;
            }
        }

        private static int Clamp(int percent) => Math.Max(70, Math.Min(150, percent));

        /// <summary>
        /// Регистрирует окно: применяет к нему текущий масштаб и подписывает
        /// на дальнейшие изменения.
        /// </summary>
        /// <param name="window">Окно.</param>
        /// <param name="transform">
        /// Готовая трансформация из разметки (если есть). Если не передана,
        /// менеджер сам обернёт содержимое окна.
        /// </param>
        public static void Register(Window window, ScaleTransform transform = null)
        {
            if (window == null) return;

            try
            {
                if (transform == null) transform = AttachTransform(window);
                if (transform == null) return;

                ApplyTo(transform, _currentPercent);

                // Убираем закрытые окна, чтобы список не рос
                PruneClosed();
                Registered.Add((new WeakReference(window), transform));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[UiScale] регистрация окна: {ex.Message}");
            }
        }

        /// <summary>
        /// Оборачивает содержимое окна в контейнер с LayoutTransform.
        /// Нужно для окон, где такой обёртки нет в разметке.
        /// </summary>
        private static ScaleTransform AttachTransform(Window window)
        {
            // Уже обёрнуто нами — второй раз не надо
            if (window.Tag is ScaleTransform existing) return existing;

            var content = window.Content as UIElement;
            if (content == null) return null;

            window.Content = null;

            var host = new Grid { LayoutTransform = new ScaleTransform(1, 1) };
            host.Children.Add(content);
            window.Content = host;

            var transform = (ScaleTransform)host.LayoutTransform;
            window.Tag = transform;
            return transform;
        }

        /// <summary>Устанавливает масштаб для всех открытых окон и сохраняет его.</summary>
        public static void Apply(int percent, bool save)
        {
            _currentPercent = Clamp(percent);

            PruneClosed();
            foreach (var (reference, transform) in Registered)
            {
                ApplyTo(transform, _currentPercent);
            }

            if (save)
            {
                try { DatabaseHelper.SaveSetting(ScaleKey, _currentPercent.ToString()); }
                catch (Exception ex) { Debug.WriteLine($"[UiScale] сохранение: {ex.Message}"); }
            }
        }

        private static void ApplyTo(ScaleTransform transform, int percent)
        {
            if (transform == null) return;

            double factor = percent / 100.0;
            transform.ScaleX = factor;
            transform.ScaleY = factor;

            // ScaleTransform не является Freezable-замороженным, поэтому менять можно
            Debug.WriteLine($"[UiScale] применён {percent}%");
        }

        /// <summary>
        /// Убирает из списка закрытые окна. Слабая ссылка не даёт менеджеру
        /// удерживать окна в памяти после закрытия.
        /// </summary>
        private static void PruneClosed()
        {
            for (int i = Registered.Count - 1; i >= 0; i--)
            {
                if (!Registered[i].Window.IsAlive) Registered.RemoveAt(i);
            }
        }
    }
}
