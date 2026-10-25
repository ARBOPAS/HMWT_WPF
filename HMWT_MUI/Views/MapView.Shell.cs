using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Net;
using HMWT_MUI.Services;

namespace HMWT_MUI.Views
{
    /// <summary>
    /// Построение панели карты и её кнопок. Вынесено из MapView.cs (partial),
    /// чтобы разметка не жила в одном файле с логикой сцен и жетонов.
    /// </summary>
    public sealed partial class MapView
    {
        private void BuildShell()
        {
            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var bar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };

            // Вторая строка — инструменты мастера. У игрока её нет совсем,
            // а у ведущего она свёрнута: иначе панель занимает пол-экрана.
            var dmBar = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
            dmBar.Visibility = Visibility.Collapsed;
            bar.Children.Add(DmUiKit.Title("КАРТА"));

            var open = DmUiKit.Action("Открыть карту…", (s, e) => PickImage(), DmUiKit.ButtonKind.Primary,
                "Загрузить PNG или JPEG в текущую сцену", 150, 30);
            open.Margin = new Thickness(14, 0, 8, 0);
            open.IsEnabled = _isDm;
            dmBar.Children.Add(open);

            _sceneCombo.Height = 30;
            _sceneCombo.Margin = new Thickness(0, 0, 8, 0);
            _sceneCombo.SelectionChanged += (s, e) =>
            {
                if (_sceneCombo.SelectedItem is MapSceneData scene && (_scene == null || scene.Id != _scene.Id))
                    OpenScene(scene);
            };
            bar.Children.Add(_sceneCombo);

            var scenesWindow = DmUiKit.Action("Сцены…", (s, e) => OpenScenesWindow(),
                DmUiKit.ButtonKind.Ghost, "Все сцены по кампаниям", 96, 30);
            scenesWindow.Margin = new Thickness(0, 0, 8, 0);
            scenesWindow.Visibility = _isDm ? Visibility.Visible : Visibility.Collapsed;
            bar.Children.Add(scenesWindow);

            var newScene = DmUiKit.Icon("+", (s, e) => NewScene(), "Новая сцена", 30);
            newScene.IsEnabled = _isDm;
            newScene.Margin = new Thickness(0, 0, 6, 0);
            dmBar.Children.Add(newScene);

            var delScene = DmUiKit.Icon("✕", (s, e) => DeleteScene(), "Удалить текущую сцену", 30, DmUiKit.ButtonKind.Soft);
            delScene.IsEnabled = _isDm;
            delScene.Margin = new Thickness(0, 0, 10, 0);
            dmBar.Children.Add(delScene);

            // Аудиотека: может открыть любой участник — иногда лучший диджей
            // не ведущий, а игрок.
            var audio = DmUiKit.Action("Аудио", (s, e) =>
            {
                var window = new Windows.AudioWindow { Owner = Window.GetWindow(this) };
                window.Show();
            }, DmUiKit.ButtonKind.Ghost, "Музыка, эмбиент и звуки — для всех участников", 86, 30);
            audio.Margin = new Thickness(12, 0, 8, 0);
            bar.Children.Add(audio);

            // ── сетка: включение, размер клетки, цвет ──
            _gridCheck.Margin = new Thickness(0, 0, 8, 0);
            _gridCheck.IsEnabled = _isDm;
            _gridCheck.Visibility = _isDm ? Visibility.Visible : Visibility.Collapsed;
            _gridCheck.Checked += (s, e) => { if (_scene != null) { _scene.GridOn = true; MapService.SaveScene(_scene); PublishMap(); BuildGrid(); } };
            _gridCheck.Unchecked += (s, e) => { if (_scene != null) { _scene.GridOn = false; MapService.SaveScene(_scene); PublishMap(); BuildGrid(); } };
            bar.Children.Add(_gridCheck);

            _snapCheck.Margin = new Thickness(0, 0, 8, 0);
            _snapCheck.IsEnabled = _isDm;
            _snapCheck.Visibility = _isDm ? Visibility.Visible : Visibility.Collapsed;
            _snapCheck.ToolTip = "Привязывать жетоны к клеткам. Сетку можно оставить видимой и двигаться свободно.";
            _snapCheck.Checked += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                _scene.SnapToGrid = true;
                MapService.SaveScene(_scene);
                PublishMap();
            };
            _snapCheck.Unchecked += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                _scene.SnapToGrid = false;
                MapService.SaveScene(_scene);
                PublishMap();
            };
            bar.Children.Add(_snapCheck);

            _showPlayersCheck.Margin = new Thickness(0, 0, 8, 0);
            _showPlayersCheck.ToolTip = "Показывать жетоны игроков даже вне вашего обзора";
            _showPlayersCheck.Checked += (s, e) => { RefreshTokenVisibility(); PushWebMap(); };
            _showPlayersCheck.Unchecked += (s, e) => { RefreshTokenVisibility(); PushWebMap(); };
            bar.Children.Add(_showPlayersCheck);

            _cellSlider.Margin = new Thickness(0, 0, 6, 0);
            _cellSlider.VerticalAlignment = VerticalAlignment.Center;
            _cellSlider.ToolTip = "Размер клетки: тяните, чтобы подогнать под сетку карты";
            _cellSlider.IsEnabled = _isDm;
            _cellSlider.ValueChanged += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                if (Math.Abs(_scene.GridPx - _cellSlider.Value) < 0.5) return;

                _scene.GridPx = _cellSlider.Value;
                MapService.SaveScene(_scene);
                PublishMap();
                _cellField.Text = ((int)Math.Round(_cellSlider.Value)).ToString();
                BuildGrid();
                RebuildTokens();
                BuildFogMask();
            };
            dmBar.Children.Add(_cellSlider);

            // Толщина линий сетки: на светлых картах тонкая линия почти не видна.
            _gridThicknessSlider.Margin = new Thickness(0, 0, 8, 0);
            _gridThicknessSlider.VerticalAlignment = VerticalAlignment.Center;
            _gridThicknessSlider.ToolTip = "Толщина линий сетки";
            _gridThicknessSlider.IsEnabled = _isDm;
            _gridThicknessSlider.ValueChanged += (s, e) =>
            {
                if (_scene == null || !_isDm) return;
                double value = Math.Max(0.5, Math.Min(4, _gridThicknessSlider.Value));
                if (Math.Abs(_scene.GridThickness - value) < 0.01) return;

                _scene.GridThickness = value;
                MapService.SaveScene(_scene);
                PublishMap();
                BuildGrid();
            };
            dmBar.Children.Add(_gridThicknessSlider);

            _cellField.Width = 58;
            _cellField.Margin = new Thickness(0, 0, 4, 0);
            _cellField.IsEnabled = _isDm;
            _cellField.ToolTip = "Размер клетки в точках картинки: подгоните под готовую сетку карты";
            _cellField.KeyDown += (s, e) => { if (e.Key == Key.Enter) { ApplyGridSize(); e.Handled = true; } };
            _cellField.LostFocus += (s, e) => ApplyGridSize();
            dmBar.Children.Add(_cellField);

            var gridColor = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Цвет сетки",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                Background = DmUiKit.B("Ink")
            };
            gridColor.Content = swatch;
            gridColor.Template = FlatButtonTemplate();
            gridColor.IsEnabled = _isDm;
            gridColor.Click += (s, e) => OpenGridColorMenu(gridColor, swatch);
            dmBar.Children.Add(gridColor);

            bar.Children.Add(DmUiKit.Action("Вписать", (s, e) => FitToWindow(), DmUiKit.ButtonKind.Ghost,
                "Показать карту целиком", 90, 30));
            bar.Children.Add(DmUiKit.Icon("−", (s, e) => ZoomAtCenter(1 / 1.2), "Отдалить", 30, DmUiKit.ButtonKind.Soft));
            bar.Children.Add(DmUiKit.Icon("+", (s, e) => ZoomAtCenter(1.2), "Приблизить", 30, DmUiKit.ButtonKind.Soft));

            // ── ориентация вида: файл не трогаем, поворачиваем показ ──
             // поворот и отражение карты в WebGL не поддержаны — кнопки убраны

            _fogCheck.Margin = new Thickness(0, 0, 10, 0);
            _fogCheck.IsEnabled = _isDm;
            _fogCheck.Visibility = _isDm ? Visibility.Visible : Visibility.Collapsed;
            _fogCheck.Checked += (s, e) => { if (!_suppressFogEvents) SetFog(true); };
            _fogCheck.Unchecked += (s, e) => { if (!_suppressFogEvents) SetFog(false); };
            bar.Children.Add(_fogCheck);

            // ── фигуры областей ──
            _shapeButton = DmUiKit.Action("Фигура…", (s, e) => OpenShapeMenu(_shapeButton), DmUiKit.ButtonKind.Ghost,
                "Круг, квадрат, конус или линия: выберите тип, затем щёлкните по карте", 96, 30);
            _shapeButton.Margin = new Thickness(0, 0, 10, 0);
            _shapeButton.IsEnabled = _isDm || _remote;
            dmBar.Children.Add(_shapeButton);

            _shapeField.Margin = new Thickness(0, 0, 10, 0);
            _shapeField.VerticalAlignment = VerticalAlignment.Center;
            _shapeField.ToolTip = "Размер новой фигуры в клетках (число)";
            _shapeField.IsEnabled = _isDm || _remote;
            dmBar.Children.Add(_shapeField);

            Button trailColor = null;
            trailColor = DmUiKit.Action("Цвет следа", (s, e) => OpenTrailColorMenu(trailColor),
                DmUiKit.ButtonKind.Ghost, "Цвет линии, остающейся за жетоном", 110, 30);
            trailColor.Margin = new Thickness(0, 0, 10, 0);
            trailColor.IsEnabled = _isDm;
            dmBar.Children.Add(trailColor);

            // ── линейка: протяните мышью и увидите клетки/футы ──
            _measureCheck.Margin = new Thickness(0, 0, 10, 0);
            _measureCheck.ToolTip = "Протяните мышью по карте — покажет расстояние в клетках и футах";
            _measureCheck.Checked += (s, e) =>
            {
                _drawCheck.IsChecked = false;
                _wallCheck.IsChecked = false;
                _pingCheck.IsChecked = false;
                _pendingShape = null;
                ClearMeasure();
                _viewport.Cursor = Cursors.Cross;
            };
            _measureCheck.Unchecked += (s, e) =>
            {
                ClearMeasure();
                _viewport.Cursor = Cursors.Arrow;
            };
            bar.Children.Add(_measureCheck);

            // ── пинг: точка, которую видят все в комнате ──
            // Чекбокс убран: пинг ставится клавишей TAB, режим не нужен.
            _pingCheck.Margin = new Thickness(0, 0, 10, 0);
            _pingCheck.ToolTip = "Щёлкните по карте — все увидят пульс в этом месте";
            _pingCheck.Checked += (s, e) =>
            {
                _drawCheck.IsChecked = false;
                _wallCheck.IsChecked = false;
                _measureCheck.IsChecked = false;
                _pendingShape = null;
                _viewport.Cursor = Cursors.Cross;
            };
            _pingCheck.Unchecked += (s, e) => _viewport.Cursor = Cursors.Arrow;
            _pingCheck.Visibility = Visibility.Collapsed;

            // ── стены: ломают обзор, за ними туман не рассеивается ──
            // ── дальность обзора выделенного жетона ──
            _revealLabel.Text = "Обзор";
            _revealLabel.Foreground = DmUiKit.B("Mute");
            _revealLabel.VerticalAlignment = VerticalAlignment.Center;
            _revealLabel.Margin = new Thickness(0, 0, 4, 0);
            bar.Children.Add(_revealLabel);

            _revealSlider.Margin = new Thickness(0, 0, 10, 0);
            _revealSlider.VerticalAlignment = VerticalAlignment.Center;
            _revealSlider.ToolTip = "Дальность обзора выделенного жетона, клетки";
            _revealSlider.IsEnabled = _isDm || _remote;
            _revealSlider.ValueChanged += (s, e) =>
            {
                if (_suppressRevealEvents) return;
                if (_scene == null) return;
                var selected = _scene.Tokens.FirstOrDefault(x => x.Id == _selectedTokenId);
                if (selected == null) return;
                if (_remote && !CanMoveToken(selected)) return;
                if (Math.Abs(selected.RevealCells - _revealSlider.Value) < 0.5) return;

                selected.RevealCells = _revealSlider.Value;

                if (_isDm)
                {
                    MapService.SaveToken(selected);
                    PublishMap();
                }
                else
                {
                    // Игрок меняет обзор своего жетона: хост применяет и рассылает
                    RoomSession.Instance.SendMapMove(selected.Id, selected.X, selected.Y,
                        selected.Facing, selected.ViewAngle, (int)selected.SizeCells,
                        selected.RevealCells);
                }

                RenderFov();
                RememberCurrentVision();
                BuildFogMask();
                PushWebMap();
            };
            bar.Children.Add(_revealSlider);

            _wallCheck.Margin = new Thickness(0, 0, 10, 0);
            _wallCheck.IsEnabled = _isDm;
            _wallCheck.ToolTip = "Тяните мышью по карте, чтобы нарисовать стену. ПКМ по стене — убрать.";
            _wallCheck.Checked += (s, e) => { _wallStart = null; _viewport.Cursor = Cursors.Cross; };
            _wallCheck.Unchecked += (s, e) => { _wallStart = null; _viewport.Cursor = Cursors.Arrow; };
            dmBar.Children.Add(_wallCheck);

            // ── кисть ──
            _drawCheck.Margin = new Thickness(0, 0, 6, 0);
            _drawCheck.IsEnabled = _isDm || _remote;
            _drawCheck.Checked += (s, e) => { _strokePoints = null; _viewport.Cursor = Cursors.Pen; };
            _drawCheck.Unchecked += (s, e) => { _strokePoints = null; _eraserCheck.IsChecked = false; _viewport.Cursor = Cursors.Arrow; };
            dmBar.Children.Add(_drawCheck);

            _eraserCheck.Margin = new Thickness(0, 0, 6, 0);
            _eraserCheck.IsEnabled = _isDm || _remote;
            _eraserCheck.Checked += (s, e) => { if (_drawCheck.IsChecked != true) _drawCheck.IsChecked = true; };
            dmBar.Children.Add(_eraserCheck);

            var brushColor = new Button
            {
                Width = 30,
                Height = 30,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(0),
                Cursor = Cursors.Hand,
                ToolTip = "Цвет кисти",
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                IsEnabled = _isDm || _remote,
                Template = FlatButtonTemplate()
            };
            _brushSwatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                BorderBrush = DmUiKit.B("Line"),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromRgb(0xE0, 0x6A, 0x63))
            };
            brushColor.Content = _brushSwatch;
            brushColor.Click += (s, e) => OpenBrushColorMenu(brushColor);
            System.Windows.Automation.AutomationProperties.SetAutomationId(brushColor, "mapBrushColor");
            System.Windows.Automation.AutomationProperties.SetName(brushColor, "Цвет кисти");
            dmBar.Children.Add(brushColor);

            _brushField.Width = 50;
            _brushField.Margin = new Thickness(0, 0, 6, 0);
            _brushField.IsEnabled = _isDm || _remote;
            _brushField.ToolTip = "Толщина кисти в точках картинки";
            dmBar.Children.Add(_brushField);

            var clearDraw = DmUiKit.Action("Очистить", (s, e) => ClearStrokes(), DmUiKit.ButtonKind.Ghost,
                "Убрать все рисунки с этой сцены", 96, 30);
            clearDraw.Margin = new Thickness(0, 0, 10, 0);
            clearDraw.IsEnabled = _isDm;
            dmBar.Children.Add(clearDraw);

            var tokens = DmUiKit.Action("Токены из боя", (s, e) => SyncTokens(), DmUiKit.ButtonKind.Ghost,
                "Поставить жетоны всех участников текущего боя", 150, 30);
            tokens.Margin = new Thickness(10, 0, 0, 0);
            tokens.IsEnabled = _isDm;
            dmBar.Children.Add(tokens);

            _status.FontSize = 11;
            _status.Foreground = DmUiKit.B("Mute");
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Margin = new Thickness(12, 0, 0, 0);
            // «Мастер ⚙» — вкладыш с инструментами; игроку он не показывается
            var dmToggle = DmUiKit.Action("Инструменты", (s, e) =>
            {
                dmBar.Visibility = dmBar.Visibility == Visibility.Visible
                    ? Visibility.Collapsed
                    : Visibility.Visible;
                WebLog("dm panel " + dmBar.Visibility);
            }, DmUiKit.ButtonKind.Ghost,
                "Открыть инструменты мастера: картинка, сетка, фигуры, стены, кисть", 100, 30);
            dmToggle.Margin = new Thickness(16, 0, 6, 0);
            dmToggle.Visibility = Visibility.Visible;
            bar.Children.Add(dmToggle);
            bar.Children.Add(_status);

            // Вторая строка — общая: игроку доступны фигуры, кисть, ластик,
            // очистка рисунков. Прячем у него только мастерское: картинку,
            // сцены, сетку/клетку, стены, цвет следа и «Токены из боя».
            if (!_isDm)
            {
                foreach (var el in new UIElement[]
                {
                    open, newScene, delScene, _cellSlider, _gridThicknessSlider,
                    _cellField, gridColor, trailColor, _wallCheck, tokens
                })
                {
                    if (el != null) el.Visibility = Visibility.Collapsed;
                }
            }

            // Обе строки живут в одной ячейке сетки: иначе вторая строка
            // накладывалась на первую и её не было видно.
            var topBars = new StackPanel { Orientation = Orientation.Vertical };
            topBars.Children.Add(bar);
            topBars.Children.Add(dmBar);

            Grid.SetRow(topBars, 0);
            root.Children.Add(topBars);

            // ── полотно ──
            _viewport.Background = DmUiKit.B("Void");
            _viewport.ClipToBounds = true;
            System.Windows.Automation.AutomationProperties.SetAutomationId(_viewport, "mapViewport");
            System.Windows.Automation.AutomationProperties.SetName(_viewport, "Поле карты");

            var group = new TransformGroup();
            group.Children.Add(_zoom);
            group.Children.Add(_shift);
            _world.RenderTransform = group;
            _world.RenderTransformOrigin = new Point(0, 0);
            _world.HorizontalAlignment = HorizontalAlignment.Left;
            _world.VerticalAlignment = VerticalAlignment.Top;

            _mapImage.Stretch = Stretch.Fill;
            _mapImage.IsHitTestVisible = false;

            _content.HorizontalAlignment = HorizontalAlignment.Left;
            _content.VerticalAlignment = VerticalAlignment.Top;
            _content.RenderTransformOrigin = new Point(0, 0);
            _content.Children.Add(_mapImage);
            _content.Children.Add(_gridLayer);
            _content.Children.Add(_wallLayer);
            _content.Children.Add(_drawLayer);
            _content.Children.Add(_fovLayer);
            _content.Children.Add(_trailLayer);
            // Фигуры под жетонами: жетон игрока всегда можно схватить.
            _content.Children.Add(_shapeLayer);
            _content.Children.Add(_tokenLayer);
            _world.Children.Add(_content);

            _fog.IsHitTestVisible = false;
            // Туман — сплошная заливка без анимации: кэшируем её в битмап,
            // иначе WPF каждый кадр перерисовывал сотни полигонов-исключений.
            _fog.CacheMode = new BitmapCache();
            _fogDark.CacheMode = new BitmapCache();
            _fog.Visibility = Visibility.Collapsed;
            _world.Children.Add(_fog);
            _world.Children.Add(_fogDark);

            // Линейка — отдельный слой поверх карты и тумана, в координатах
            // окна: так подпись не масштабируется вместе с картой и всегда видна.
            var viewportHost = new Grid();
            viewportHost.Children.Add(_world);
            viewportHost.Children.Add(_measureLayer);
            viewportHost.Children.Add(_pingLayer);
            viewportHost.Children.Add(_webMap);
            _viewport.Child = viewportHost;
            // Подгонка по размеру — только для нативного WPF-слоя. Когда карту
            // рисует WebGL, масштаб и панорама принадлежат ему: любой viewFit
            // здесь сбрасывал вид игрока при каждом изменении размера (окно
            // состояний, оверлеи, возврат на вкладку).
            _viewport.SizeChanged += (s, e) => { if (!_webMapReady && !_userZoomed) FitToWindow(); };
            _viewport.MouseWheel += Viewport_MouseWheel;
            _viewport.MouseLeftButtonDown += (s, e) => Focus();
            _viewport.MouseLeftButtonDown += Viewport_MouseDown;
            _viewport.MouseMove += Viewport_MouseMove;
            _viewport.MouseLeftButtonUp += Viewport_MouseUp;
            _viewport.MouseLeave += (s, e) => { _panning = false; _viewport.Cursor = Cursors.Arrow; };

            var card = DmUiKit.Panel(_viewport, new Thickness(0), DmUiKit.B("Panel"), DmUiKit.B("Line"), 8);
            Grid.SetRow(card, 1);
            root.Children.Add(card);

            Content = root;
        }

        private static ControlTemplate FlatButtonTemplate()
        {
            var template = new ControlTemplate(typeof(Button));
            var border = new FrameworkElementFactory(typeof(Border), "bd");
            border.SetValue(Border.BackgroundProperty, Brushes.Transparent);

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter), "content");
            presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            border.AppendChild(presenter);

            template.VisualTree = border;
            return template;
        }
    }
}
