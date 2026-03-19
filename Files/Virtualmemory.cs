using System;
using System.IO;
using System.Text;

/// <summary>
/// Класс VirtualMemory — моделирует массив произвольно большой размерности
/// с хранением данных в файле подкачки прямого доступа.
///
/// Поддерживаемые типы (задание, пп. 1–3):
///   "int"     — массив long (8 байт), тип 'I' в заголовке.
///               512 байт / 8 байт = 64 элемента на страницу.
///   "char"    — массив строк фиксированной длины, тип 'C'.
///               128 элементов на страницу; размер данных выравнивается на 512.
///   "varchar" — массив строк произвольной длины (до maxLen), тип 'V'.
///               128 элементов на страницу (адреса long в .vm);
///               строки хранятся в отдельном .dat-файле.
///
/// Структура swap-файла (режимы I и C):
///   [2 б]  Сигнатура 'V','M'
///   [8 б]  Размерность массива (long)
///   [1 б]  Тип ('I' или 'C')
///   [4 б]  Длина строки (для 'C') или 0 (для 'I')
///   Далее: [bitmap][page data] × pageCount
///
/// Структура swap-файла (режим V — индексный):
///   [2 б]  Сигнатура 'V','M'
///   [8 б]  Размерность массива (long)
///   [1 б]  Тип 'V'
///   [4 б]  Максимальная длина строки
///   Далее: [bitmap][128 × long адреса] × pageCount
///
/// Структура .dat-файла (только режим V):
///   Записи вида: [4 б длина][байты строки]
/// </summary>
namespace PTaMLab2.Files
{
    public class VirtualMemory : IDisposable
    {
        // ── Константы ────────────────────────────────────────────────────────────

        /// <summary>Минимальный размер буфера страниц (≥ 3 по заданию).</summary>
        private const int BUFFER_SIZE = 3;

        /// <summary>Базовый размер блока данных страницы в байтах.</summary>
        private const int BASE_PAGE_BYTES = 512;

        // Режим I: long (8 байт), 64 элемента × 8 = 512 байт
        private const int INT_ELEM_BYTES    = 8;
        private const int INT_ELEMS_PER_PAGE = BASE_PAGE_BYTES / INT_ELEM_BYTES; // 64
        private const int INT_BITMAP_BYTES  = INT_ELEMS_PER_PAGE / 8;            //  8

        // Режимы C и V: 128 элементов на страницу (по заданию)
        private const int STR_ELEMS_PER_PAGE = 128;
        private const int STR_BITMAP_BYTES   = STR_ELEMS_PER_PAGE / 8; // 16

        // Режим V: адрес хранится как long (8 байт), итого 128 × 8 = 1024 байта
        private const int ADDR_ELEM_BYTES   = 8;
        private const long NO_ADDR          = -1L;

        // Смещение первой страницы: сигнатура(2) + size(8) + type(1) + strLen(4) = 15
        private const long PAGES_START_OFFSET = 15;

        // ── Поля класса ──────────────────────────────────────────────────────────

        /// <summary>Файловый указатель виртуального массива (swap-файл).</summary>
        private readonly FileStream _swapFile;

        /// <summary>Файл строковых данных (только для режима varchar).</summary>
        private FileStream _datFile;

        /// <summary>Тип массива: "int" | "char" | "varchar".</summary>
        private readonly string _arrayType;

        /// <summary>Размерность моделируемого массива.</summary>
        private readonly long _size;

        /// <summary>Длина строки (char) или максимальная длина (varchar); 0 для int.</summary>
        private readonly int _strLen;

        /// <summary>Число страниц в файле подкачки.</summary>
        private readonly long _pageCount;

        /// <summary>Число элементов на одной странице.</summary>
        private readonly int _elemsPerPage;

        /// <summary>Размер битовой карты одной страницы в байтах.</summary>
        private readonly int _bitmapBytes;

        /// <summary>Размер блока данных одной страницы (выровнен на BASE_PAGE_BYTES).</summary>
        private readonly int _pageDataBytes;

        /// <summary>Буфер страниц — массив структур страниц в памяти (≥ 3 слотов).</summary>
        private readonly PageBuffer[] _buffer;

        // ── Конструктор ──────────────────────────────────────────────────────────

        /// <summary>
        /// Инициализация объекта виртуальной памяти.
        /// Если файл не существует — создаёт его, записывает сигнатуру и заполняет нулями.
        /// Если файл существует — проверяет заголовок, затем загружает BUFFER_SIZE страниц.
        /// </summary>
        /// <param name="filePath">Путь к swap-файлу.</param>
        /// <param name="size">Размерность массива (> 0).</param>
        /// <param name="arrayType">"int", "char" или "varchar".</param>
        /// <param name="strLen">
        ///   Фиксированная длина строки для "char";
        ///   максимальная длина строки для "varchar";
        ///   0 для "int".
        /// </param>
        public VirtualMemory(string filePath, long size, string arrayType = "int", int strLen = 0)
        {
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size),
                    "Размерность массива должна быть > 0.");

            _arrayType = arrayType.ToLower();
            _size      = size;
            _strLen    = strLen;

            // Вычисляем параметры страниц в зависимости от типа
            switch (_arrayType)
            {
                case "int":
                    _elemsPerPage  = INT_ELEMS_PER_PAGE; // 64
                    _bitmapBytes   = INT_BITMAP_BYTES;   //  8
                    _pageDataBytes = BASE_PAGE_BYTES;    // 512
                    break;

                case "char":
                    if (strLen <= 0)
                        throw new ArgumentException(
                            "Для типа char длина строки должна быть > 0.", nameof(strLen));
                    _elemsPerPage  = STR_ELEMS_PER_PAGE; // 128
                    _bitmapBytes   = STR_BITMAP_BYTES;   //  16
                    // Размер данных: 128 × strLen, выровнять на 512
                    int rawPageC   = STR_ELEMS_PER_PAGE * strLen;
                    _pageDataBytes = AlignTo512(rawPageC);
                    break;

                case "varchar":
                    if (strLen <= 0)
                        throw new ArgumentException(
                            "Для типа varchar максимальная длина строки должна быть > 0.",
                            nameof(strLen));
                    _elemsPerPage  = STR_ELEMS_PER_PAGE;              // 128
                    _bitmapBytes   = STR_BITMAP_BYTES;                //  16
                    // Данные: 128 адресов × 8 байт = 1024, выровнять на 512
                    int rawPageV   = STR_ELEMS_PER_PAGE * ADDR_ELEM_BYTES; // 1024
                    _pageDataBytes = AlignTo512(rawPageV);                  // 1024
                    break;

                default:
                    throw new ArgumentException(
                        $"Неизвестный тип массива: '{arrayType}'. " +
                        "Ожидается int, char или varchar.", nameof(arrayType));
            }

            // Количество страниц: выравнивание размера массива на границу страницы
            _pageCount = (_size + _elemsPerPage - 1) / _elemsPerPage;

            // Открываем swap-файл в режиме rw
            bool isNewFile = !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
            _swapFile = new FileStream(filePath, FileMode.OpenOrCreate,
                                       FileAccess.ReadWrite, FileShare.ReadWrite);

            if (isNewFile)
                InitSwapFile();    // создаём структуры, заполняем нулями
            else
                ValidateSwapFile(); // проверяем сигнатуру и параметры

            // Для varchar — открываем файл строк (.dat)
            if (_arrayType == "varchar")
            {
                string datPath = Path.ChangeExtension(filePath, ".dat");
                _datFile = new FileStream(datPath, FileMode.OpenOrCreate,
                                          FileAccess.ReadWrite, FileShare.ReadWrite);
            }

            // Инициализируем буфер страниц и загружаем первые BUFFER_SIZE страниц
            _buffer = new PageBuffer[BUFFER_SIZE];
            for (int i = 0; i < BUFFER_SIZE; i++)
                _buffer[i] = new PageBuffer(_bitmapBytes, _pageDataBytes);

            long pagesToLoad = Math.Min(BUFFER_SIZE, _pageCount);
            for (int i = 0; i < pagesToLoad; i++)
                LoadPage(i, i); // загружаем страницу i в слот i буфера
        }

        // ── Публичные свойства ───────────────────────────────────────────────────

        /// <summary>Число элементов в моделируемом массиве.</summary>
        public long Size => _size;

        /// <summary>Число страниц в файле подкачки.</summary>
        public long PageCount => _pageCount;

        /// <summary>Тип массива ("int", "char", "varchar").</summary>
        public string ArrayType => _arrayType;

        /// <summary>Длина строки (char) или макс. длина (varchar); 0 для int.</summary>
        public int StrLen => _strLen;

        // ── Индексатор для int-режима ─────────────────────────────────────────────

        /// <summary>
        /// Оператор [] — чтение/запись элемента long по индексу (только режим int).
        /// </summary>
        public long this[long index]
        {
            get
            {
                if (_arrayType != "int")
                    throw new InvalidOperationException(
                        "Индексатор [] доступен только для режима int. " +
                        "Используйте ReadString/WriteString для char и varchar.");
                return ReadInt(index);
            }
            set
            {
                if (_arrayType != "int")
                    throw new InvalidOperationException(
                        "Индексатор [] доступен только для режима int.");
                WriteInt(index, value);
            }
        }

        // ── Метод чтения целого значения ──────────────────────────────────────────

        /// <summary>
        /// Метод чтения значения элемента массива с заданным индексом.
        /// Определяет номер страницы в буфере, вычисляет страничный адрес,
        /// считывает значение. Возвращает 0 если ячейка не инициализирована.
        /// </summary>
        public long ReadInt(long index)
        {
            if (_arrayType != "int")
                throw new InvalidOperationException("Метод ReadInt доступен только для режима int.");

            ValidateIndex(index);

            int bufSlot   = GetPageBufferIndex(index);
            int slotOnPage = (int)(index % _elemsPerPage);

            // Бит = 0 → ячейка не записана
            if (!_buffer[bufSlot].IsBitSet(slotOnPage))
                return 0L;

            int dataOffset = slotOnPage * INT_ELEM_BYTES;
            return BitConverter.ToInt64(_buffer[bufSlot].Data, dataOffset);
        }

        // ── Метод записи целого значения ──────────────────────────────────────────

        /// <summary>
        /// Метод записи заданного значения в элемент массива с указанным индексом.
        /// Определяет номер страницы в буфере, вычисляет страничный адрес,
        /// записывает значение и модифицирует атрибуты страницы.
        /// </summary>
        public void WriteInt(long index, long value)
        {
            if (_arrayType != "int")
                throw new InvalidOperationException("Метод WriteInt доступен только для режима int.");

            ValidateIndex(index);

            int bufSlot    = GetPageBufferIndex(index);
            int slotOnPage = (int)(index % _elemsPerPage);
            int dataOffset = slotOnPage * INT_ELEM_BYTES;

            // Записываем значение в данные страницы
            Array.Copy(BitConverter.GetBytes(value), 0,
                       _buffer[bufSlot].Data, dataOffset, INT_ELEM_BYTES);

            // Модифицируем атрибуты страницы: бит карты, статус, время
            _buffer[bufSlot].SetBit(slotOnPage);
            _buffer[bufSlot].Modified = 1;
            _buffer[bufSlot].LoadTime = DateTime.UtcNow;
        }

        // ── Метод чтения строки ───────────────────────────────────────────────────

        /// <summary>
        /// Метод чтения строки по индексу.
        /// Возвращает null если ячейка не инициализирована.
        /// </summary>
        public string ReadString(long index)
        {
            if (_arrayType == "int")
                throw new InvalidOperationException(
                    "Метод ReadString недоступен для режима int. Используйте ReadInt.");

            ValidateIndex(index);

            int bufSlot    = GetPageBufferIndex(index);
            int slotOnPage = (int)(index % _elemsPerPage);

            // Бит = 0 → ячейка не записана
            if (!_buffer[bufSlot].IsBitSet(slotOnPage))
                return null;

            if (_arrayType == "char")
            {
                // Страничный адрес элемента
                int dataOffset = slotOnPage * _strLen;
                // Убираем нулевые байты-заполнители
                int realLen = _strLen;
                while (realLen > 0 && _buffer[bufSlot].Data[dataOffset + realLen - 1] == 0)
                    realLen--;
                return Encoding.UTF8.GetString(_buffer[bufSlot].Data, dataOffset, realLen);
            }
            else // varchar
            {
                // Считываем адрес записи в .dat-файле
                int addrOffset = slotOnPage * ADDR_ELEM_BYTES;
                long datAddr   = BitConverter.ToInt64(_buffer[bufSlot].Data, addrOffset);
                return datAddr == NO_ADDR ? null : ReadDatRecord(datAddr);
            }
        }

        // ── Метод записи строки ───────────────────────────────────────────────────

        /// <summary>
        /// Метод записи строки в элемент массива с указанным индексом.
        /// Модифицирует атрибуты страницы: статус, время, битовую карту.
        /// </summary>
        public void WriteString(long index, string value)
        {
            if (_arrayType == "int")
                throw new InvalidOperationException(
                    "Метод WriteString недоступен для режима int. Используйте WriteInt.");

            ValidateIndex(index);

            int  bufSlot    = GetPageBufferIndex(index);
            int  slotOnPage = (int)(index % _elemsPerPage);
            byte[] strBytes = Encoding.UTF8.GetBytes(value ?? "");

            if (_arrayType == "char")
            {
                if (strBytes.Length > _strLen)
                    throw new ArgumentException(
                        $"Строка занимает {strBytes.Length} байт, лимит — {_strLen} байт.");

                // Страничный адрес элемента
                int dataOffset = slotOnPage * _strLen;
                // Заполняем нулями и копируем строку
                Array.Clear(_buffer[bufSlot].Data, dataOffset, _strLen);
                Array.Copy(strBytes, 0, _buffer[bufSlot].Data, dataOffset, strBytes.Length);
            }
            else // varchar
            {
                if (strBytes.Length > _strLen)
                    throw new ArgumentException(
                        $"Строка занимает {strBytes.Length} байт, максимум — {_strLen} байт.");

                // Записываем строку в .dat-файл, получаем её адрес
                long datAddr   = AppendDatRecord(strBytes);
                byte[] addrB   = BitConverter.GetBytes(datAddr);
                int addrOffset = slotOnPage * ADDR_ELEM_BYTES;
                Array.Copy(addrB, 0, _buffer[bufSlot].Data, addrOffset, ADDR_ELEM_BYTES);
            }

            // Модифицируем атрибуты страницы: битовую карту, статус, время записи
            _buffer[bufSlot].SetBit(slotOnPage);
            _buffer[bufSlot].Modified = 1;
            _buffer[bufSlot].LoadTime = DateTime.UtcNow;
        }

        // ── Проверка инициализации ────────────────────────────────────────────────

        /// <summary>
        /// Возвращает true, если в ячейку с указанным индексом было записано значение
        /// (бит в битовой карте соответствующей страницы равен 1).
        /// </summary>
        public bool IsInitialized(long index)
        {
            ValidateIndex(index);
            int bufSlot    = GetPageBufferIndex(index);
            int slotOnPage = (int)(index % _elemsPerPage);
            return _buffer[bufSlot].IsBitSet(slotOnPage);
        }

        // ── Информация о файле подкачки ───────────────────────────────────────────

        /// <summary>Выводит информацию о файле подкачки в консоль.</summary>
        public void PrintInfo()
        {
            char typeChar = _arrayType == "int" ? 'I'
                          : _arrayType == "char" ? 'C' : 'V';
            Console.WriteLine("=== Информация о файле подкачки ===");
            Console.WriteLine($"  Сигнатура          : VM");
            Console.WriteLine($"  Тип элемента       : {_arrayType} ('{typeChar}')");
            Console.WriteLine($"  Размер массива     : {_size} элементов");
            if (_strLen > 0)
                Console.WriteLine($"  Длина строки       : {_strLen} байт");
            Console.WriteLine($"  Элементов/страница : {_elemsPerPage}");
            Console.WriteLine($"  Страниц            : {_pageCount}");
            Console.WriteLine($"  Бит.карта/страница : {_bitmapBytes} байт");
            Console.WriteLine($"  Данные/страница    : {_pageDataBytes} байт");
            Console.WriteLine($"  Буфер страниц      : {BUFFER_SIZE} слотов");
            Console.WriteLine($"  Размер файла       : {_swapFile.Length} байт");
            if (_datFile != null)
                Console.WriteLine($"  Размер .dat файла  : {_datFile.Length} байт");
        }

        // ════════════════════════════════════════════════════════════════════════
        //  ПРИВАТНЫЕ МЕТОДЫ
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Метод определения номера (индекса) слота буфера, где находится элемент
        /// с заданным индексом. Реализует алгоритм замещения FIFO:
        ///   1. Определяет абсолютный номер страницы для элемента.
        ///   2. Проверяет наличие страницы в буфере.
        ///   3. При отсутствии — выбирает самую старую страницу (по LoadTime).
        ///   4. Если выбранная страница модифицирована — выгружает её в файл.
        ///   5. Загружает нужную страницу, обновляет атрибуты.
        /// Возвращает индекс слота в буфере.
        /// </summary>
        private int GetPageBufferIndex(long elementIndex)
        {
            long absPage = elementIndex / _elemsPerPage;

            // 1. Проверяем наличие нужной страницы в буфере
            for (int i = 0; i < BUFFER_SIZE; i++)
                if (_buffer[i].AbsolutePageNumber == absPage)
                    return i;

            // 2. Страница отсутствует — выбираем самый старый слот для замещения
            int victim = 0;
            for (int i = 1; i < BUFFER_SIZE; i++)
                if (_buffer[i].LoadTime < _buffer[victim].LoadTime)
                    victim = i;

            // 3. Если страница модифицирована — выгружаем её в файл
            if (_buffer[victim].Modified == 1)
                FlushPage(victim);

            // 4. Загружаем новую страницу, обновляем атрибуты
            LoadPage(victim, absPage);

            return victim;
        }

        /// <summary>
        /// Загружает страницу с абсолютным номером absPage из файла в слот slot буфера.
        /// Считывает битовую карту и блок данных. Обновляет атрибуты слота.
        /// </summary>
        private void LoadPage(int slot, long absPage)
        {
            long pageSize   = _bitmapBytes + _pageDataBytes;
            long bitmapOff  = PAGES_START_OFFSET + absPage * pageSize;
            long dataOff    = bitmapOff + _bitmapBytes;

            _swapFile.Seek(bitmapOff, SeekOrigin.Begin);
            _swapFile.Read(_buffer[slot].Bitmap, 0, _bitmapBytes);

            _swapFile.Seek(dataOff, SeekOrigin.Begin);
            _swapFile.Read(_buffer[slot].Data, 0, _pageDataBytes);

            // Модифицируем атрибуты загруженной страницы
            _buffer[slot].AbsolutePageNumber = absPage;
            _buffer[slot].Modified           = 0;          // статус: не изменялась
            _buffer[slot].LoadTime           = DateTime.UtcNow; // время загрузки
        }

        /// <summary>
        /// Выгружает страницу из слота slot буфера в файл подкачки.
        /// Если страница не была записана (AbsolutePageNumber &lt; 0), ничего не делает.
        /// Сбрасывает флаг модификации.
        /// </summary>
        private void FlushPage(int slot)
        {
            long absPage = _buffer[slot].AbsolutePageNumber;
            if (absPage < 0) return;

            long pageSize  = _bitmapBytes + _pageDataBytes;
            long bitmapOff = PAGES_START_OFFSET + absPage * pageSize;

            _swapFile.Seek(bitmapOff, SeekOrigin.Begin);
            _swapFile.Write(_buffer[slot].Bitmap, 0, _bitmapBytes);

            _swapFile.Seek(bitmapOff + _bitmapBytes, SeekOrigin.Begin);
            _swapFile.Write(_buffer[slot].Data, 0, _pageDataBytes);

            _swapFile.Flush();
            _buffer[slot].Modified = 0; // сбрасываем статус
        }

        /// <summary>
        /// Инициализирует новый файл подкачки: записывает заголовок и обнуляет все страницы.
        /// </summary>
        private void InitSwapFile()
        {
            _swapFile.Seek(0, SeekOrigin.Begin);

            // Сигнатура 'VM'
            _swapFile.WriteByte((byte)'V');
            _swapFile.WriteByte((byte)'M');

            // Размерность массива (8 байт, long)
            _swapFile.Write(BitConverter.GetBytes(_size), 0, 8);

            // Тип массива (1 байт)
            byte typeChar = _arrayType == "int" ? (byte)'I'
                          : _arrayType == "char" ? (byte)'C' : (byte)'V';
            _swapFile.WriteByte(typeChar);

            // Длина строки (4 байта, int)
            _swapFile.Write(BitConverter.GetBytes(_strLen), 0, 4);

            // Страницы: заполняем нулями
            byte[] zeroPage = new byte[_bitmapBytes + _pageDataBytes];
            for (long p = 0; p < _pageCount; p++)
                _swapFile.Write(zeroPage, 0, zeroPage.Length);

            _swapFile.Flush();
        }

        /// <summary>
        /// Проверяет заголовок существующего файла подкачки.
        /// </summary>
        private void ValidateSwapFile()
        {
            _swapFile.Seek(0, SeekOrigin.Begin);

            if (_swapFile.ReadByte() != 'V' || _swapFile.ReadByte() != 'M')
                throw new InvalidDataException(
                    "Неверная сигнатура файла (ожидалось 'VM').");

            byte[] buf8 = new byte[8];
            _swapFile.Read(buf8, 0, 8);
            long storedSize = BitConverter.ToInt64(buf8, 0);
            if (storedSize != _size)
                throw new InvalidDataException(
                    $"Размер массива в файле ({storedSize}) не совпадает с запрошенным ({_size}).");

            char storedType = (char)_swapFile.ReadByte();
            char expectedType = _arrayType == "int" ? 'I'
                              : _arrayType == "char" ? 'C' : 'V';
            if (storedType != expectedType)
                throw new InvalidDataException(
                    $"Тип элемента в файле ('{storedType}') не совпадает с ожидаемым ('{expectedType}').");

            byte[] buf4 = new byte[4];
            _swapFile.Read(buf4, 0, 4);
            int storedStrLen = BitConverter.ToInt32(buf4, 0);
            if (storedStrLen != _strLen)
                throw new InvalidDataException(
                    $"Длина строки в файле ({storedStrLen}) не совпадает с запрошенной ({_strLen}).");
        }

        // ── Методы работы с .dat-файлом (только для varchar) ────────────────────

        /// <summary>
        /// Дописывает запись в конец .dat-файла.
        /// Возвращает смещение (адрес) начала записи.
        /// </summary>
        private long AppendDatRecord(byte[] data)
        {
            long pos = _datFile.Seek(0, SeekOrigin.End);
            // Префикс: 4 байта — длина строки
            _datFile.Write(BitConverter.GetBytes(data.Length), 0, 4);
            _datFile.Write(data, 0, data.Length);
            _datFile.Flush();
            return pos;
        }

        /// <summary>
        /// Читает запись из .dat-файла по указанному смещению.
        /// </summary>
        private string ReadDatRecord(long offset)
        {
            _datFile.Seek(offset, SeekOrigin.Begin);
            byte[] lenBuf = new byte[4];
            _datFile.Read(lenBuf, 0, 4);
            int len = BitConverter.ToInt32(lenBuf, 0);
            if (len <= 0) return "";
            byte[] strBuf = new byte[len];
            _datFile.Read(strBuf, 0, len);
            return Encoding.UTF8.GetString(strBuf);
        }

        // ── Вспомогательные методы ────────────────────────────────────────────────

        /// <summary>Проверяет, что индекс находится в диапазоне [0, _size - 1].</summary>
        private void ValidateIndex(long index)
        {
            if (index < 0 || index >= _size)
                throw new IndexOutOfRangeException(
                    $"Индекс {index} выходит за границы массива [0, {_size - 1}].");
        }

        /// <summary>Выравнивает значение value на ближайшее кратное 512 сверху.</summary>
        private static int AlignTo512(int value)
            => (value + BASE_PAGE_BYTES - 1) / BASE_PAGE_BYTES * BASE_PAGE_BYTES;

        // ── IDisposable ───────────────────────────────────────────────────────────

        /// <summary>
        /// Выгружает все модифицированные страницы и закрывает файлы.
        /// </summary>
        public void Dispose()
        {
            for (int i = 0; i < BUFFER_SIZE; i++)
                if (_buffer[i].Modified == 1)
                    FlushPage(i);

            _swapFile?.Flush();
            _swapFile?.Close();
            _swapFile?.Dispose();

            _datFile?.Flush();
            _datFile?.Close();
            _datFile?.Dispose();
        }
    }
}
