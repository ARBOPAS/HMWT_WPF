using System;
using System.Collections.Generic;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HMWT_MUI.Net
{
    /// <summary>
    /// Конверт сообщения. Тело лежит как JsonElement — так один конверт
    /// обслуживает все типы, и не нужен зоопарк наследников.
    /// </summary>
    public sealed class NetMessage
    {
        /// <summary>Тип из <see cref="MsgType"/>.</summary>
        [JsonPropertyName("t")]
        public string Type { get; set; } = "";

        /// <summary>Идентификатор запроса для сопоставления ответа (необязателен).</summary>
        [JsonPropertyName("rid")]
        public string RequestId { get; set; }

        /// <summary>Тело сообщения.</summary>
        [JsonPropertyName("d")]
        public JsonElement Data { get; set; }

        public NetMessage() { }

        public NetMessage(string type, object payload = null)
        {
            Type = type;
            Data = NetJson.ToElement(payload);
        }

        /// <summary>Разбирает тело в указанный тип.</summary>
        public T As<T>() where T : class => NetJson.FromElement<T>(Data);
    }

    /// <summary>Общие настройки сериализации. Одно место — одинаковое поведение на хосте и клиенте.</summary>
    public static class NetJson
    {
        public static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        public static JsonElement ToElement(object value)
        {
            if (value == null) return default;
            using var doc = JsonDocument.Parse(JsonSerializer.SerializeToUtf8Bytes(value, Options));
            return doc.RootElement.Clone();
        }

        public static T FromElement<T>(JsonElement element) where T : class
        {
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null)
                return null;
            try { return JsonSerializer.Deserialize<T>(element.GetRawText(), Options); }
            catch { return null; }
        }
    }

    // ══════════════════════ РУКОПОЖАТИЕ ══════════════════════

    /// <summary>Клиент -> хост: первое сообщение. Ключ передаётся не так, как раньше.</summary>
    public sealed class HelloRequest
    {
        [JsonPropertyName("app")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("proto")] public int ProtocolVersion { get; set; }
        [JsonPropertyName("data")] public string DataFingerprint { get; set; } = "";

        /// <summary>Имя комнаты, к которой подключаемся (для постоянных комнат).</summary>
        [JsonPropertyName("room")] public string RoomId { get; set; } = "";

        /// <summary>Одноразовый номер, выданный хостом в <see cref="HelloChallenge"/>.</summary>
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";

        /// <summary>HMAC-SHA256 от ключа и nonce. Сам ключ по сети НЕ идёт.</summary>
        [JsonPropertyName("proof")] public string Proof { get; set; } = "";

        [JsonPropertyName("player")] public PlayerInfo Player { get; set; }
    }

    /// <summary>
    /// Хост -> клиент: вызов перед подключением. Содержит идентификатор комнаты,
    /// требования по версиям и одноразовый nonce для проверки ключа.
    /// Так ключ не передаётся открытым текстом, даже если трафик кто-то слушает.
    /// </summary>
    public sealed class HelloChallenge
    {
        [JsonPropertyName("room")] public string RoomId { get; set; } = "";
        [JsonPropertyName("roomName")] public string RoomName { get; set; } = "";
        [JsonPropertyName("app")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("proto")] public int ProtocolVersion { get; set; }
        [JsonPropertyName("data")] public string DataFingerprint { get; set; } = "";
        [JsonPropertyName("nonce")] public string Nonce { get; set; } = "";
        [JsonPropertyName("players")] public int PlayerCount { get; set; }
        [JsonPropertyName("max")] public int MaxPlayers { get; set; }
    }

    /// <summary>Хост -> клиент: результат рукопожатия.</summary>
    public sealed class HelloResponse
    {
        [JsonPropertyName("ok")] public bool Ok { get; set; }
        [JsonPropertyName("code")] public string ErrorCode { get; set; }
        [JsonPropertyName("message")] public string ErrorText { get; set; }

        /// <summary>Идентификатор, который хост присвоил этому участнику.</summary>
        [JsonPropertyName("playerId")] public string PlayerId { get; set; } = "";
        [JsonPropertyName("role")] public RoomRole Role { get; set; }

        /// <summary>Требуемая версия — чтобы клиент показал понятное «обновитесь».</summary>
        [JsonPropertyName("requiredApp")] public string RequiredAppVersion { get; set; } = "";
        [JsonPropertyName("requiredProto")] public int RequiredProtocolVersion { get; set; }
        [JsonPropertyName("releasesUrl")] public string ReleasesUrl { get; set; } = "";
    }

    // ══════════════════════ УЧАСТНИКИ ══════════════════════

    /// <summary>Участник комнаты. Персонаж берётся из его листа.</summary>
    public sealed class PlayerInfo
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("charName")] public string CharacterName { get; set; } = "";
        [JsonPropertyName("charClass")] public string CharacterClass { get; set; } = "";
        [JsonPropertyName("race")] public string CharacterRace { get; set; } = "";
        [JsonPropertyName("level")] public int Level { get; set; }
        [JsonPropertyName("hp")] public int Hp { get; set; }
        [JsonPropertyName("maxHp")] public int MaxHp { get; set; }
        [JsonPropertyName("ac")] public int ArmorClass { get; set; }

        /// <summary>Относительный путь к аватару. Файл передаётся отдельно, не в каждом сообщении.</summary>
        [JsonPropertyName("avatar")] public string AvatarPath { get; set; } = "";

        [JsonPropertyName("role")] public RoomRole Role { get; set; }
        [JsonPropertyName("state")] public PeerState State { get; set; }

        [JsonPropertyName("charId")] public int CharacterId { get; set; }

        /// <summary>
        /// Модификатор инициативы с листа персонажа. Нужен мастеру, чтобы
        /// добавить игроков в трекер инициативы одним нажатием.
        /// </summary>
        [JsonPropertyName("init")] public int Initiative { get; set; }
    }

    /// <summary>
    /// Часть файла фотографии.
    ///
    /// Файл режется на части: одно сообщение с картинкой в несколько мегабайт
    /// заблокировало бы канал и подвисло бы на слабом соединении. Части идут
    /// через хоста — у остальных игроков порт закрыт, принять напрямую они
    /// не могут.
    /// </summary>
    public sealed class PhotoChunk
    {
        /// <summary>Чей это аватар: идентификатор участника в комнате.</summary>
        [JsonPropertyName("owner")] public string OwnerId { get; set; } = "";

        /// <summary>Номер части, начиная с нуля.</summary>
        [JsonPropertyName("i")] public int Index { get; set; }

        /// <summary>Сколько всего частей. Вместе с номером позволяет собрать файл.</summary>
        [JsonPropertyName("n")] public int Total { get; set; }

        /// <summary>Расширение файла вместе с точкой, например «.gif».</summary>
        [JsonPropertyName("ext")] public string Extension { get; set; } = ".png";

        /// <summary>Содержимое части в base64.</summary>
        [JsonPropertyName("d")] public string Base64 { get; set; } = "";

        /// <summary>Полный размер файла — чтобы проверять целостность.</summary>
        [JsonPropertyName("len")] public int TotalBytes { get; set; }
    }

    /// <summary>
    /// Просьба прислать чужой аватар.
    ///
    /// В снимке комнаты есть только путь к файлу, а самого файла у нового
    /// игрока нет: фото передаются отдельно. Поэтому при входе клиент просит
    /// недостающие аватары у хоста.
    /// </summary>
    public sealed class PhotoRequest
    {
        /// <summary>Чей аватар нужен.</summary>
        [JsonPropertyName("owner")] public string OwnerId { get; set; } = "";
    }

    /// <summary>Сборка частей фото на принимающей стороне.</summary>
    public sealed class PhotoAssembler
    {
        private readonly Dictionary<int, byte[]> _parts = new Dictionary<int, byte[]>();
        private int _expected = -1;

        public string Extension { get; private set; } = ".png";
        public string OwnerId { get; private set; } = "";
        public int TotalBytes { get; private set; }

        /// <summary>
        /// Добавляет часть. Возвращает готовый файл, когда пришли все части,
        /// иначе null.
        /// </summary>
        public byte[] Add(PhotoChunk chunk)
        {
            if (chunk == null || chunk.Total <= 0) return null;

            // Смена владельца или числа частей — начинаем заново
            if (_expected != chunk.Total || !string.Equals(OwnerId, chunk.OwnerId, StringComparison.Ordinal))
            {
                _parts.Clear();
                _expected = chunk.Total;
                OwnerId = chunk.OwnerId;
                Extension = string.IsNullOrWhiteSpace(chunk.Extension) ? ".png" : chunk.Extension;
                TotalBytes = chunk.TotalBytes;
            }

            try
            {
                _parts[chunk.Index] = Convert.FromBase64String(chunk.Base64 ?? "");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[PhotoChunk] часть {chunk.Index}: {ex.Message}");
                _parts.Clear();
                _expected = -1;
                return null;
            }

            if (_parts.Count < _expected) return null;

            // Собираем по порядку
            var result = new List<byte>(TotalBytes > 0 ? TotalBytes : _parts.Count * 1024);
            for (int i = 0; i < _expected; i++)
            {
                if (!_parts.TryGetValue(i, out var part))
                {
                    // Часть потерялась — ждём её повторно
                    return null;
                }
                result.AddRange(part);
            }

            _parts.Clear();
            _expected = -1;
            return result.ToArray();
        }

        public void Reset()
        {
            _parts.Clear();
            _expected = -1;
        }
    }

    // ══════════════════════ ЧАТ И ЛЕНТА ══════════════════════

    public enum FeedKind
    {
        Chat = 0,
        Roll = 1,
        System = 2
    }

    /// <summary>Одна запись в общей ленте: чат, бросок или системное событие.</summary>
    public sealed class FeedEntry
    {
        [JsonPropertyName("kind")] public FeedKind Kind { get; set; }
        [JsonPropertyName("at")] public DateTime At { get; set; } = DateTime.Now;
        [JsonPropertyName("by")] public string AuthorId { get; set; } = "";
        [JsonPropertyName("byName")] public string AuthorName { get; set; } = "";
        [JsonPropertyName("text")] public string Text { get; set; } = "";

        // ── поля броска (заполняются при Kind = Roll) ──
        [JsonPropertyName("formula")] public string Formula { get; set; } = "";
        [JsonPropertyName("rollType")] public string RollType { get; set; } = "";
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("rolls")] public List<int> IndividualRolls { get; set; }
        [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    }

    // ══════════════════════ ИГРОВЫЕ ЗАПРОСЫ ══════════════════════

    /// <summary>Клиент -> хост: отправить сообщение в чат.</summary>
    public sealed class ChatSendRequest
    {
        [JsonPropertyName("text")] public string Text { get; set; } = "";
    }

    /// <summary>
    /// Клиент -> хост: сообщить результат броска.
    /// Кубы считает клиент (у него уже есть DiceRoller), а хост ведёт ленту.
    /// </summary>
    public sealed class RollSendRequest
    {
        [JsonPropertyName("formula")] public string Formula { get; set; } = "";
        [JsonPropertyName("rollType")] public string RollType { get; set; } = "";
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("rolls")] public List<int> IndividualRolls { get; set; }

        /// <summary>Скрытый бросок мастера: игроки не получают это событие вообще.</summary>
        [JsonPropertyName("hidden")] public bool Hidden { get; set; }
    }

    // ══════════════════════ ИНИЦИАТИВА ══════════════════════

    /// <summary>
    /// Один жетон трекера инициативы для игроков.
    ///
    /// Уходит ТОЛЬКО имя и результат: бестиарий у каждого свой, и передавать
    /// его незачем. Мастер ведёт бой у себя, игроки видят очередь и свой ход.
    /// </summary>
    public sealed class InitiativeCombatantDto
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("roll")] public int Roll { get; set; }
        [JsonPropertyName("mod")] public int Modifier { get; set; }
        [JsonPropertyName("total")] public int Total { get; set; }
        [JsonPropertyName("player")] public bool IsPlayer { get; set; }
        [JsonPropertyName("current")] public bool IsCurrent { get; set; }
    }

    /// <summary>Состояние трекера инициативы: порядок хода, раунд, чей ход.</summary>
    public sealed class InitiativeState
    {
        [JsonPropertyName("active")] public bool IsActive { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("round")] public int Round { get; set; } = 1;
        [JsonPropertyName("order")] public List<InitiativeCombatantDto> Combatants { get; set; }
            = new List<InitiativeCombatantDto>();

        /// <summary>Есть ли что показывать (пустое состояние ничего не рисует).</summary>
        public bool HasContent => IsActive && Combatants != null && Combatants.Count > 0;

        public string RoundLine() => $"РАУНД {Math.Max(1, Round)}";
    }

    /// <summary>Полный снимок состояния комнаты (до 8 человек это проще и надёжнее диффов).</summary>
    public sealed class RoomSnapshot
    {
        [JsonPropertyName("roomId")] public string RoomId { get; set; } = "";
        [JsonPropertyName("roomName")] public string RoomName { get; set; } = "";
        [JsonPropertyName("players")] public List<PlayerInfo> Players { get; set; } = new List<PlayerInfo>();
        [JsonPropertyName("feed")] public List<FeedEntry> Feed { get; set; } = new List<FeedEntry>();
        [JsonPropertyName("app")] public string AppVersion { get; set; } = "";
        [JsonPropertyName("proto")] public int ProtocolVersion { get; set; }
        [JsonPropertyName("data")] public string DataFingerprint { get; set; } = "";

        /// <summary>Трекер инициативы ведущего. У игроков — только для показа.</summary>
        [JsonPropertyName("init")] public InitiativeState Initiative { get; set; }
    }
}
