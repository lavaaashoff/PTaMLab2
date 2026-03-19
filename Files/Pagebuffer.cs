using System;

/// <summary>
/// Структура страницы, находящейся в памяти (слот буфера страниц).
/// Содержит атрибуты, необходимые для управления процессом замещения,
/// а также битовую карту и блок данных страницы.
/// </summary>
namespace PTaMLab2.Files
{
    public class PageBuffer
    {
        /// <summary>
        /// Абсолютный номер страницы в файле подкачки
        /// (порядковый номер страницы). Значение -1 означает, что слот свободен.
        /// </summary>
        public long AbsolutePageNumber { get; set; } = -1;

        /// <summary>
        /// Статус страницы (флаг модификации):
        ///   0 — страница не модифицировалась (запись не производилась);
        ///   1 — была запись на страницу (страница "грязная", требует выгрузки).
        /// </summary>
        public byte Modified { get; set; } = 0;

        /// <summary>
        /// Время загрузки страницы в память.
        /// Используется алгоритмом замещения FIFO для выбора жертвы —
        /// вытесняется страница с наименьшим (самым старым) значением LoadTime.
        /// </summary>
        public DateTime LoadTime { get; set; } = DateTime.MinValue;

        /// <summary>
        /// Битовая карта страницы. Каждый бит соответствует ячейке страницы.
        /// Значение 0 означает, что в ячейку ничего не записано.
        /// Значение 1 означает, что ячейка инициализирована.
        /// </summary>
        public byte[] Bitmap { get; set; }

        /// <summary>
        /// Блок данных страницы. Содержит байты значений элементов массива,
        /// находящихся на данной странице.
        /// </summary>
        public byte[] Data { get; set; }

        /// <summary>
        /// Конструктор: инициализирует буферы битовой карты и данных.
        /// </summary>
        /// <param name="bitmapBytes">Размер битовой карты в байтах.</param>
        /// <param name="pageDataBytes">Размер блока данных страницы в байтах.</param>
        public PageBuffer(int bitmapBytes, int pageDataBytes)
        {
            Bitmap = new byte[bitmapBytes];
            Data   = new byte[pageDataBytes];
        }

        /// <summary>
        /// Проверяет, установлен ли бит для ячейки с номером slotIndex в битовой карте.
        /// </summary>
        /// <param name="slotIndex">Порядковый номер ячейки на странице.</param>
        /// <returns>true если бит = 1 (ячейка инициализирована), иначе false.</returns>
        public bool IsBitSet(int slotIndex)
        {
            int byteIdx = slotIndex / 8;
            int bitIdx  = slotIndex % 8;
            return (Bitmap[byteIdx] & (1 << bitIdx)) != 0;
        }

        /// <summary>
        /// Устанавливает бит в 1 для ячейки с номером slotIndex в битовой карте.
        /// </summary>
        /// <param name="slotIndex">Порядковый номер ячейки на странице.</param>
        public void SetBit(int slotIndex)
        {
            int byteIdx = slotIndex / 8;
            int bitIdx  = slotIndex % 8;
            Bitmap[byteIdx] |= (byte)(1 << bitIdx);
        }
    }
}
