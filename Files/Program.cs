using PTaMLab2.Files;
using System;
using System.IO;

class Program
{
    static void Main()
    {
        Console.WriteLine("╔══════════════════════════════════════╗");
        Console.WriteLine("║  Демонстрация VirtualMemory          ║");
        Console.WriteLine("╚══════════════════════════════════════╝\n");

        DemoInt();
        DemoChar();
        DemoVarchar();

        Console.WriteLine("\nВсе тесты завершены.");
    }

    // ── Режим int ────────────────────────────────────────────────────────────
    static void DemoInt()
    {
        Console.WriteLine("══ Режим INT ══════════════════════════════");
        const string f = "demo_int.vm";
        if (File.Exists(f)) File.Delete(f);

        using (var vm = new VirtualMemory(f, 10000, "int"))
        {
            vm.PrintInfo();

            // Запись
            vm.WriteInt(0, 42);
            vm.WriteInt(63, 1000);       // последний на 1-й странице
            vm.WriteInt(64, -7);          // первый на 2-й странице
            vm.WriteInt(9999, long.MaxValue);

            Console.WriteLine("\nЗапись и чтение:");
            foreach (long idx in new long[] { 0, 63, 64, 200, 9999 })
            {
                long v = vm.ReadInt(idx);
                Console.WriteLine($"  [{idx,5}] = {v,22}   init={vm.IsInitialized(idx)}");
            }

            // Перезапись
            vm.WriteInt(0, -999);
            Console.WriteLine($"\n  [0] после перезаписи = {vm.ReadInt(0)}");

            // Явное использование индексатора []
            Console.WriteLine("\nЧерез индексатор []:");
            vm[10] = 12345L;
            long read = vm[10];
            Console.WriteLine($"  vm[10] = {read}   (ожидается 12345)");
        } // Dispose: все модифицированные страницы сброшены на диск

        // Повторное открытие — проверяем сохранность
        Console.WriteLine("\nПовторное открытие:");
        using var vm2 = new VirtualMemory(f, 10000, "int");
        Console.WriteLine($"  [0]    = {vm2.ReadInt(0)}   (ожидается -999)");
        Console.WriteLine($"  [9999] = {vm2.ReadInt(9999)}   (ожидается {long.MaxValue})");
    }

    // ── Режим char (фиксированная строка) ───────────────────────────────────
    static void DemoChar()
    {
        Console.WriteLine("\n══ Режим CHAR (фикс. длина 20 байт) ══════");
        const string f = "demo_char.vm";
        const int STRLEN = 25;
        if (File.Exists(f)) File.Delete(f);

        using (var vm = new VirtualMemory(f, 10000, "char", STRLEN))
        {
            vm.PrintInfo();

            vm.WriteString(0, "Привет мир");
            vm.WriteString(127, "last on page 0");
            vm.WriteString(128, "first on page 1");
            vm.WriteString(9999, "конец массива");

            Console.WriteLine("\nЗапись и чтение:");
            foreach (long idx in new long[] { 0, 127, 128, 500, 9999 })
            {
                string v = vm.ReadString(idx);
                Console.WriteLine($"  [{idx,5}] = \"{v ?? "null"}\"   init={vm.IsInitialized(idx)}");
            }
        } // Dispose до открытия vm2

        Console.WriteLine("\nПовторное открытие:");
        using var vm2 = new VirtualMemory(f, 10000, "char", STRLEN);
        Console.WriteLine($"  [0]    = \"{vm2.ReadString(0)}\"");
        Console.WriteLine($"  [9999] = \"{vm2.ReadString(9999)}\"");
    }

    // ── Режим varchar ────────────────────────────────────────────────────────
    static void DemoVarchar()
    {
        Console.WriteLine("\n══ Режим VARCHAR (maxLen=100) ══════════════");
        const string f = "demo_varchar.vm";
        const string fDat = "demo_varchar.dat";
        const int MAXLEN = 100;
        if (File.Exists(f)) File.Delete(f);
        if (File.Exists(fDat)) File.Delete(fDat);

        using (var vm = new VirtualMemory(f, 10000, "varchar", MAXLEN))
        {
            vm.PrintInfo();

            vm.WriteString(0, "Короткая");
            vm.WriteString(1, "Строка средней длины для теста");
            vm.WriteString(63, "последняя на странице 0");
            vm.WriteString(64, "первая на странице 1");
            vm.WriteString(9999, "самая последняя запись в массиве");

            Console.WriteLine("\nЗапись и чтение:");
            foreach (long idx in new long[] { 0, 1, 63, 64, 500, 9999 })
            {
                string v = vm.ReadString(idx);
                Console.WriteLine($"  [{idx,5}] = \"{v ?? "null"}\"   init={vm.IsInitialized(idx)}");
            }
        } // Dispose до открытия vm2

        Console.WriteLine("\nПовторное открытие:");
        using var vm2 = new VirtualMemory(f, 10000, "varchar", MAXLEN);
        Console.WriteLine($"  [0]    = \"{vm2.ReadString(0)}\"");
        Console.WriteLine($"  [9999] = \"{vm2.ReadString(9999)}\"");
    }
}