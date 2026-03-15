namespace PTaMLab2.Files
{
    internal class Program
    {
        static void Main(string[] args)
        {
            const string swapFile = "swap.vm";
            const long SIZE = 10000;

            // Удаляем старый файл, чтобы каждый запуск был чистым
            if (File.Exists(swapFile))
                File.Delete(swapFile);

            using var vm = new VirtualMemory(swapFile, SIZE);

            // Информация о файле подкачки
            vm.PrintInfo();

            // Запись нескольких элементов
            Console.WriteLine("\nЗапись элементов");
            long[] indices = { 0, 63, 64, 100, 9999 };
            long[] values = { 42, 1000, -7, 123456789, long.MaxValue };

            for (int i = 0; i < indices.Length; i++)
            {
                vm[indices[i]] = values[i];
                Console.WriteLine($"  vm[{indices[i],6}] = {values[i]}");
            }

            // Чтение и проверка
            Console.WriteLine("\nЧтение и проверка");
            foreach (long idx in indices)
            {
                long read = vm[idx];
                bool init = vm.IsInitialized(idx);
                Console.WriteLine($"  vm[{idx,6}] = {read,22}   инициализирована: {init}");
            }

            // Чтение неинициализированной ячейки
            Console.WriteLine("\nНеинициализированная ячейка");
            long uninitIdx = 500;
            Console.WriteLine($"  IsInitialized({uninitIdx}) = {vm.IsInitialized(uninitIdx)}");
            Console.WriteLine($"  vm[{uninitIdx}] = {vm[uninitIdx]}  (ожидается 0)");

            // Перезапись значения
            Console.WriteLine("\nПерезапись");
            vm[0] = -999;
            Console.WriteLine($"  vm[0] после перезаписи = {vm[0]}");

            // Проверка корректности файла при повторном открытии
            Console.WriteLine("\nПовторное открытие файла");
            using var vm2 = new VirtualMemory(swapFile, SIZE);
            Console.WriteLine($"  vm2[0]    = {vm2[0]}   (ожидается -999)");
            Console.WriteLine($"  vm2[9999] = {vm2[9999]}   (ожидается {long.MaxValue})");

            Console.WriteLine("\nГотово. Файл подкачки: " + new FileInfo(swapFile).Length + " байт.");
        }
    }
}
