namespace AbletonOptions
{
    /// <summary>
    /// Локализация каталога опций. В Alive интерфейс только английский (см.
    /// AbletonManager.L), поэтому Ru так и остаётся false, а чтения и записи выбора
    /// языка тут нет — файла настроек у отдельной программы больше не существует.
    /// Русские строки в каталоге сохранены: они уже написаны, и выбрасывать их, чтобы
    /// потом писать заново, было бы глупо, — достаточно поставить Ru, если понадобятся.
    /// </summary>
    public static class L
    {
        public static bool Ru;

        public static string S(string en, string ru)
        {
            return Ru && !string.IsNullOrEmpty(ru) ? ru : en;
        }
    }
}
