using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Универсальный класс виртуальной памяти.
///
/// Поддерживает три типа массивов:
///   "int"     — массив long  (8 байт/элемент), тип 'I' в файле.
///   "char"    — массив строк фиксированной длины (len байт), тип 'C'.
///   "varchar" — массив строк произвольной длины (до maxLen), тип 'V'.
///               Использует два файла: индексный swap (.vm) и строковый (.dat).
///
/// Структура swap-файла (режимы I и C):
///   [2 байта]  Сигнатура 'V','M'
///   [8 байт]   Размерность (long)
///   [1 байт]   Тип ('I' или 'C')
///   [4 байта]  Длина строки (для 'C') или 0 (для 'I')
///   Далее: [bitmap][page data] × pageCount
///
/// Структура swap-файла (режим V — индексный):
///   [2 байта]  Сигнатура 'V','M'
///   [8 байт]   Размерность (long)
///   [1 байт]   Тип 'V'
///   [4 байта]  Максимальная длина строки
///   Далее: [bitmap (8 байт)][64 × long адреса в .dat] × pageCount
///
/// Структура .dat-файла (только режим V):
///   Записи вида: [4 байта длина][байты строки]
/// </summary>

namespace PTaMLab2.Files
{
    public class VirtualMemory : IDisposable
    {
        // ── Константы ────────────────────────────────────────────────────────────
        private const int BUFFER_SIZE = 3;    // минимум 3 страницы в буфере
        private const int PAGE_DATA_BYTES = 512;  // базовый размер страницы

        // Режим I: long (8 байт), 64 элемента на страницу
        private const int INT_ELEM_SIZE = 8;
        private const int INT_ELEMS_PER_PAGE = PAGE_DATA_BYTES / INT_ELEM_SIZE; // 64
        private const int INT_BITMAP_BYTES = INT_ELEMS_PER_PAGE / 8;          // 8

        // Режим C и V: 128 элементов на страницу
        private const int STR_ELEMS_PER_PAGE = 128;
        private const int STR_BITMAP_BYTES = STR_ELEMS_PER_PAGE / 8;         // 16

        // Режим V: адрес = long (8 байт), 64 адреса × 8 = 512
        private const int ADDR_ELEM_SIZE = 8;
        private const int ADDR_ELEMS_PER_PAGE = PAGE_DATA_BYTES / ADDR_ELEM_SIZE; // 64
        private const int ADDR_BITMAP_BYTES = ADDR_ELEMS_PER_PAGE / 8;           // 8

        private const long NO_ADDR = -1L;

        // ── Поля ─────────────────────────────────────────────────────────────────
        private readonly FileStream _swapFile;
        private FileStream _datFile;       // только для varchar
        private readonly string _arrayType;     // "int" | "char" | "varchar"
        private readonly long _size;
        private readonly int _strLen;        // фикс. длина (char) или maxLen (varchar)
        private readonly long _pageCount;
        private readonly int _elemsPerPage;
        private readonly int _bitmapBytes;
        private readonly int _pageDataBytes; // фактический размер блока данных
        private readonly long _pagesStartOffset;

        private readonly PageBuffer[] _buffer;

        // ── Конструктор ──────────────────────────────────────────────────────────
        /// <param name="filePath">Путь к файлу (без расширения для varchar).</param>
        /// <param name="size">Размерность массива.</param>
        /// <param name="arrayType">"int", "char" или "varchar".</param>
        /// <param name="strLen">Длина строки для "char"; максимальная для "varchar"; 0 для "int".</param>
        public VirtualMemory(string filePath, long size, string arrayType = "int", int strLen = 0)
        {
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size));

            _arrayType = arrayType.ToLower();
            _size = size;
            _strLen = strLen;

            // Вычисляем параметры страниц по типу
            switch (_arrayType)
            {
                case "int":
                    _elemsPerPage = INT_ELEMS_PER_PAGE;   // 64
                    _bitmapBytes = INT_BITMAP_BYTES;      // 8
                    _pageDataBytes = PAGE_DATA_BYTES;        // 512
                    break;

                case "char":
                    if (strLen <= 0)
                        throw new ArgumentException("Для char strLen > 0.");
                    _elemsPerPage = STR_ELEMS_PER_PAGE;   // 128
                    _bitmapBytes = STR_BITMAP_BYTES;      // 16
                                                          // 128 строк × strLen байт, выровнено на 512
                    int rawPage = STR_ELEMS_PER_PAGE * strLen;
                    _pageDataBytes = ((rawPage + PAGE_DATA_BYTES - 1)
                                      / PAGE_DATA_BYTES) * PAGE_DATA_BYTES;
                    break;

                case "varchar":
                    if (strLen <= 0)
                        throw new ArgumentException("Для varchar strLen (maxLen) > 0.");
                    _elemsPerPage = ADDR_ELEMS_PER_PAGE;  // 64
                    _bitmapBytes = ADDR_BITMAP_BYTES;     // 8
                    _pageDataBytes = PAGE_DATA_BYTES;        // 512
                    break;

                default:
                    throw new ArgumentException($"Неизвестный тип: {arrayType}");
            }

            _pageCount = (_size + _elemsPerPage - 1) / _elemsPerPage;
            // сигнатура(2) + size(8) + type(1) + strLen(4) = 15
            _pagesStartOffset = 15;

            // Открываем swap-файл
            bool newSwap = !File.Exists(filePath) || new FileInfo(filePath).Length == 0;
            _swapFile = new FileStream(filePath, FileMode.OpenOrCreate,
                                          FileAccess.ReadWrite, FileShare.ReadWrite);
            if (newSwap)
                InitSwapFile();
            else
                ValidateSwapFile();

            // Для varchar — файл строк
            if (_arrayType == "varchar")
            {
                string datPath = Path.ChangeExtension(filePath, ".dat");
                _datFile = new FileStream(datPath, FileMode.OpenOrCreate,
                                                FileAccess.ReadWrite, FileShare.ReadWrite);
            }

            // Инициализируем буфер и загружаем первые BUFFER_SIZE страниц
            _buffer = new PageBuffer[BUFFER_SIZE];
            for (int i = 0; i < BUFFER_SIZE; i++)
                _buffer[i] = new PageBuffer(_bitmapBytes, _pageDataBytes);

            long toLoad = Math.Min(BUFFER_SIZE, _pageCount);
            for (int i = 0; i < toLoad; i++)
                LoadPage(i, i);
        }

        // ── Публичные свойства ───────────────────────────────────────────────────
        public long Size => _size;
        public long PageCount => _pageCount;

        // ── Индексатор для int-режима: long read = vm[idx] ───────────────────────
        public long this[long index]
        {
            get
            {
                if (_arrayType != "int")
                    throw new InvalidOperationException(
                        "Используйте GetString(index) для режимов char/varchar.");
                return ReadInt(index);
            }
            set
            {
                if (_arrayType != "int")
                    throw new InvalidOperationException(
                        "Используйте SetString(index, value) для режимов char/varchar.");
                WriteInt(index, value);
            }
        }

        // ── Вспомогательные методы для строковых режимов ─────────────────────────
        public string GetString(long index) => ReadString(index);
        public void SetString(long index, string value) => WriteString(index, value);

        // ── Чтение long ──────────────────────────────────────────────────────────
        public long ReadInt(long index)
        {
            if (_arrayType != "int") throw new InvalidOperationException("Не режим int.");
            ValidateIndex(index);

            int bufIdx = GetPageBufferIndex(index);
            int slotInPage = (int)(index % _elemsPerPage);

            if (!_buffer[bufIdx].IsBitSet(slotInPage)) return 0L;
            return BitConverter.ToInt64(_buffer[bufIdx].Data, slotInPage * INT_ELEM_SIZE);
        }

        // ── Запись long ──────────────────────────────────────────────────────────
        public void WriteInt(long index, long value)
        {
            if (_arrayType != "int") throw new InvalidOperationException("Не режим int.");
            ValidateIndex(index);

            int bufIdx = GetPageBufferIndex(index);
            int slotInPage = (int)(index % _elemsPerPage);
            int offset = slotInPage * INT_ELEM_SIZE;

            Array.Copy(BitConverter.GetBytes(value), 0,
                       _buffer[bufIdx].Data, offset, INT_ELEM_SIZE);

            _buffer[bufIdx].SetBit(slotInPage);
            _buffer[bufIdx].Modified = 1;
            _buffer[bufIdx].LoadTime = DateTime.UtcNow;
        }

        // ── Чтение строки ─────────────────────────────────────────────────────────
        public string ReadString(long index)
        {
            ValidateIndex(index);
            int bufIdx = GetPageBufferIndex(index);
            int slotInPage = (int)(index % _elemsPerPage);

            if (!_buffer[bufIdx].IsBitSet(slotInPage)) return null;

            if (_arrayType == "char")
            {
                int offset = slotInPage * _strLen;
                int realLen = _strLen;
                while (realLen > 0 && _buffer[bufIdx].Data[offset + realLen - 1] == 0)
                    realLen--;
                return Encoding.UTF8.GetString(_buffer[bufIdx].Data, offset, realLen);
            }
            else // varchar
            {
                int addrOff = slotInPage * ADDR_ELEM_SIZE;
                long datAddr = BitConverter.ToInt64(_buffer[bufIdx].Data, addrOff);
                return datAddr == NO_ADDR ? null : ReadDatRecord(datAddr);
            }
        }

        // ── Запись строки ─────────────────────────────────────────────────────────
        public void WriteString(long index, string value)
        {
            ValidateIndex(index);
            int bufIdx = GetPageBufferIndex(index);
            int slotInPage = (int)(index % _elemsPerPage);
            byte[] strBytes = Encoding.UTF8.GetBytes(value ?? "");

            if (_arrayType == "char")
            {
                if (strBytes.Length > _strLen)
                    throw new ArgumentException($"Строка > {_strLen} байт.");
                int offset = slotInPage * _strLen;
                Array.Clear(_buffer[bufIdx].Data, offset, _strLen);
                Array.Copy(strBytes, 0, _buffer[bufIdx].Data, offset, strBytes.Length);
            }
            else // varchar
            {
                if (strBytes.Length > _strLen)
                    throw new ArgumentException($"Строка > maxLen={_strLen} байт.");
                long datAddr = AppendDatRecord(strBytes);
                byte[] addrB = BitConverter.GetBytes(datAddr);
                int addrOff = slotInPage * ADDR_ELEM_SIZE;
                Array.Copy(addrB, 0, _buffer[bufIdx].Data, addrOff, ADDR_ELEM_SIZE);
            }

            _buffer[bufIdx].SetBit(slotInPage);
            _buffer[bufIdx].Modified = 1;
            _buffer[bufIdx].LoadTime = DateTime.UtcNow;
        }

        // ── Проверка инициализации ────────────────────────────────────────────────
        public bool IsInitialized(long index)
        {
            ValidateIndex(index);
            int bufIdx = GetPageBufferIndex(index);
            int slotInPage = (int)(index % _elemsPerPage);
            return _buffer[bufIdx].IsBitSet(slotInPage);
        }

        // ── Информация ───────────────────────────────────────────────────────────
        public void PrintInfo()
        {
            char tc = _arrayType == "int" ? 'I' : _arrayType == "char" ? 'C' : 'V';
            Console.WriteLine("=== VirtualMemory ===");
            Console.WriteLine($"  Тип            : {_arrayType} ('{tc}')");
            Console.WriteLine($"  Размерность    : {_size}");
            if (_strLen > 0) Console.WriteLine($"  Длина строки   : {_strLen}");
            Console.WriteLine($"  Элементов/стр. : {_elemsPerPage}");
            Console.WriteLine($"  Страниц        : {_pageCount}");
            Console.WriteLine($"  Bitmap/стр.    : {_bitmapBytes} байт");
            Console.WriteLine($"  Данные/стр.    : {_pageDataBytes} байт");
            Console.WriteLine($"  Буфер страниц  : {BUFFER_SIZE} слотов");
            Console.WriteLine($"  Размер .vm     : {_swapFile.Length} байт");
            if (_datFile != null)
                Console.WriteLine($"  Размер .dat    : {_datFile.Length} байт");
        }

        // ════════════════════════════════════════════════════════════════════════
        //  ПРИВАТНЫЕ МЕТОДЫ
        // ════════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Ключевой метод: определяет индекс слота буфера для нужного элемента.
        /// Реализует алгоритм замещения: выбирает самый старый слот (FIFO),
        /// выгружает если модифицирован, загружает нужную страницу.
        /// </summary>
        private int GetPageBufferIndex(long elementIndex)
        {
            long absPage = elementIndex / _elemsPerPage;

            // 1. Проверяем наличие страницы в буфере
            for (int i = 0; i < BUFFER_SIZE; i++)
                if (_buffer[i].AbsolutePageNumber == absPage)
                    return i;

            // 2. Выбираем самый старый слот
            int victim = 0;
            for (int i = 1; i < BUFFER_SIZE; i++)
                if (_buffer[i].LoadTime < _buffer[victim].LoadTime)
                    victim = i;

            // 3. Проверяем флаг модификации — если 1, выгружаем страницу
            if (_buffer[victim].Modified == 1)
                FlushPage(victim);

            // 4. Загружаем новую страницу, обновляем атрибуты
            LoadPage(victim, absPage);

            return victim;
        }

        private void LoadPage(int slot, long absPage)
        {
            long bitmapOff = _pagesStartOffset
                           + absPage * (_bitmapBytes + _pageDataBytes);
            long dataOff = bitmapOff + _bitmapBytes;

            _swapFile.Seek(bitmapOff, SeekOrigin.Begin);
            _swapFile.Read(_buffer[slot].Bitmap, 0, _bitmapBytes);

            _swapFile.Seek(dataOff, SeekOrigin.Begin);
            _swapFile.Read(_buffer[slot].Data, 0, _pageDataBytes);

            // Модифицируем атрибуты загруженной страницы
            _buffer[slot].AbsolutePageNumber = absPage;
            _buffer[slot].Modified = 0;
            _buffer[slot].LoadTime = DateTime.UtcNow;
        }

        private void FlushPage(int slot)
        {
            long absPage = _buffer[slot].AbsolutePageNumber;
            if (absPage < 0) return;

            long bitmapOff = _pagesStartOffset
                           + absPage * (_bitmapBytes + _pageDataBytes);

            _swapFile.Seek(bitmapOff, SeekOrigin.Begin);
            _swapFile.Write(_buffer[slot].Bitmap, 0, _bitmapBytes);

            _swapFile.Seek(bitmapOff + _bitmapBytes, SeekOrigin.Begin);
            _swapFile.Write(_buffer[slot].Data, 0, _pageDataBytes);

            _swapFile.Flush();
            _buffer[slot].Modified = 0;
        }

        private void InitSwapFile()
        {
            _swapFile.Seek(0, SeekOrigin.Begin);
            _swapFile.WriteByte((byte)'V');
            _swapFile.WriteByte((byte)'M');
            _swapFile.Write(BitConverter.GetBytes(_size), 0, 8);

            byte tc = _arrayType == "int" ? (byte)'I'
                    : _arrayType == "char" ? (byte)'C' : (byte)'V';
            _swapFile.WriteByte(tc);
            _swapFile.Write(BitConverter.GetBytes(_strLen), 0, 4);

            byte[] zero = new byte[_bitmapBytes + _pageDataBytes];
            for (long p = 0; p < _pageCount; p++)
                _swapFile.Write(zero, 0, zero.Length);

            _swapFile.Flush();
        }

        private void ValidateSwapFile()
        {
            _swapFile.Seek(0, SeekOrigin.Begin);
            if (_swapFile.ReadByte() != 'V' || _swapFile.ReadByte() != 'M')
                throw new InvalidDataException("Неверная сигнатура (ожидалось 'VM').");

            byte[] b8 = new byte[8];
            _swapFile.Read(b8, 0, 8);
            long storedSize = BitConverter.ToInt64(b8, 0);
            if (storedSize != _size)
                throw new InvalidDataException(
                    $"Размер в файле ({storedSize}) ≠ запрошенному ({_size}).");

            char storedType = (char)_swapFile.ReadByte();
            char expected = _arrayType == "int" ? 'I' : _arrayType == "char" ? 'C' : 'V';
            if (storedType != expected)
                throw new InvalidDataException(
                    $"Тип в файле ('{storedType}') ≠ ожидаемому ('{expected}').");
        }

        // ── .dat файл (varchar) ──────────────────────────────────────────────────
        private long AppendDatRecord(byte[] data)
        {
            long pos = _datFile.Seek(0, SeekOrigin.End);
            _datFile.Write(BitConverter.GetBytes(data.Length), 0, 4);
            _datFile.Write(data, 0, data.Length);
            _datFile.Flush();
            return pos;
        }

        private string ReadDatRecord(long offset)
        {
            _datFile.Seek(offset, SeekOrigin.Begin);
            byte[] lb = new byte[4];
            _datFile.Read(lb, 0, 4);
            int len = BitConverter.ToInt32(lb, 0);
            if (len <= 0) return "";
            byte[] sb = new byte[len];
            _datFile.Read(sb, 0, len);
            return Encoding.UTF8.GetString(sb);
        }

        private void ValidateIndex(long index)
        {
            if (index < 0 || index >= _size)
                throw new IndexOutOfRangeException($"Индекс {index} вне [0,{_size - 1}].");
        }

        // ── IDisposable ──────────────────────────────────────────────────────────
        public void Dispose()
        {
            for (int i = 0; i < BUFFER_SIZE; i++)
                if (_buffer[i].Modified == 1)
                    FlushPage(i);

            _swapFile?.Flush(); _swapFile?.Close(); _swapFile?.Dispose();
            _datFile?.Flush(); _datFile?.Close(); _datFile?.Dispose();
        }
    }
}