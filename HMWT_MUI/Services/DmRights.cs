using HMWT_MUI.Net;
using System;

namespace HMWT_MUI.Services
{
    /// <summary>
    /// Права на инструменты ДМа.
    ///
    /// Правило простое: пока игра идёт за одним компьютером, инструменты
    /// доступны всегда. Как только появился отряд, ведущим считается хост —
    /// у подключившихся игроков трекера и бестиария нет: бой ведёт один человек.
    /// </summary>
    public static class DmRights
    {
        /// <summary>Можно ли открывать «Инструменты ДМа» и трекер инициативы.</summary>
        public static bool CanUseDmTools()
        {
            var session = RoomSession.Instance;

            // Одиночная игра: отряда нет, значит и делить нечего
            if (session == null || !session.IsActive) return true;

            // Хост ведёт игру — инструменты у него
            if (session.IsHost) return true;

            // Ведущий может быть и не хостом: роль передаётся, если хост ушёл
            return session.AmIGameMaster;
        }

        /// <summary>Причина отказа — показывается в подсказке кнопки.</summary>
        public static string DenyReason()
        {
            var session = RoomSession.Instance;
            if (session == null || !session.IsActive) return "";
            if (session.IsHost || session.AmIGameMaster) return "";

            return "Инструменты ДМа доступны только ведущему. " +
                   "Трекер инициативы ведёт хост — у вас они скрыты, чтобы бой не разъехался.";
        }

        /// <summary>Мы в комнате, но не ведущий.</summary>
        public static bool IsPlayerInRoom() => !CanUseDmTools();

        /// <summary>Короткое описание роли для подписи в интерфейсе.</summary>
        public static string RoleLine()
        {
            var session = RoomSession.Instance;
            if (session == null || !session.IsActive) return "одиночная игра";
            if (session.IsHost) return "ведущий (хост)";
            return session.AmIGameMaster ? "ведущий" : "игрок";
        }
    }
}
