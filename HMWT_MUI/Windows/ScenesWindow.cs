using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HMWT_MUI.Helpers;
using HMWT_MUI.Models;
using HMWT_MUI.Services;

namespace HMWT_MUI.Windows
{
    /// <summary>
    /// Окно сцен: кампании-группы и карточки с миниатюрой карты.
    /// Одна кампания — свой набор сцен, чтобы не смешивать два похода.
    /// </summary>
    public sealed class ScenesWindow : Window
    {
        private static readonly Brush Ink = new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xF5));
        private static readonly Brush Mute = new SolidColorBrush(Color.FromRgb(0x8B, 0x95, 0xB4));
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x0E, 0x12, 0x1F));
        private static readonly Brush Card = new SolidColorBrush(Color.FromRgb(0x16, 0x1C, 0x2E));
        private static readonly Brush Line = new SolidColorBrush(Color.FromRgb(0x3A, 0x46, 0x68));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x7F));

        private readonly int _characterId;
        private readonly int _currentSceneId;
        private readonly Action<MapSceneData> _onOpen;
        private readonly StackPanel _groups = new StackPanel();
        private readonly TextBox _search;

        public ScenesWindow(int characterId, int currentSceneId, Action<MapSceneData> onOpen)
        {
            _characterId = characterId;
            _currentSceneId = currentSceneId;
            _onOpen = onOpen;

            Title = "Сцены · " + HMWT_MUI.Net.NetVersions.AppVersion;
            Width = 1120;
            Height = 760;
            MinWidth = 760;
            MinHeight = 520;
            Background = Bg;
            Foreground = Ink;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            var root = new Grid { Margin = new Thickness(18, 14, 18, 14) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };

            var addCampaign = DmUiKit.Action("Новая кампания", (s, e) => CreateCampaign(),
                DmUiKit.ButtonKind.Primary, null, 160, 30);
            addCampaign.Margin = new Thickness(8, 0, 0, 0);
            DockPanel.SetDock(addCampaign, Dock.Right);
            top.Children.Add(addCampaign);

            // Поиск по названию сцены: в длинном походе карточек много, и искать
            // нужную глазами по всем кампаниям — лишняя работа
            _search = new TextBox
            {
                Width = 300,
                Height = 30,
                FontSize = 12.5,
                Tag = "Поиск по названию сцены"
            };
            if (TryFindResource("SearchBox") is Style searchStyle) _search.Style = searchStyle;
            _search.TextChanged += (s, e) => Reload();
            DockPanel.SetDock(_search, Dock.Right);
            top.Children.Add(_search);

            top.Children.Add(new TextBlock
            {
                Text = "Кампании объединяют сцены: у каждого похода свои карты.",
                FontSize = 11, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            });

            root.Children.Add(top);

            var scroll = new ScrollViewer
            {
                Content = _groups,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
            };
            Grid.SetRow(scroll, 1);
            root.Children.Add(scroll);

            Content = root;
            Reload();

            DarkWindowChrome.Apply(this, "Сцены · " + HMWT_MUI.Net.NetVersions.AppVersion,
                (UIElement)Content, Card, Line, Ink);
        }

        private void Reload()
        {
            _groups.Children.Clear();

            string query = (_search?.Text ?? "").Trim();

            var campaigns = MapService.LoadCampaigns(_characterId);
            var scenes = MapService.LoadScenes(_characterId);

            if (query.Length > 0)
            {
                scenes = scenes
                    .Where(s => (s.Name ?? "").IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0)
                    .ToList();
            }

            var byCampaign = scenes.GroupBy(s => s.CampaignId)
                .ToDictionary(g => g.Key, g => g.OrderBy(s => s.Name).ToList());

            int shown = 0;

            // Сцены без кампании показываем всегда, а не только когда кампаний нет:
            // иначе они пропадали из окна, стоило завести первую кампанию
            var loose = byCampaign.TryGetValue(0, out var looseScenes)
                ? looseScenes : new List<MapSceneData>();
            if (campaigns.Count == 0 || loose.Count > 0)
            {
                _groups.Children.Add(BuildGroup(null, loose));
                shown++;
            }

            foreach (var campaign in campaigns)
            {
                var list = byCampaign.TryGetValue(campaign.Id, out var own)
                    ? own : new List<MapSceneData>();

                if (query.Length > 0 && list.Count == 0) continue;

                _groups.Children.Add(BuildGroup(campaign, list));
                shown++;
            }

            if (query.Length > 0 && shown == 0)
            {
                _groups.Children.Add(DmUiKit.EmptyState("Ничего не найдено",
                    "По запросу «" + query + "» сцен нет. Попробуйте часть названия."));
            }
        }

        private FrameworkElement BuildGroup(MapCampaignData campaign, List<MapSceneData> scenes)
        {
            var box = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            head.Children.Add(new TextBlock
            {
                Text = campaign == null ? "Без кампании" : campaign.Name,
                FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Ink,
                VerticalAlignment = VerticalAlignment.Center
            });
            head.Children.Add(new TextBlock
            {
                Text = "  " + scenes.Count,
                FontSize = 11, Foreground = Mute, VerticalAlignment = VerticalAlignment.Center
            });

            var addScene = DmUiKit.Action("+ Сцена", (s, e) => CreateScene(campaign?.Id ?? 0),
                DmUiKit.ButtonKind.Soft, null, 92, 26);
            addScene.Margin = new Thickness(0, 0, 6, 0);
            DockPanel.SetDock(addScene, Dock.Right);
            head.Children.Add(addScene);

            if (campaign != null)
            {
                var rename = DmUiKit.Action("Переименовать", (s, e) => RenameCampaign(campaign),
                    DmUiKit.ButtonKind.Ghost, null, 128, 26);
                rename.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(rename, Dock.Right);
                head.Children.Add(rename);

                var delete = DmUiKit.Action("Удалить", (s, e) => DeleteCampaign(campaign),
                    DmUiKit.ButtonKind.Ghost, "Сцены перейдут в «Без кампании»", 86, 26);
                delete.Margin = new Thickness(0, 0, 6, 0);
                DockPanel.SetDock(delete, Dock.Right);
                head.Children.Add(delete);
            }

            box.Children.Add(head);

            var wrap = new WrapPanel();
            if (scenes.Count == 0)
            {
                wrap.Children.Add(new TextBlock
                {
                    Text = "Сцен пока нет — нажмите «+ Сцена».",
                    FontSize = 11, Foreground = Mute, Margin = new Thickness(2, 4, 0, 4)
                });
            }
            else
            {
                foreach (var scene in scenes) wrap.Children.Add(BuildCard(scene));
            }
            box.Children.Add(wrap);
            return box;
        }

        private FrameworkElement BuildCard(MapSceneData scene)
        {
            var card = new Border
            {
                Width = 196,
                Height = 188,
                Background = Card,
                BorderBrush = scene.Id == _currentSceneId ? Accent : Line,
                BorderThickness = new Thickness(scene.Id == _currentSceneId ? 2 : 1),
                CornerRadius = new CornerRadius(8),
                Margin = new Thickness(0, 0, 10, 10),
                Cursor = Cursors.Hand,
                ToolTip = scene.Name
            };
            card.MouseLeftButtonUp += (s, e) =>
            {
                if (FindButton(e.OriginalSource as DependencyObject) != null) return;
                Open(scene);
            };

            var grid = new Grid();
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(104) });
            grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            var preview = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0D, 0x16)),
                CornerRadius = new CornerRadius(7, 7, 0, 0),
                ClipToBounds = true
            };

            string thumb = MapService.ThumbnailPath(scene);
            if (thumb != null && File.Exists(thumb))
            {
                try
                {
                    var image = new BitmapImage();
                    image.BeginInit();
                    image.CacheOption = BitmapCacheOption.OnLoad;
                    image.UriSource = new Uri(thumb);
                    image.EndInit();

                    preview.Child = new Image
                    {
                        Source = image,
                        Stretch = Stretch.UniformToFill
                    };
                }
                catch { }
            }

            if (preview.Child == null)
            {
                string kind = (Path.GetExtension(scene.ImagePath ?? "") ?? "").ToLowerInvariant();
                string hint = kind == ".webm" ? "ВИДЕО" : kind == ".webp" ? "WEBP" : "БЕЗ КАРТИНКИ";
                preview.Child = new TextBlock
                {
                    Text = hint,
                    FontSize = 10,
                    Foreground = Mute,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            Grid.SetRow(preview, 0);
            grid.Children.Add(preview);

            var caption = new StackPanel { Margin = new Thickness(8, 4, 8, 4) };
            caption.Children.Add(new TextBlock
            {
                Text = scene.Name,
                FontSize = 12,
                Foreground = Ink,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            caption.Children.Add(new TextBlock
            {
                Text = $"{scene.WidthPx}×{scene.HeightPx}",
                FontSize = 9,
                Foreground = Mute
            });

            var menu = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 2, 0, 0)
            };
            var rename = DmUiKit.Icon("edit", (s, e) => RenameScene(scene), "Переименовать сцену", 28,
                DmUiKit.ButtonKind.Ghost);
            rename.Margin = new Thickness(0, 0, 4, 0);
            menu.Children.Add(rename);
            var move = DmUiKit.Icon("arrowRight", (s, e) => ShowCampaignMenu(scene, (FrameworkElement)s),
                "Перенести в кампанию", 28, DmUiKit.ButtonKind.Ghost);
            move.Margin = new Thickness(0, 0, 4, 0);
            menu.Children.Add(move);

            var remove = DmUiKit.Icon("trash", (s, e) => DeleteScene(scene), "Удалить сцену", 28,
                DmUiKit.ButtonKind.Ghost);
            menu.Children.Add(remove);
            caption.Children.Add(menu);
            Grid.SetRow(caption, 1);
            grid.Children.Add(caption);

            card.Child = grid;
            return card;
        }

        private void ShowCampaignMenu(MapSceneData scene, FrameworkElement target)
        {
            var menu = new ContextMenu();

            var none = new MenuItem { Header = "Без кампании" };
            none.Click += (s, e) => MoveScene(scene, 0);
            menu.Items.Add(none);
            menu.Items.Add(new Separator());

            foreach (var campaign in MapService.LoadCampaigns(_characterId))
            {
                var item = new MenuItem { Header = campaign.Name };
                int id = campaign.Id;
                item.Click += (s, e) => MoveScene(scene, id);
                menu.Items.Add(item);
            }

            menu.PlacementTarget = target;
            menu.IsOpen = true;
        }

        private void MoveScene(MapSceneData scene, int campaignId)
        {
            if (scene.CampaignId == campaignId) return;
            scene.CampaignId = campaignId;
            MapService.SaveScene(scene);
            Reload();
        }

        private static Button FindButton(DependencyObject source)
        {
            while (source != null && !(source is Button))
            {
                source = VisualTreeHelper.GetParent(source);
            }
            return source as Button;
        }

        private void Open(MapSceneData scene)
        {
            if (scene == null) return;
            _onOpen?.Invoke(scene);
            Close();
        }

        private void CreateCampaign()
        {
            string name = NotePromptWindow.AskText(this, "Новая кампания", "Название",
                "Кампания " + (MapService.LoadCampaigns(_characterId).Count + 1), "Создать");
            if (string.IsNullOrWhiteSpace(name)) return;
            MapService.CreateCampaign(_characterId, name);
            Reload();
        }

        private void RenameCampaign(MapCampaignData campaign)
        {
            string name = NotePromptWindow.AskText(this, "Кампания", "Название", campaign.Name, "Сохранить");
            if (string.IsNullOrWhiteSpace(name)) return;
            MapService.RenameCampaign(campaign.Id, name.Trim());
            Reload();
        }

        private void DeleteCampaign(MapCampaignData campaign)
        {
            if (!NotePromptWindow.Confirm(this, "Удалить кампанию?",
                    "Сцены останутся, но перейдут в «Без кампании».", "Удалить"))
                return;

            MapService.DeleteCampaign(campaign.Id);
            Reload();
        }

        private void CreateScene(int campaignId)
        {
            string name = NotePromptWindow.AskText(this, "Новая сцена", "Название",
                "Сцена " + (MapService.LoadScenes(_characterId).Count + 1), "Создать");
            if (string.IsNullOrWhiteSpace(name)) return;

            var scene = MapService.CreateScene(_characterId, name, campaignId);
            if (scene == null || scene.Id <= 0)
            {
                NotePromptWindow.Message(this, "Сцены", "Не удалось создать сцену.");
                return;
            }

            _onOpen?.Invoke(scene);
            Close();
        }

        private void RenameScene(MapSceneData scene)
        {
            string name = NotePromptWindow.AskText(this, "Сцена", "Название", scene.Name, "Сохранить");
            if (string.IsNullOrWhiteSpace(name)) return;
            scene.Name = name.Trim();
            MapService.SaveScene(scene);
            Reload();
        }

        private void DeleteScene(MapSceneData scene)
        {
            if (!NotePromptWindow.Confirm(this, "Удалить сцену?",
                    $"«{scene.Name}» и её жетоны будут удалены.", "Удалить"))
                return;

            MapService.DeleteScene(scene.Id);
            Reload();
        }
    }
}
