using System;
using System.IO;
using System.Text;

/// <summary>
/// Класс VirtualMemoryString — моделирует массив строк фиксированной длины
/// произвольно большой размерности с хранением данных в файле подкачки прямого доступа.
///
/// Соответствует варианту 2 задания: строковый массив (тип 'C').
///
/// Структура файла подкачки:
///   [2 б]  Сигнатура 'V','M'
///   [8 б]  Размерность массива (long)
///   [1 б]  Тип 'C'
///   [4 б]  Длина строки (int)
///   Далее: [bitmap (16 б)][данные страницы] × pageCount
///
/// На каждой странице — 128 элементов. Размер данных страницы в байтах
/// вычисляется как 128 × длина_строки, выравнивается на кратное 512.
/// Количество страниц: выравнивание размера массива на границу страницы.
/// </summary>
public class VirtualMemoryString : IDisposable
{
    // ── Константы структуры страницы ─────────────────────────────────────────

    /// <summary>Число элементов на одной странице (по заданию).</summary>
    private const int ELEMENTS_PER_PAGE = 128;

    /// <summary>Размер битовой карты: 128 / 8 = 16 байт.</summary>
    private const int BITMAP_BYTES = ELEMENTS_PER_PAGE / 8; // 16

    // ── Константы заголовка ───────────────────────────────────────────────────

    private const byte SIG0 = (byte)'V';
    private const byte SIG1 = (byte)'M';
    private const byte TYPE = (byte)'C';

    /// <summary>Смещение первой страницы: сигнатура(2) + size(8) + type(1) + strLen(4) = 15.</summary>
    private const long PAGES_START_OFFSET = 15;

    // ── Поля класса ───────────────────────────────────────────────────────────

    /// <summary>Файловый указатель виртуального массива.</summary>
    private readonly FileStream _file;

    /// <summary>Число элементов в моделируемом массиве.</summary>
    private readonly long _size;

    /// <summary>Фиксированная длина строки в байтах.</summary>
    private readonly int _stringLength;

    /// <summary>Число страниц в файле подкачки.</summary>
    private readonly long _pageCount;

    /// <summary>Размер блока данных страницы (выровнен на 512).</summary>
    private readonly int _pageDataBytes;

    /// <summary>Полный размер одной страницы = BITMAP_BYTES + _pageDataBytes.</summary>
    private readonly int _pageFullBytes;

    // ── Конструктор ───────────────────────────────────────────────────────────

    /// <summary>
    /// Инициализация объекта виртуального строкового массива.
    /// Если файл не существует — создаёт его и заполняет нулями.
    /// Если файл существует — проверяет заголовок.
    /// </summary>
    /// <param name="filePath">Путь к файлу подкачки.</param>
    /// <param name="size">Размерность массива (> 0).</param>
    /// <param name="stringLength">Фиксированная длина строки в байтах (> 0).</param>
    public VirtualMemoryString(string filePath, long size, int stringLength)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size),
                "Размерность массива должна быть > 0.");
        if (stringLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(stringLength),
                "Длина строки должна быть > 0.");

        _size         = size;
        _stringLength = stringLength;

        // Размер данных страницы: 128 × длина_строки, выровнять на 512
        int rawDataBytes = ELEMENTS_PER_PAGE * _stringLength;
        _pageDataBytes   = AlignTo512(rawDataBytes);
        _pageFullBytes   = BITMAP_BYTES + _pageDataBytes;

        // Количество страниц: выравнивание размера массива на границу страницы
        _pageCount = (_size + ELEMENTS_PER_PAGE - 1) / ELEMENTS_PER_PAGE;

        bool fileExists = File.Exists(filePath);
        _file = new FileStream(filePath, FileMode.OpenOrCreate,
                               FileAccess.ReadWrite, FileShare.ReadWrite);

        if (!fileExists || _file.Length == 0)
            InitializeFile();
        else
            ValidateFile();
    }

    // ── Публичные свойства ────────────────────────────────────────────────────

    /// <summary>Число элементов в моделируемом массиве.</summary>
    public long Size => _size;

    /// <summary>Число страниц в файле подкачки.</summary>
    public long PageCount => _pageCount;

    /// <summary>Фиксированная длина строки в байтах.</summary>
    public int StringLength => _stringLength;

    // ── Индексатор ────────────────────────────────────────────────────────────

    /// <summary>
    /// Чтение и запись строки по логическому индексу.
    /// </summary>
    public string this[long index]
    {
        get => ReadElement(index);
        set => WriteElement(index, value);
    }

    // ── Публичные методы ──────────────────────────────────────────────────────

    /// <summary>
    /// Возвращает true, если ячейка была инициализирована (бит в карте = 1).
    /// </summary>
    public bool IsInitialized(long index)
    {
        ValidateIndex(index);
        long pageIndex  = index / ELEMENTS_PER_PAGE;
        int  bitPosition = (int)(index % ELEMENTS_PER_PAGE);
        byte bitmapByte = ReadBitmapByte(pageIndex, bitPosition / 8);
        return (bitmapByte & (1 << (bitPosition % 8))) != 0;
    }

    /// <summary>
    /// Выводит информацию о файле подкачки в консоль.
    /// </summary>
    public void PrintInfo()
    {
        Console.WriteLine("=== Информация о файле подкачки (char) ===");
        Console.WriteLine($"  Сигнатура          : VM");
        Console.WriteLine($"  Тип элемента       : string (char, '{(char)TYPE}')");
        Console.WriteLine($"  Размер массива     : {_size} элементов");
        Console.WriteLine($"  Длина строки       : {_stringLength} байт");
        Console.WriteLine($"  Элементов/страница : {ELEMENTS_PER_PAGE}");
        Console.WriteLine($"  Страниц            : {_pageCount}");
        Console.WriteLine($"  Бит.карта/страница : {BITMAP_BYTES} байт");
        Console.WriteLine($"  Данные/стр. (raw)  : {ELEMENTS_PER_PAGE * _stringLength} байт");
        Console.WriteLine($"  Данные/стр. (align): {_pageDataBytes} байт");
        Console.WriteLine($"  Полная страница    : {_pageFullBytes} байт");
        Console.WriteLine($"  Размер файла       : {_file.Length} байт");
    }

    // ── Приватные методы ──────────────────────────────────────────────────────

    /// <summary>
    /// Инициализирует файл: записывает заголовок и обнуляет все страницы.
    /// </summary>
    private void InitializeFile()
    {
        _file.Seek(0, SeekOrigin.Begin);

        // Сигнатура 'VM'
        _file.WriteByte(SIG0);
        _file.WriteByte(SIG1);

        // Размерность массива (8 байт)
        _file.Write(BitConverter.GetBytes(_size), 0, 8);

        // Тип элемента 'C'
        _file.WriteByte(TYPE);

        // Длина строки (4 байта)
        _file.Write(BitConverter.GetBytes(_stringLength), 0, 4);

        // Страницы: заполняем нулями
        byte[] zeroPage = new byte[_pageFullBytes];
        for (long p = 0; p < _pageCount; p++)
            _file.Write(zeroPage, 0, _pageFullBytes);

        _file.Flush();
    }

    /// <summary>
    /// Проверяет заголовок существующего файла подкачки.
    /// </summary>
    private void ValidateFile()
    {
        _file.Seek(0, SeekOrigin.Begin);

        if (_file.ReadByte() != SIG0 || _file.ReadByte() != SIG1)
            throw new InvalidDataException(
                "Неверная сигнатура файла (ожидалось 'VM').");

        byte[] buf8 = new byte[8];
        _file.Read(buf8, 0, 8);
        long storedSize = BitConverter.ToInt64(buf8, 0);
        if (storedSize != _size)
            throw new InvalidDataException(
                $"Размер массива в файле ({storedSize}) " +
                $"не совпадает с запрошенным ({_size}).");

        if (_file.ReadByte() != TYPE)
            throw new InvalidDataException(
                "Неверный тип элемента (ожидалось 'C').");

        byte[] buf4 = new byte[4];
        _file.Read(buf4, 0, 4);
        int storedStrLen = BitConverter.ToInt32(buf4, 0);
        if (storedStrLen != _stringLength)
            throw new InvalidDataException(
                $"Длина строки в файле ({storedStrLen}) " +
                $"не совпадает с запрошенной ({_stringLength}).");
    }

    /// <summary>
    /// Читает строку по логическому индексу.
    /// Возвращает string.Empty если ячейка не инициализирована.
    /// </summary>
    private string ReadElement(long index)
    {
        ValidateIndex(index);

        if (!IsInitialized(index))
            return string.Empty; // неинициализированная ячейка → пустая строка

        long dataOffset = ElementDataOffset(index);
        _file.Seek(dataOffset, SeekOrigin.Begin);

        byte[] buf = new byte[_stringLength];
        _file.Read(buf, 0, _stringLength);

        // Убираем нулевые байты-заполнители в конце
        int len = Array.IndexOf(buf, (byte)0);
        if (len < 0) len = _stringLength;
        return Encoding.UTF8.GetString(buf, 0, len);
    }

    /// <summary>
    /// Записывает строку по логическому индексу и взводит бит в битовой карте.
    /// </summary>
    private void WriteElement(long index, string value)
    {
        ValidateIndex(index);

        if (value == null) value = string.Empty;

        byte[] strBytes = Encoding.UTF8.GetBytes(value);
        if (strBytes.Length > _stringLength)
            throw new ArgumentException(
                $"Строка занимает {strBytes.Length} байт, " +
                $"максимум — {_stringLength} байт.");

        long pageIndex   = index / ELEMENTS_PER_PAGE;
        int  slotInPage  = (int)(index % ELEMENTS_PER_PAGE);

        // Взводим бит в битовой карте (ячейка инициализирована)
        SetBitmapBit(pageIndex, slotInPage);

        // Формируем буфер фиксированного размера (дополняем нулями)
        byte[] buf = new byte[_stringLength];
        Array.Copy(strBytes, buf, strBytes.Length);

        long dataOffset = ElementDataOffset(index);
        _file.Seek(dataOffset, SeekOrigin.Begin);
        _file.Write(buf, 0, _stringLength);
        _file.Flush();
    }

    // ── Вычисление смещений ───────────────────────────────────────────────────

    /// <summary>Смещение начала битовой карты страницы pageIndex.</summary>
    private long PageBitmapOffset(long pageIndex)
        => PAGES_START_OFFSET + pageIndex * _pageFullBytes;

    /// <summary>Смещение начала блока данных страницы pageIndex.</summary>
    private long PageDataOffset(long pageIndex)
        => PageBitmapOffset(pageIndex) + BITMAP_BYTES;

    /// <summary>Смещение конкретного элемента (строки) в файле.</summary>
    private long ElementDataOffset(long index)
    {
        long pageIndex  = index / ELEMENTS_PER_PAGE;
        int  slotInPage = (int)(index % ELEMENTS_PER_PAGE);
        return PageDataOffset(pageIndex) + (long)slotInPage * _stringLength;
    }

    // ── Методы работы с битовой картой ───────────────────────────────────────

    /// <summary>Читает один байт из битовой карты страницы.</summary>
    private byte ReadBitmapByte(long pageIndex, int byteOffset)
    {
        _file.Seek(PageBitmapOffset(pageIndex) + byteOffset, SeekOrigin.Begin);
        return (byte)_file.ReadByte();
    }

    /// <summary>
    /// Устанавливает бит в 1 в битовой карте (отмечает ячейку как инициализированную).
    /// </summary>
    private void SetBitmapBit(long pageIndex, int bitPosition)
    {
        int  byteOffset = bitPosition / 8;
        int  bitInByte  = bitPosition % 8;
        byte current    = ReadBitmapByte(pageIndex, byteOffset);
        byte updated    = (byte)(current | (1 << bitInByte));

        long pos = PageBitmapOffset(pageIndex) + byteOffset;
        _file.Seek(pos, SeekOrigin.Begin);
        _file.WriteByte(updated);
        _file.Flush();
    }

    // ── Вспомогательные методы ────────────────────────────────────────────────

    /// <summary>Проверяет корректность индекса.</summary>
    private void ValidateIndex(long index)
    {
        if (index < 0 || index >= _size)
            throw new IndexOutOfRangeException(
                $"Индекс {index} вне диапазона [0, {_size - 1}].");
    }

    /// <summary>Выравнивает значение value на ближайшее кратное 512 сверху.</summary>
    private static int AlignTo512(int value)
        => (value + 511) / 512 * 512;

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _file?.Flush();
        _file?.Close();
        _file?.Dispose();
    }
}
