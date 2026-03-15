using PTaMLab2.Files;
using System;
using System.IO;
using System.Text.RegularExpressions;

namespace PTaMLab2
{
    class Program
    {
        static VirtualMemory currentVm = null;
        static string currentFileName = null;

        static void Main()
        { 
            while (true)
            {
                Console.Write("VM> ");
                string input = Console.ReadLine();
                if (string.IsNullOrWhiteSpace(input)) continue;

                string commandLine = input.Trim();
                string command = commandLine.Split(' ')[0].ToLower();

                try
                {
                    switch (command)
                    {
                        case "create":
                            HandleCreate(commandLine);
                            break;
                        case "open":
                            HandleOpen(commandLine);
                            break;
                        case "input":
                            HandleInput(commandLine);
                            break;
                        case "print":
                            HandlePrint(commandLine);
                            break;
                        case "help":
                            HandleHelp(commandLine);
                            break;
                        case "demo":
                            RunDemos();
                            break;
                        case "exit":
                            HandleExit();
                            return;
                        default:
                            Console.WriteLine("Неизвестная команда. Введите Help для справки.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка: {ex.Message}");
                    Console.WriteLine("Введите Help для списка команд");
                }
            }
        }

        // ── Обработчики команд оболочки ─────────────────────────────────────────

        static void HandleCreate(string commandLine)
        {
            var match = Regex.Match(commandLine, @"^create\s+(.+?)\s*\((.+)\)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                Console.WriteLine("Ошибка синтаксиса. Формат: Create имя_файла(int | char(длина) | varchar(макс_длина))");
                return;
            }

            string fileName = match.Groups[1].Value.Trim();
            string typeDef = match.Groups[2].Value.Trim().ToLower();

            CloseCurrentVm();

            string arrayType = "";
            int strLen = 0;

            if (typeDef == "int")
            {
                arrayType = "int";
            }
            else if (typeDef.StartsWith("char"))
            {
                arrayType = "char";
                var m = Regex.Match(typeDef, @"char\s*\(\s*(\d+)\s*\)");
                if (m.Success) strLen = int.Parse(m.Groups[1].Value);
                else throw new ArgumentException("Неверно указана длина для char.");
            }
            else if (typeDef.StartsWith("varchar"))
            {
                arrayType = "varchar";
                var m = Regex.Match(typeDef, @"varchar\s*\(\s*(\d+)\s*\)");
                if (m.Success) strLen = int.Parse(m.Groups[1].Value);
                else throw new ArgumentException("Неверно указана длина для varchar.");
            }
            else
            {
                throw new ArgumentException("Неизвестный тип данных. Ожидается: int, char(длина), varchar(макс_длина).");
            }

            if (File.Exists(fileName)) File.Delete(fileName);
            if (arrayType == "varchar" && File.Exists(Path.ChangeExtension(fileName, ".dat")))
                File.Delete(Path.ChangeExtension(fileName, ".dat"));

            long arraySize = 10000;

            currentVm = new VirtualMemory(fileName, arraySize, arrayType, strLen);
            currentFileName = fileName;

            Console.WriteLine($"Файл {fileName} успешно создан (тип: {arrayType}, размер: {arraySize} элементов).");
        }

        static void HandleOpen(string commandLine)
        {
            var match = Regex.Match(commandLine, @"^open\s+(.+)$", RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                Console.WriteLine("Ошибка синтаксиса. Формат: Open имя_файла");
                return;
            }

            string fileName = match.Groups[1].Value.Trim();

            if (!File.Exists(fileName))
            {
                Console.WriteLine($"Ошибка: Файл {fileName} не существует.");
                return;
            }

            CloseCurrentVm();

            using (var fs = new FileStream(fileName, FileMode.Open, FileAccess.Read))
            {
                if (fs.Length < 15) throw new InvalidDataException("Файл слишком мал или повреждён.");

                int b1 = fs.ReadByte();
                int b2 = fs.ReadByte();

                if (b1 != 'V' || b2 != 'M')
                    throw new InvalidDataException("Неверная сигнатура файла.");

                byte[] sizeBuf = new byte[8];
                fs.Read(sizeBuf, 0, 8);
                long storedSize = BitConverter.ToInt64(sizeBuf, 0);

                char storedType = (char)fs.ReadByte();

                string arrayType = storedType switch
                {
                    'I' => "int",
                    'C' => "char",
                    'V' => "varchar",
                    _ => throw new InvalidDataException("Неизвестный тип данных в файле.")
                };

                byte[] lenBuf = new byte[4];
                fs.Read(lenBuf, 0, 4);
                int strLen = BitConverter.ToInt32(lenBuf, 0);

                currentVm = new VirtualMemory(fileName, storedSize, arrayType, strLen);
                currentFileName = fileName;
            }

            Console.WriteLine($"Файл {fileName} успешно открыт.");
        }

        static void HandleInput(string commandLine)
        {
            if (currentVm == null)
            {
                Console.WriteLine("Ошибка: Нет открытого файла. Сначала используйте Create или Open.");
                return;
            }

            var match = Regex.Match(commandLine, @"^input\s*\(\s*(\d+)\s*,\s*(.*?)\s*\)$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Console.WriteLine("Ошибка синтаксиса. Формат: Input (индекс, значение)");
                return;
            }

            long index = long.Parse(match.Groups[1].Value);
            string valueStr = match.Groups[2].Value;

            var typeField = typeof(VirtualMemory).GetField("_arrayType",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            string type = (string)typeField?.GetValue(currentVm) ?? "int";

            if (type == "int")
            {
                if (int.TryParse(valueStr, out int val))
                    currentVm.WriteInt(index, val);
                else
                    Console.WriteLine("Ошибка: Ожидалось числовое значение для int.");
            }
            else
            {
                if (valueStr.StartsWith("\"") && valueStr.EndsWith("\""))
                {
                    string parsedString = valueStr.Substring(1, valueStr.Length - 2);
                    currentVm.WriteString(index, parsedString);
                }
                else
                {
                    Console.WriteLine("Ошибка: Строковое значение должно быть в кавычках.");
                }
            }
        }

        static void HandlePrint(string commandLine)
        {
            if (currentVm == null)
            {
                Console.WriteLine("Ошибка: Нет открытого файла.");
                return;
            }

            var match = Regex.Match(commandLine, @"^print\s*\(\s*(\d+)\s*\)$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Console.WriteLine("Ошибка синтаксиса. Формат: Print (индекс)");
                return;
            }

            long index = long.Parse(match.Groups[1].Value);

            var typeField = typeof(VirtualMemory).GetField("_arrayType",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);

            string type = (string)typeField?.GetValue(currentVm) ?? "int";

            if (!currentVm.IsInitialized(index))
            {
                Console.WriteLine($"[Индекс {index}]: (Не инициализировано)");
                return;
            }

            if (type == "int")
            {
                long val = currentVm.ReadInt(index);
                Console.WriteLine($"[Индекс {index}]: {val}");
            }
            else
            {
                string val = currentVm.ReadString(index);
                Console.WriteLine($"[Индекс {index}]: \"{val ?? ""}\"");
            }
        }

        static void HandleHelp(string commandLine)
        {
            var match = Regex.Match(commandLine, @"^help\s*(.*)$", RegexOptions.IgnoreCase);
            string targetFile = match.Groups[1].Value.Trim();

            if (string.IsNullOrEmpty(targetFile))
            {
                DisplayHelpToConsole();
            }
            else
            {
                try
                {
                    SaveHelpToFile(targetFile);
                    Console.WriteLine($"Справка сохранена в файл {targetFile}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка при сохранении справки: {ex.Message}");
                }
            }
        }

        static void DisplayHelpToConsole()
        {
            Console.WriteLine("Доступные команды:");
            Console.WriteLine("  Create <имя_файла>(int)                - создать файл массива целых чисел");
            Console.WriteLine("  Create <имя_файла>(char(<длина>))      - создать файл строк фиксированной длины");
            Console.WriteLine("  Create <имя_файла>(varchar(<длина>))   - создать файл строк переменной длины");
            Console.WriteLine("  Open <имя_файла>                       - открыть существующий файл");
            Console.WriteLine("  Input (<индекс>, <значение>)           - записать значение в элемент массива");
            Console.WriteLine("  Print (<индекс>)                       - вывести значение элемента массива");
            Console.WriteLine("  Demo                                   - запуск хардкод-демонстраций (int, char, varchar)");
            Console.WriteLine("  Help [имя_файла]                       - показать справку или сохранить в файл");
            Console.WriteLine("  Exit                                   - выход из программы");
        }

        static void SaveHelpToFile(string filename)
        {
            using (var writer = new StreamWriter(filename))
            {
                writer.WriteLine("Справка по командам Virtual Memory Console:");
                writer.WriteLine();
                writer.WriteLine("  Create <имя_файла>(int)");
                writer.WriteLine("  Create <имя_файла>(char(<длина>))");
                writer.WriteLine("  Create <имя_файла>(varchar(<длина>))");
                writer.WriteLine("  Open <имя_файла>");
                writer.WriteLine("  Input (<индекс>, <значение>)");
                writer.WriteLine("  Print (<индекс>)");
                writer.WriteLine("  Demo");
                writer.WriteLine("  Help [имя_файла]");
                writer.WriteLine("  Exit");
            }
        }

        static void HandleExit()
        {
            CloseCurrentVm();
            Console.WriteLine("Программа завершена.");
        }

        static void CloseCurrentVm()
        {
            if (currentVm != null)
            {
                currentVm.Dispose();
                currentVm = null;
                currentFileName = null;
            }
        }

        // ── Хардкод демонстрации (вызываются командой demo) ──────────────────────

        static void RunDemos()
        {
            Console.WriteLine("\nЗапуск демонстраций...\n");
            DemoInt();
            DemoChar();
            DemoVarchar();
            Console.WriteLine("\nВсе тесты завершены.\n");
        }

        static void DemoInt()
        {
            Console.WriteLine("══ Режим INT ══════════════════════════════");
            const string f = "demo_int.vm";
            if (File.Exists(f)) File.Delete(f);

            using (var vm = new VirtualMemory(f, 10000, "int"))
            {
                vm.PrintInfo();

                vm.WriteInt(0, 42);
                vm.WriteInt(63, 1000);
                vm.WriteInt(64, -7);
                vm.WriteInt(9999, long.MaxValue);

                Console.WriteLine("\nЗапись и чтение:");
                foreach (long idx in new long[] { 0, 63, 64, 200, 9999 })
                {
                    long v = vm.ReadInt(idx);
                    Console.WriteLine($"  [{idx,5}] = {v,22}   init={vm.IsInitialized(idx)}");
                }

                vm.WriteInt(0, -999);
                Console.WriteLine($"\n  [0] после перезаписи = {vm.ReadInt(0)}");

                Console.WriteLine("\nЧерез индексатор []:");
                vm[10] = 12345L;
                long read = vm[10];
                Console.WriteLine($"  vm[10] = {read}   (ожидается 12345)");
            }

            Console.WriteLine("\nПовторное открытие:");
            using var vm2 = new VirtualMemory(f, 10000, "int");
            Console.WriteLine($"  [0]    = {vm2.ReadInt(0)}   (ожидается -999)");
            Console.WriteLine($"  [9999] = {vm2.ReadInt(9999)}   (ожидается {long.MaxValue})");
        }

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
            }

            Console.WriteLine("\nПовторное открытие:");
            using var vm2 = new VirtualMemory(f, 10000, "char", STRLEN);
            Console.WriteLine($"  [0]    = \"{vm2.ReadString(0)}\"");
            Console.WriteLine($"  [9999] = \"{vm2.ReadString(9999)}\"");
        }

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
            }

            Console.WriteLine("\nПовторное открытие:");
            using var vm2 = new VirtualMemory(f, 10000, "varchar", MAXLEN);
            Console.WriteLine($"  [0]    = \"{vm2.ReadString(0)}\"");
            Console.WriteLine($"  [9999] = \"{vm2.ReadString(9999)}\"");
        }
    }
}