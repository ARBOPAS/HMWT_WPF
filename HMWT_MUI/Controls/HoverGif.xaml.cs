using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMWT_MUI.Services;

namespace HMWT_MUI.Controls
{
    /// <summary>
    /// Картинка, которая показывает статичный первый кадр, а анимируется только
    /// при наведении курсора.
    ///
    /// Зачем так: полный набор кадров GIF занимает десятки мегабайт — декодер
    /// делает отдельный полноразмерный буфер на каждый кадр. Если держать такие
    /// наборы для всех плиток сразу, приложение раздувается до гигабайтов. Поэтому
    /// пока курсор не наведён, в памяти лежит один кадр, а полный набор грузится
    /// на время анимации и сразу освобождается.
    ///
    /// Размеры гифок при этом можно не ограничивать: платим только за ту, на
    /// которую смотрим.
    /// </summary>
    public partial class HoverGif : UserControl
    {
        private GifAnimator _animator;
        private string _loadedPath;
        private bool _isHovered;

        /// <summary>Полный путь к файлу изображения. Пусто — показывается пустая рамка.</summary>
        public string SourcePath { get; private set; }

        /// <summary>Показывать ли анимацию при наведении. Для одиночных картинок выключается.</summary>
        public bool AnimateOnHover { get; set; } = true;

        /// <summary>
        /// Ширина, в которой картинка показывается.
        ///
        /// Кадры разбираются сразу в этом размере — иначе гифка 800×450, показанная
        /// в квадрате 40×40, занимала бы в четыреста раз больше памяти и времени.
        /// 0 — брать фактическую ширину элемента.
        /// </summary>
        public int DisplayWidth { get; set; }

        /// <summary>Растяжение картинки внутри плитки.</summary>
        public Stretch Stretch
        {
            get => imgFrame.Stretch;
            set => imgFrame.Stretch = value;
        }

        /// <summary>Скругление углов (0 — без скругления).</summary>
        public double CornerRadius { get; set; }

        public HoverGif()
        {
            InitializeComponent();
            Unloaded += (s, e) => ReleaseAnimation();
        }

        /// <summary>
        /// Задаёт картинку. Принимает как полный путь, так и относительный
        /// (тогда ищется в папке фотографий приложения).
        /// </summary>
        public void SetImage(string pathOrRelative)
        {
            ReleaseAnimation();
            _loadedPath = null;
            SourcePath = pathOrRelative;

            if (string.IsNullOrWhiteSpace(pathOrRelative))
            {
                imgFrame.Source = null;
                return;
            }

            string full = Path.IsPathRooted(pathOrRelative)
                ? pathOrRelative
                : HMWT_MUI.PhotoHelper.ResolvePhotoPath(pathOrRelative);

            if (string.IsNullOrEmpty(full) || !File.Exists(full))
            {
                imgFrame.Source = null;
                return;
            }

            _loadedPath = full;
            ApplyClip();

            // Всегда начинаем со статичного кадра: он лёгкий и нужен в списках
            imgFrame.Source = GifFrameCache.GetFirstFrame(full);
        }

        /// <summary>Задаёт уже готовое изображение (например, из памяти).</summary>
        public void SetImageSource(BitmapSource source)
        {
            ReleaseAnimation();
            _loadedPath = null;
            SourcePath = null;
            imgFrame.Source = source;
        }

        /// <summary>
        /// Убирает картинку. Нужен там, где раньше присваивали Image.Source = null:
        /// у файла может не быть арта, и тогда показывается заглушка.
        /// </summary>
        public void ClearImage()
        {
            ReleaseAnimation();
            _loadedPath = null;
            SourcePath = null;
            imgFrame.Source = null;
        }

        /// <summary>Есть ли что показывать.</summary>
        public bool HasImage => imgFrame.Source != null;

        private void ApplyClip()
        {
            if (CornerRadius <= 0) { imgFrame.Clip = null; return; }

            imgFrame.Clip = new RectangleGeometry(
                new Rect(0, 0, imgFrame.ActualWidth, imgFrame.ActualHeight),
                CornerRadius, CornerRadius);
        }

        #region === НАВЕДЕНИЕ: АНИМАЦИЯ ТОЛЬКО ЗДЕСЬ ===

        protected override void OnMouseEnter(MouseEventArgs e)
        {
            base.OnMouseEnter(e);
            _isHovered = true;
            if (AnimateOnHover) StartAnimation();
        }

        protected override void OnMouseLeave(MouseEventArgs e)
        {
            base.OnMouseLeave(e);
            _isHovered = false;
            ReleaseAnimation();
        }

        private void StartAnimation()
        {
            if (_animator != null || string.IsNullOrEmpty(_loadedPath)) return;

            try
            {
                var gif = GifFrameCache.Get(_loadedPath, ResolveDisplayWidth());
                if (gif == null || !gif.IsAnimated)
                {
                    // Не анимация — оставляем статичный кадр
                    GifFrameCache.Release(_loadedPath);
                    return;
                }

                _animator = new GifAnimator(gif.Frames, gif.Delays, frame => imgFrame.Source = frame);
                _animator.Start();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HoverGif] запуск анимации: {ex.Message}");
                _animator = null;
            }
        }

        /// <summary>
        /// В каком размере разбирать кадры: заданное явно, иначе фактическая ширина.
        /// Ограничиваем разумным максимумом, чтобы полноэкранная картинка не
        /// разбиралась в огромном разрешении.
        /// </summary>
        private int ResolveDisplayWidth()
        {
            if (DisplayWidth > 0) return DisplayWidth;

            double width = ActualWidth;
            if (width <= 0) return 256;

            return (int)Math.Min(1024, Math.Ceiling(width));
        }

        /// <summary>
        /// Останавливает анимацию и освобождает кадры.
        ///
        /// Освобождение обязательно: иначе полный набор останется в памяти
        /// до выхода из приложения, и смысл «анимации по наведению» потеряется.
        /// </summary>
        private void ReleaseAnimation()
        {
            bool wasAnimating = _animator != null;

            if (_animator != null)
            {
                try { _animator.Stop(); } catch { }
                _animator = null;
            }

            if (!string.IsNullOrEmpty(_loadedPath))
            {
                // Возвращаем статичный кадр: он остаётся в лёгком кэше
                var first = GifFrameCache.GetFirstFrame(_loadedPath);
                if (first != null) imgFrame.Source = first;

                if (wasAnimating) GifFrameCache.Release(_loadedPath);
            }
        }

        #endregion

        #region === РАЗМЕРЫ ===

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);
            ApplyClip();
        }

        #endregion
    }
}
