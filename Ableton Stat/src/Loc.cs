namespace AbletonManager
{
    /// <summary>
    /// Интерфейс только на английском. Вызовы вида L.S("Open", "Открыть") оставлены как
    /// есть: второй аргумент просто не используется, зато не пришлось трогать каждую
    /// строку в коде и рисковать опечатками.
    /// </summary>
    public static class L
    {
        public static string S(string en, string ru) { return en; }
    }
}
