using System.Collections.Generic;

namespace HMWT_MUI.Models
{
    /// <summary>
    /// Сцена — одна карта боя: картинка, сетка и жетоны на ней.
    ///
    /// Сцены принадлежат персонажу (то есть кампании) и переключаются одной
    /// строкой: мастер заранее готовит «Таверну» и «Подземелье», а на игре
    /// просто выбирает нужную — картинка, сетка и жетоны уже на месте.
    /// </summary>
    public sealed class MapSceneData
    {
        public int Id { get; set; }

        public int CharacterId { get; set; }

        public string Name { get; set; } = "Карта";

        /// <summary>Путь картинки относительно папки данных приложения (Maps/…).</summary>
        public string ImagePath { get; set; } = "";

        /// <summary>Хэш картинки: по нему клиенты понимают, что карту качать не нужно.</summary>
        public string ImageHash { get; set; } = "";

        public int WidthPx { get; set; }

        public int HeightPx { get; set; }

        /// <summary>Размер клетки в точках картинки. 70 — типовой.</summary>
        public double GridPx { get; set; } = 70;

        public bool GridOn { get; set; } = true;

        /// <summary>Привязывать ли перемещение жетонов к клеткам. Сетку можно
        /// показывать, но двигать жетоны свободно.</summary>
        public bool SnapToGrid { get; set; } = true;

        /// <summary>Смещение сетки по X: бывает, что карта начинается не с угла клетки.</summary>
        public double GridOffsetX { get; set; }

        public double GridOffsetY { get; set; }

        public bool FogOn { get; set; } = true;

        /// <summary>
        /// Режим тумана: combined — игроки видят общий обзор отряда, own — каждый
        /// только свой. Второй режим нужен для «не вижу, что за спиной у соседа».
        /// </summary>
        public string FogMode { get; set; } = "combined";

        /// <summary>Цвет сетки строкой #AARRGGBB. По умолчанию белая полупрозрачная.</summary>
        public string GridColor { get; set; } = "#33FFFFFF";

        /// <summary>Цвет следа, который остаётся за жетоном.</summary>
        public string TrailColor { get; set; } = "#FF9B8BFF";

        /// <summary>Толщина линий сетки в точках картинки. 1 — тонкая, 3–4 — заметная.</summary>
        public double GridThickness { get; set; } = 1;

        /// <summary>Поворот карты: 0, 90, 180, 270 градусов (не меняет картинку).</summary>
        public int Rotation { get; set; }

        public bool FlipH { get; set; }

        public bool FlipV { get; set; }

        public List<MapTokenData> Tokens { get; set; } = new List<MapTokenData>();

        /// <summary>Стены: закрывают обзор, туман за ними не рассеивается.</summary>
        public List<MapWallData> Walls { get; set; } = new List<MapWallData>();

        /// <summary>Фигуры областей: круг, квадрат, конус, линия.</summary>
        public List<MapShapeData> Shapes { get; set; } = new List<MapShapeData>();

        /// <summary>Штрихи, нарисованные мастером поверх карты.</summary>
        public List<MapStrokeData> Strokes { get; set; } = new List<MapStrokeData>();

        public override string ToString() => Name;
    }

    /// <summary>
    /// Стена: отрезок на карте. Обзор игрока за неё не проходит — туман там
    /// не рассеивается. Дверь — та же стена с kind = "door": мастер щёлкает
    /// по ней на карте WebGL, и открытая дверь обзору больше не мешает.
    /// </summary>
    public sealed class MapWallData
    {
        public int Id { get; set; }

        public int SceneId { get; set; }

        public double X1 { get; set; }

        public double Y1 { get; set; }

        public double X2 { get; set; }

        public double Y2 { get; set; }

        /// <summary>wall — стена, door — дверь (мастер может «открыть», убрав её).</summary>
        public string Kind { get; set; } = "wall";

        /// <summary>Открытая дверь не мешает обзору.</summary>
        public bool IsOpen { get; set; }
    }
    /// <summary>
    /// Фигура области на карте: круг, квадрат, конус или линия.
    ///
    /// Нужна для заклинаний и эффектов: «купол тишины» — круг на 4 клетки,
    /// «огненный шар» — круг на 6, конус дыхания — конус. Полупрозрачная,
    /// чтобы было видно, кто внутри, и не мешала смотреть карту.
    /// </summary>
    public sealed class MapShapeData
    {
        public int Id { get; set; }

        public int SceneId { get; set; }

        /// <summary>circle | square | cone | line</summary>
        public string Kind { get; set; } = "circle";

        public double X { get; set; }

        public double Y { get; set; }

        /// <summary>Радиус (или длина) в клетках.</summary>
        public double RadiusCells { get; set; } = 4;

        /// <summary>Поворот для конуса и линии, градусы.</summary>
        public double Angle { get; set; }

        public string Color { get; set; } = "#FFB98BFF";

        /// <summary>Подпись внутри фигуры (например, название заклинания).</summary>
        public string Label { get; set; } = "";
    }
    /// <summary>
    /// Штрих поверх карты: линия из точек с цветом и толщиной.
    ///
    /// Точки хранятся строкой «x,y;x,y;…» — так штрих занимает одну строку
    /// в базе и легко передаётся в сетевой игре.
    /// </summary>
    public sealed class MapStrokeData
    {
        public int Id { get; set; }

        public int SceneId { get; set; }

        public string Color { get; set; } = "#FFE06A63";

        /// <summary>Толщина в точках картинки: при приближении растёт вместе с картой.</summary>
        public double Thickness { get; set; } = 4;

        /// <summary>Точки через «;», координаты через запятую.</summary>
        public string Points { get; set; } = "";

        public bool IsEraser { get; set; }
    }
    /// <summary>
    /// Жетон на карте — это участник боя (или просто метка, если боя ещё нет).
    ///
    /// Связь с боем через IdCombatant: хиты и состояния у жетона те же, что
    /// в трекере инициативы, а не копия.
    /// </summary>
    public sealed class MapTokenData
    {
        public int Id { get; set; }

        public int SceneId { get; set; }

        /// <summary>Участник боя (EncounterCombatant); 0 — метка без связи.</summary>
        public int CombatantId { get; set; }

        public string Label { get; set; } = "";

        /// <summary>Положение центра жетона в координатах картинки.</summary>
        public double X { get; set; }

        public double Y { get; set; }

        /// <summary>Размер в клетках: 1 — обычное существо, 2 — большое, 3 — огромное.</summary>
        public double SizeCells { get; set; } = 1;

        /// <summary>Цвет кольца: пусто — по стороне (игрок/монстр).</summary>
        public string Color { get; set; } = "";

        public bool Hidden { get; set; }

        /// <summary>Жетон игрока: его обзор рассеивает туман. У игрока нет своей базы боёв, поэтому признак передаётся с картой.</summary>
        public bool IsPlayer { get; set; }

        /// <summary>Хэш картинки жетона: по нему игрок скачивает её один раз.</summary>
        public string PhotoHash { get; set; } = "";

        /// <summary>Путь к картинке на машине ведущего (нужен серверу, чтобы отдать файл).</summary>
        public string PhotoPath { get; set; } = "";

        /// <summary>Радиус обзора в клетках: сколько видно и какой круг рассеивает туман.</summary>
        public double RevealCells { get; set; } = 6;

        /// <summary>Поворот обзора в градусах (0 — вправо), для конуса зрения.</summary>
        public double Facing { get; set; }

        /// <summary>Обзор в градусах: 360 — круговой, 90 — конус (для «спиной не видит»).</summary>
        public double ViewAngle { get; set; } = 360;

        /// <summary>
        /// Цвет рамки жетона: пусто — по стороне (игрок/монстр), иначе свой цвет.
        /// Настраивается из меню по правой кнопке на карте WebGL.
        /// </summary>
        public string FrameColor { get; set; } = "";

        /// <summary>
        /// Цвет сектора обзора этого жетона (пусто — по стороне). Нужен, чтобы
        /// у каждого игрока конус был своего цвета и не сливался с чужими.
        /// </summary>
        public string ViewColor { get; set; } = "";

        /// <summary>
        /// Хиты жетона на момент публикации карты. У клиента нет базы боёв,
        /// поэтому значения едут вместе с жетоном, а не считаются на месте.
        /// </summary>
        public int Hp { get; set; }

        /// <summary>Максимум хитов (0 — не показывать).</summary>
        public int HpMax { get; set; }

        /// <summary>Состояния жетона строкой (как в трекере инициативы).</summary>
        public string Conditions { get; set; } = "";
    }
}
