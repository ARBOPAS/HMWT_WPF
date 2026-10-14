using System;

namespace HMWT_MUI.Net
{
    /// <summary>
    /// Версии протокола комнаты.
    ///
    /// AppVersion сравнивается, чтобы игроки не пытались играть на разных сборках.
    /// ProtocolVersion повышается при ЛЮБОМ изменении формата сообщений: иначе
    /// патч, поменявший одно поле, сломает связь и это будет трудно найти.
    /// DataFingerprint сверяет наборы данных (справочники), потому что при разных
    /// выгрузках идентификаторы предметов не совпадают.
    /// </summary>
    public static class NetVersions
    {
        /// <summary>
        /// Размер одной части фотографии при передаче.
        ///
        /// Файл режется на части: одно сообщение с картинкой в несколько
        /// мегабайт заблокировало бы канал и подвисло бы на слабом соединении.
        /// </summary>
        public const int PhotoChunkSize = 32 * 1024;

        /// <summary>
        /// Предельный размер фото, который вообще передаём.
        ///
        /// Пять мегабайт — с запасом для гифки-аватара. Более крупный файл
        /// передавался бы слишком долго, и получилось бы хуже, чем без него.
        /// </summary>
        public const int PhotoMaxBytes = 5 * 1024 * 1024;

        /// <summary>
        /// Версия приложения. Держать в одном месте с AssemblyInfo.cs
        /// (Properties\AssemblyInfo.cs) — эти два значения должны совпадать.
        /// Расхождение ловит проверка версии в подвале главного окна и скрипт
        /// сборки установщика (installer\build-installer.ps1).
        ///
        /// 1.0.0.0 — первый официальный выпуск (установщик на GitHub).
        /// Промежуточные сборки 1.2.x закрыты, их нумерация больше не продолжается:
        /// 1.2.2.0 — вкладка «Атаки», честный бросок кубиков, передача фото
        /// в отряде и правило брандмауэра; 1.2.2.5 — атаки по подготовленным
        /// заклинаниям, составные формулы броска, серии атак (мультиатака).
        ///
        /// 1.0.0.2 — сняты зависания на полминуты (открытый reader во время
        /// записи в базу), журнал WAL, починенные иконки заклинаний.
        ///
        /// ВАЖНО: у хоста и у игроков версия должна быть одинаковой.
        /// </summary>
        public const string AppVersion = "1.0.0.2";

        /// <summary>
        /// Версия протокола комнаты. Повышать при изменении формата сообщений.
        ///
        /// 2 — в снимок комнаты добавлено состояние трекера инициативы
        /// (<see cref="RoomSnapshot.Initiative"/>) и модификатор инициативы у
        /// участника: старые сборки не должны подключаться к новым.
        /// </summary>
        public const int ProtocolVersion = 2;

        /// <summary>Ссылка, которую показываем при несовпадении версий.</summary>
        public const string ReleasesUrl = "https://github.com/ARBOPAS/HMWT_WPF/releases";

        /// <summary>Порт по умолчанию для комнаты.</summary>
        public const int DefaultPort = 47777;

        /// <summary>Порт для UDP-поиска комнат в локальной сети.</summary>
        public const int DiscoveryPort = 47778;
    }

    /// <summary>Роль участника в комнате. Мастер получает скрытые броски и инструменты ДМа.</summary>
    public enum RoomRole
    {
        Player = 0,
        GameMaster = 1
    }

    /// <summary>Состояние соединения участника.</summary>
    public enum PeerState
    {
        Connecting = 0,
        Online = 1,
        Away = 2,
        Disconnected = 3
    }

    /// <summary>Типы сообщений обмена.</summary>
    public static class MsgType
    {
        // ── рукопожатие ──
        public const string Hello = "hello";              // клиент -> хост
        public const string HelloOk = "hello_ok";         // хост -> клиент
        public const string Error = "error";              // хост -> клиент

        // ── присутствие ──
        public const string Ping = "ping";
        public const string Pong = "pong";

        // ── состояние ──
        public const string Snapshot = "snapshot";        // полный снимок комнаты (до 8 человек это дешевле диффов)
        public const string PlayerJoined = "player_joined";
        public const string PlayerLeft = "player_left";
        public const string PlayerUpdated = "player_updated";

        /// <summary>
        /// Клиент просит прислать снимок заново.
        ///
        /// Нужно для периодического обновления: если другой игрок сменил аватар
        /// или здоровье, событие об этом могло не дойти, и тогда состояние
        /// запрашивается само — раз в несколько секунд.
        /// </summary>
        public const string SnapshotRequest = "snapshot_request";

        // ── фотографии ──
        //
        // Фото идёт ЧЕРЕЗ ХОСТА: у остальных порт закрыт, принять напрямую они
        // не могут. Клиент отправляет файл хосту частями, хост сохраняет его
        // у себя и рассылает остальным — так каждый видит фото другого.
        public const string PhotoChunk = "photo_chunk";   // часть файла в обе стороны

        /// <summary>
        /// Клиент просит прислать чужой аватар.
        ///
        /// Нужно при входе в комнату: в снимке есть только ПУТЬ к файлу, а самого
        /// файла у нового игрока нет. Без этого запроса чужие аватары не
        /// появлялись до тех пор, пока их владелец не сменит фото.
        /// </summary>
        public const string PhotoRequest = "photo_request";

        /// <summary>Хост отвечает: такого аватара у него нет.</summary>
        public const string PhotoMissing = "photo_missing";

        // ── игровые события ──
        public const string ChatSend = "chat_send";
        public const string ChatMessage = "chat_message";
        public const string RollSend = "roll_send";
        public const string RollMessage = "roll_message";
        public const string SystemNotice = "system_notice";   // «Гром отключился» — серым в ленте
    }

    /// <summary>Коды ошибок рукопожатия (показываем игроку понятный текст).</summary>
    public static class NetError
    {
        public const string VersionMismatch = "version_mismatch";
        public const string ProtocolMismatch = "protocol_mismatch";
        public const string BadKey = "bad_key";
        public const string RoomFull = "room_full";
        public const string DataMismatch = "data_mismatch";
        public const string NoSuchRoom = "no_such_room";
        public const string Internal = "internal";
    }

    /// <summary>Максимальное число участников: больше за одним столом почти не бывает.</summary>
    public static class NetLimits
    {
        public const int MaxPlayers = 8;
        public const int MaxChatHistory = 500;
        public const int MaxRollHistory = 500;
        public const int MaxMessageBytes = 1024 * 512;   // защита от мусора в сокете
        public const int ChatMaxLength = 2000;
    }
}
