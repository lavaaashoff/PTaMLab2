using System;

/// <summary>
/// Структура страницы, находящейся в памяти (буфер страниц).
/// </summary>

namespace PTaMLab2.Files
{
    public class PageBuffer
    {
        // Абсолютный номер страницы в файле (-1 = слот свободен)
        public long AbsolutePageNumber { get; set; } = -1;

        // Флаг модификации: 0 — не изменялась, 1 — была запись
        public byte Modified { get; set; } = 0;

        // Время загрузки страницы в память (для алгоритма замещения FIFO)
        public DateTime LoadTime { get; set; } = DateTime.MinValue;

        // Битовая карта страницы (инициализируется под нужный размер)
        public byte[] Bitmap { get; set; }

        // Байты данных страницы (512 байт или больше для режима C)
        public byte[] Data { get; set; }

        public PageBuffer(int bitmapBytes, int pageDataBytes)
        {
            Bitmap = new byte[bitmapBytes];
            Data = new byte[pageDataBytes];
        }

        // Проверить бит в битовой карте по позиции слота на странице
        public bool IsBitSet(int slotIndex)
        {
            int byteIdx = slotIndex / 8;
            int bitIdx = slotIndex % 8;
            return (Bitmap[byteIdx] & (1 << bitIdx)) != 0;
        }

        // Установить бит в битовой карте
        public void SetBit(int slotIndex)
        {
            int byteIdx = slotIndex / 8;
            int bitIdx = slotIndex % 8;
            Bitmap[byteIdx] |= (byte)(1 << bitIdx);
        }
    }
}