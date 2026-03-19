using PTaMLab2.Files;
using System;
using System.IO;
using System.Text.RegularExpressions;

/// <summary>
/// Тестирующая программа (консольное приложение) для работы с виртуальной памятью.
/// При запуске выводит подсказку VM> и ожидает команду.
///
/// Список команд (по заданию):
///   Create имя_файла(int | char(длина) | varchar(макс_длина)) — создать файл
///   Open имя_файла                — открыть существующий файл
///   Input (индекс, значение)      — записать значение в элемент массива
///   Print (индекс)                — вывести значение элемента массива
///   Help [имя_файла]              — справка (на экран или в файл)
///   Exit                          — завершить программу
/// </summary>
namespace PTaMLab2
{
    class Program
    {
        // Текущий объект виртуальной памяти и имя открытого файла
        private static VirtualMemory s_currentVm = null;
        private static string s_currentFile = null;

        static void Main()
        {
            while (true)
            {
                Console.Write("VM> ");
                string input = Console.ReadLine();

                if (string.IsNullOrWhiteSpace(input))
                    continue;

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
                        case "exit":
                            HandleExit();
                            return;
                        default:
                            Console.WriteLine(
                                "Неизвестная команда. Введите Help для справки.");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Ошибка: {ex.Message}");
                }
            }
        }

        // ── Обработчики команд ────────────────────────────────────────────────────

        /// <summary>
        /// Команда Create: создаёт все необходимые структуры в памяти и файлы на диске.
        /// Формат: Create имя_файла(int | char(длина) | varchar(макс_длина))
        /// Размерность массива по умолчанию: 10000 элементов.
        /// </summary>
        private static void HandleCreate(string commandLine)
        {
            // Разбор: Create имя_файла(тип)
            var match = Regex.Match(commandLine,
                @"^create\s+(.+?)\s*\((.+)\)\s*$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Console.WriteLine(
                    "Ошибка синтаксиса. Формат: " +
                    "Create имя_файла(int | char(длина) | varchar(макс_длина))");
                return;
            }

            string fileName = match.Groups[1].Value.Trim();
            string typeDef = match.Groups[2].Value.Trim().ToLower();

            string arrayType = "";
            int strLen = 0;

            if (typeDef == "int")
            {
                arrayType = "int";
            }
            else if (Regex.IsMatch(typeDef, @"^char\s*\(\s*\d+\s*\)$"))
            {
                arrayType = "char";
                strLen = int.Parse(Regex.Match(typeDef, @"\d+").Value);
                if (strLen <= 0)
                    throw new ArgumentException("Длина строки для char должна быть > 0.");
            }
            else if (Regex.IsMatch(typeDef, @"^varchar\s*\(\s*\d+\s*\)$"))
            {
                arrayType = "varchar";
                strLen = int.Parse(Regex.Match(typeDef, @"\d+").Value);
                if (strLen <= 0)
                    throw new ArgumentException(
                        "Максимальная длина строки для varchar должна быть > 0.");
            }
            else
            {
                Console.WriteLine(
                    "Неизвестный тип данных. Ожидается: int, char(длина), varchar(макс_длина).");
                return;
            }

            // Закрываем предыдущий файл, если открыт
            CloseCurrentVm();

            // Удаляем файлы, если существуют
            if (File.Exists(fileName))
                File.Delete(fileName);
            if (arrayType == "varchar")
            {
                string datPath = Path.ChangeExtension(fileName, ".dat");
                if (File.Exists(datPath))
                    File.Delete(datPath);
            }

            // Размерность массива: > 10000 по заданию
            const long ARRAY_SIZE = 10000;

            s_currentVm = new VirtualMemory(fileName, ARRAY_SIZE, arrayType, strLen);
            s_currentFile = fileName;

            Console.WriteLine(
                $"Файл '{fileName}' создан. " +
                $"Тип: {arrayType}, размер: {ARRAY_SIZE} элементов.");
        }

        /// <summary>
        /// Команда Open: открывает существующий файл подкачки.
        /// Считывает параметры из заголовка, создаёт структуры в памяти,
        /// загружает ≥ 3 страниц с модификацией атрибутов.
        /// Формат: Open имя_файла
        /// </summary>
        private static void HandleOpen(string commandLine)
        {
            var match = Regex.Match(commandLine,
                @"^open\s+(.+)\s*$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Console.WriteLine("Ошибка синтаксиса. Формат: Open имя_файла");
                return;
            }

            string fileName = match.Groups[1].Value.Trim();

            if (!File.Exists(fileName))
            {
                Console.WriteLine($"Ошибка: файл '{fileName}' не найден.");
                return;
            }

            // Считываем заголовок (первые 15 байт) без удержания файла открытым
            byte[] header = new byte[15];
            using (var fs = new FileStream(fileName, FileMode.Open,
                                           FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length < 15)
                    throw new InvalidDataException(
                        "Файл слишком мал или повреждён (< 15 байт заголовка).");
                fs.Read(header, 0, 15);
            } // fs закрывается здесь — до создания VirtualMemory

            // Разбираем заголовок
            if (header[0] != 'V' || header[1] != 'M')
                throw new InvalidDataException(
                    "Неверная сигнатура файла (ожидалось 'VM').");

            long storedSize = BitConverter.ToInt64(header, 2);

            char typeChar = (char)header[10];
            string arrayType = typeChar switch
            {
                'I' => "int",
                'C' => "char",
                'V' => "varchar",
                _ => throw new InvalidDataException(
                    $"Неизвестный тип элемента в файле: '{typeChar}'.")
            };

            int strLen = BitConverter.ToInt32(header, 11);

            // Закрываем предыдущий файл, если открыт
            CloseCurrentVm();

            s_currentVm = new VirtualMemory(fileName, storedSize, arrayType, strLen);
            s_currentFile = fileName;

            Console.WriteLine($"Файл '{fileName}' открыт. " +
                              $"Тип: {arrayType}, размер: {storedSize} элементов.");
        }

        /// <summary>
        /// Команда Input: записывает значение в элемент массива с номером индекс.
        /// Строковое значение обрамляется кавычками.
        /// Формат: Input (индекс, значение)
        /// </summary>
        private static void HandleInput(string commandLine)
        {
            if (s_currentVm == null)
            {
                Console.WriteLine(
                    "Ошибка: нет открытого файла. Используйте Create или Open.");
                return;
            }

            // Разбор: Input (индекс, значение)
            var match = Regex.Match(commandLine,
                @"^input\s*\(\s*(\d+)\s*,\s*(.*?)\s*\)\s*$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Console.WriteLine(
                    "Ошибка синтаксиса. Формат: Input (индекс, значение)");
                return;
            }

            long index = long.Parse(match.Groups[1].Value);
            string valueStr = match.Groups[2].Value;

            string type = s_currentVm.ArrayType;

            if (type == "int")
            {
                if (!long.TryParse(valueStr, out long val))
                {
                    Console.WriteLine("Ошибка: ожидалось целое числовое значение.");
                    return;
                }
                s_currentVm.WriteInt(index, val);
                Console.WriteLine($"Записано: [{index}] = {val}");
            }
            else
            {
                // Строка должна быть в кавычках
                if (!(valueStr.StartsWith("\"") && valueStr.EndsWith("\"")))
                {
                    Console.WriteLine(
                        "Ошибка: строковое значение должно быть заключено в кавычки.");
                    return;
                }
                string parsedStr = valueStr.Substring(1, valueStr.Length - 2);
                s_currentVm.WriteString(index, parsedStr);
                Console.WriteLine($"Записано: [{index}] = \"{parsedStr}\"");
            }
        }

        /// <summary>
        /// Команда Print: выводит на экран значение элемента массива с номером индекс.
        /// Формат: Print (индекс)
        /// </summary>
        private static void HandlePrint(string commandLine)
        {
            if (s_currentVm == null)
            {
                Console.WriteLine(
                    "Ошибка: нет открытого файла. Используйте Create или Open.");
                return;
            }

            var match = Regex.Match(commandLine,
                @"^print\s*\(\s*(\d+)\s*\)\s*$", RegexOptions.IgnoreCase);

            if (!match.Success)
            {
                Console.WriteLine("Ошибка синтаксиса. Формат: Print (индекс)");
                return;
            }

            long index = long.Parse(match.Groups[1].Value);

            if (!s_currentVm.IsInitialized(index))
            {
                Console.WriteLine($"[{index}]: (не инициализировано)");
                return;
            }

            if (s_currentVm.ArrayType == "int")
            {
                long val = s_currentVm.ReadInt(index);
                Console.WriteLine($"[{index}] = {val}");
            }
            else
            {
                string val = s_currentVm.ReadString(index) ?? "";
                Console.WriteLine($"[{index}] = \"{val}\"");
            }
        }

        /// <summary>
        /// Команда Help: выводит список команд на экран или сохраняет в файл.
        /// Формат: Help [имя_файла]
        /// </summary>
        private static void HandleHelp(string commandLine)
        {
            var match = Regex.Match(commandLine,
                @"^help\s*(.*)\s*$", RegexOptions.IgnoreCase);
            string targetFile = match.Groups[1].Value.Trim();

            string helpText = BuildHelpText();

            if (string.IsNullOrEmpty(targetFile))
            {
                Console.WriteLine(helpText);
            }
            else
            {
                File.WriteAllText(targetFile, helpText);
                Console.WriteLine($"Справка сохранена в файл '{targetFile}'.");
            }
        }

        /// <summary>
        /// Команда Exit: закрывает все файлы и завершает программу.
        /// Файлы виртуального массива при завершении не уничтожаются.
        /// </summary>
        private static void HandleExit()
        {
            CloseCurrentVm();
            Console.WriteLine("Программа завершена. " +
                              "Файлы виртуального массива сохранены.");
        }

        // ── Вспомогательные методы ────────────────────────────────────────────────

        /// <summary>Формирует текст справки по командам.</summary>
        private static string BuildHelpText()
        {
            return
                "Команды программы Virtual Memory (VM>):\n" +
                "\n" +
                "  Create имя_файла(int)\n" +
                "    — создать файл массива целых чисел (long, 8 байт).\n" +
                "\n" +
                "  Create имя_файла(char(длина))\n" +
                "    — создать файл массива строк фиксированной длины.\n" +
                "\n" +
                "  Create имя_файла(varchar(макс_длина))\n" +
                "    — создать файл массива строк произвольной длины.\n" +
                "\n" +
                "  Open имя_файла\n" +
                "    — открыть существующий файл подкачки.\n" +
                "\n" +
                "  Input (индекс, значение)\n" +
                "    — записать значение в элемент массива (строки в кавычках).\n" +
                "\n" +
                "  Print (индекс)\n" +
                "    — вывести значение элемента массива.\n" +
                "\n" +
                "  Help [имя_файла]\n" +
                "    — вывести справку на экран или сохранить в файл.\n" +
                "\n" +
                "  Exit\n" +
                "    — закрыть все файлы и завершить программу.\n";
        }

        /// <summary>Сбрасывает модифицированные страницы и закрывает текущий объект.</summary>
        private static void CloseCurrentVm()
        {
            if (s_currentVm != null)
            {
                s_currentVm.Dispose();
                s_currentVm = null;
                s_currentFile = null;
            }
        }
    }
}