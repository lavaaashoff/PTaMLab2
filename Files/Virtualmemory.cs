using System;
using System.IO;

/// Класс, моделирующий массив целого типа (long) произвольно большой размерности с хранением данных в файле подкачки прямого доступа.
public class VirtualMemory : IDisposable
{
    private const int PAGE_DATA_BYTES = 512; // байт данных на странице
    private const int ELEMENT_SIZE = 8; // байт на элемент (long)
    private const int ELEMENTS_PER_PAGE = PAGE_DATA_BYTES / ELEMENT_SIZE; // 64
    private const int BITMAP_BYTES = ELEMENTS_PER_PAGE / 8; // 8
    private const int PAGE_FULL_BYTES = BITMAP_BYTES + PAGE_DATA_BYTES; // 520

    private const byte SIG0 = (byte)'V';
    private const byte SIG1 = (byte)'M';
    private const byte TYPE = (byte)'I';

    // Смещение начала блока описания типа
    private const long HEADER_OFFSET = 2; // после 2 байт сигнатуры
    // Смещение начала первой страницы
    private const long PAGES_START_OFFSET = 2 + 8 + 1; // 11 байт

    // Поля
    private readonly FileStream _file;
    private readonly long _size; // общее число элементов
    private readonly long _pageCount; // число страниц в файле

    /// Конструктор создаёт объект виртуальной памяти и инициализирует файл подкачки.
    public VirtualMemory(string filePath, long size)
    {
        if (size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size), "Размерность должна быть > 0.");

        _size = size;
        _pageCount = (size + ELEMENTS_PER_PAGE - 1) / ELEMENTS_PER_PAGE; // size/64

        bool fileExists = File.Exists(filePath);
        _file = new FileStream(filePath, FileMode.OpenOrCreate,
                               FileAccess.ReadWrite, FileShare.ReadWrite);

        if (!fileExists || _file.Length == 0)
            InitializeFile();
        else
            ValidateFile();
    }

    // Свойства
    /// Число элементов в моделируемом массиве.
    public long Size => _size;

    /// Число страниц в файле подкачки.
    public long PageCount => _pageCount;

    // Индексатор. Чтение/запись элемента по индексу.
    public long this[long index]
    {
        get => ReadElement(index);
        set => WriteElement(index, value);
    }

    /// Возвращает true, если ячейка была инициализирована (бит = 1).
    public bool IsInitialized(long index)
    {
        ValidateIndex(index);
        long pageIndex = index / ELEMENTS_PER_PAGE;
        int bitPosition = (int)(index % ELEMENTS_PER_PAGE);
        byte bitmapByte = ReadBitmapByte(pageIndex, bitPosition / 8);
        return (bitmapByte & (1 << (bitPosition % 8))) != 0;
    }

    /// Выводит информацию о файле подкачки в консоль.
    public void PrintInfo()
    {
        Console.WriteLine("Информация о файле подкачки");
        Console.WriteLine($"  Сигнатура       : VM");
        Console.WriteLine($"  Тип элемента    : long ({ELEMENT_SIZE} байт)");
        Console.WriteLine($"  Размер массива  : {_size} элементов");
        Console.WriteLine($"  Элементов/стр.  : {ELEMENTS_PER_PAGE}");
        Console.WriteLine($"  Страниц         : {_pageCount}");
        Console.WriteLine($"  Бит.карта/стр.  : {BITMAP_BYTES} байт");
        Console.WriteLine($"  Данные/стр.     : {PAGE_DATA_BYTES} байт");
        Console.WriteLine($"  Размер файла    : {_file.Length} байт");
    }


    /// Инициализирует файл, записывает заголовок и обнуляет все страницы.
    private void InitializeFile()
    {
        _file.Seek(0, SeekOrigin.Begin);

        // Сигнатура 'V', 'M'
        _file.WriteByte(SIG0);
        _file.WriteByte(SIG1);

        // Размерность массива (8 байт, little-endian)
        _file.Write(BitConverter.GetBytes(_size), 0, 8);

        // Тип элемента 'I'
        _file.WriteByte(TYPE);

        // Страницы: битовая карта (нули) + данные (нули)
        byte[] zeroPage = new byte[PAGE_FULL_BYTES];
        for (long p = 0; p < _pageCount; p++)
            _file.Write(zeroPage, 0, PAGE_FULL_BYTES);

        _file.Flush();
    }

    /// Проверяет сигнатуру и заголовок существующего файла.
    private void ValidateFile()
    {
        _file.Seek(0, SeekOrigin.Begin);

        if (_file.ReadByte() != SIG0 || _file.ReadByte() != SIG1)
            throw new InvalidDataException("Неверная сигнатура файла подкачки (ожидалось 'VM').");

        byte[] sizeBytes = new byte[8];
        _file.Read(sizeBytes, 0, 8);
        long storedSize = BitConverter.ToInt64(sizeBytes, 0);

        if (storedSize != _size)
            throw new InvalidDataException(
                $"Размер массива в файле ({storedSize}) не совпадает с запрошенным ({_size}).");

        if (_file.ReadByte() != TYPE)
            throw new InvalidDataException("Неверный тип элемента (ожидалось 'I').");
    }

    /// Читает значение элемента по логическому индексу.
    private long ReadElement(long index)
    {
        ValidateIndex(index);

        if (!IsInitialized(index))
            return 0L; // неинициализированные ячейки возвращают 0

        long pageIndex = index / ELEMENTS_PER_PAGE;
        int slotInPage = (int)(index % ELEMENTS_PER_PAGE);
        long dataOffset = PageDataOffset(pageIndex) + slotInPage * ELEMENT_SIZE;

        _file.Seek(dataOffset, SeekOrigin.Begin);
        byte[] buf = new byte[ELEMENT_SIZE];
        _file.Read(buf, 0, ELEMENT_SIZE);
        return BitConverter.ToInt64(buf, 0);
    }

    /// Записывает значение элемента по логическому индексу и взводит бит в карте.
    private void WriteElement(long index, long value)
    {
        ValidateIndex(index);

        long pageIndex = index / ELEMENTS_PER_PAGE;
        int slotInPage = (int)(index % ELEMENTS_PER_PAGE);

        // Взводим бит в битовой карте
        SetBitmapBit(pageIndex, slotInPage);

        // Записываем данные
        long dataOffset = PageDataOffset(pageIndex) + slotInPage * ELEMENT_SIZE;
        _file.Seek(dataOffset, SeekOrigin.Begin);
        _file.Write(BitConverter.GetBytes(value), 0, ELEMENT_SIZE);
        _file.Flush();
    }

    /// Смещение начала битовой карты страницы pageIndex.
    private long PageBitmapOffset(long pageIndex)
        => PAGES_START_OFFSET + pageIndex * PAGE_FULL_BYTES;

    /// Смещение начала блока данных страницы pageIndex.
    private long PageDataOffset(long pageIndex)
        => PageBitmapOffset(pageIndex) + BITMAP_BYTES;

    /// Читает один байт из битовой карты страницы.
    private byte ReadBitmapByte(long pageIndex, int byteOffset)
    {
        _file.Seek(PageBitmapOffset(pageIndex) + byteOffset, SeekOrigin.Begin);
        return (byte)_file.ReadByte();
    }

    /// Взводит бит в битовой карте (устанавливает в 1).
    private void SetBitmapBit(long pageIndex, int bitPosition)
    {
        int byteOffset = bitPosition / 8;
        int bitInByte = bitPosition % 8;
        byte current = ReadBitmapByte(pageIndex, byteOffset);
        byte updated = (byte)(current | (1 << bitInByte));

        long bitmapBytePos = PageBitmapOffset(pageIndex) + byteOffset;
        _file.Seek(bitmapBytePos, SeekOrigin.Begin);
        _file.WriteByte(updated);
        _file.Flush();
    }

    /// Проверяет корректность индекса.
    private void ValidateIndex(long index)
    {
        if (index < 0 || index >= _size)
            throw new IndexOutOfRangeException(
                $"Индекс {index} вне диапазона [0, {_size - 1}].");
    }

    public void Dispose()
    {
        _file?.Flush();
        _file?.Close();
        _file?.Dispose();
    }
}