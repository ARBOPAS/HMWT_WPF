using System;

namespace HMWT_MUI.Helpers
{
    /// <summary>
    /// Реквизиты для добровольной поддержки проекта — единственное место, где
    /// они записаны. Окно «Поддержать проект» только показывает то, что здесь
    /// заполнено, поэтому править нужно только этот файл.
    ///
    /// Пустые строки не выводятся: можно оставить, например, только номер
    /// карты или только ссылку на страницу сбора. Если не заполнено ничего,
    /// окно честно сообщает, что реквизиты не указаны.
    ///
    /// ВАЖНО: поддержка ничего не меняет в программе. Ни одна функция не
    /// закрыта донатом, платных версий, подписок и рекламы нет, а факт
    /// перевода ни на что не влияет.
    /// </summary>
    public static class DonateInfo
    {
        #region === ЧТО ЗАПОЛНИТЬ ===

        /// <summary>
        /// Номер карты. Пробелы и дефисы можно ставить как удобно для чтения:
        /// при копировании они убираются.
        /// </summary>
        public const string CardNumber = "";

        /// <summary>
        /// Телефон для перевода через СБП, в виде «+7 999 123-45-67».
        /// Пусто — строка не показывается.
        /// </summary>
        public const string SbpPhone = "";

        /// <summary>
        /// Имя получателя. Необязательно: банк всё равно покажет его при
        /// переводе, а в окне это просто подпись под номером карты.
        /// </summary>
        public const string Recipient = "";

        /// <summary>
        /// Ссылка на страницу поддержки: Boosty, DonationAlerts, GitHub
        /// Sponsors, ЮMoney — что удобнее. Открывается в браузере.
        /// </summary>
        public const string LinkUrl = "https://boosty.to/arbopas";

        /// <summary>Подпись кнопки-ссылки. Пусто — «Открыть страницу поддержки».</summary>
        public const string LinkTitle = "Поддержать на Boosty";

        #endregion

        #region === ПРОИЗВОДНЫЕ ЗНАЧЕНИЯ ===

        public static bool HasCard => !string.IsNullOrWhiteSpace(CardNumber);
        public static bool HasSbp => !string.IsNullOrWhiteSpace(SbpPhone);
        public static bool HasLink => !string.IsNullOrWhiteSpace(LinkUrl);
        public static bool HasRecipient => !string.IsNullOrWhiteSpace(Recipient);

        /// <summary>Заполнено ли хоть что-то. Иначе окно показывает подсказку.</summary>
        public static bool IsConfigured => HasCard || HasSbp || HasLink;

        /// <summary>Номер карты без пробелов и дефисов — в таком виде его вставляют в банк.</summary>
        public static string CardForCopy =>
            (CardNumber ?? "").Replace(" ", "").Replace("-", "").Replace("\u00A0", "");

        /// <summary>Телефон без разделителей: так его принимают приложения банков.</summary>
        public static string SbpForCopy =>
            (SbpPhone ?? "").Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "").Replace("\u00A0", "");

        #endregion
    }
}
