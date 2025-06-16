using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace IntelliDecomposer.CLI
{
    // Новый класс для запроса к Ollama API
    public class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "deepseek-coder-v2:latest";

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";
    }

    // Класс для обработки фрагментов ответа
    public class OllamaResponseChunk
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("response")]
        public string Response { get; set; } = "";

        [JsonPropertyName("done")]
        public bool Done { get; set; }
    }

    class Program
    {
        private static readonly HttpClient client = new HttpClient();

        static async Task<string> CallOllamaApi(string prompt)
        {
            var url = "http://localhost:11434/api/generate";
            var request = new OllamaGenerateRequest { Prompt = prompt };

            // 🔄 Сериализация запроса
            var requestBody = JsonSerializer.Serialize(request);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            // 📤 Отправка запроса
            Console.WriteLine("🚀 Отправляю запрос к Ollama API...");
            Console.WriteLine($"🔧 Модель: {request.Model}");
            Console.WriteLine($"📝 Промпт: {prompt[..Math.Min(prompt.Length, 50)]}...");

            var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            Console.WriteLine("✅ Ответ успешно получен");

            // 📥 Чтение потока ответа
            var responseStream = await response.Content.ReadAsStreamAsync();
            using var reader = new StreamReader(responseStream);

            StringBuilder fullResponse = new StringBuilder();
            string line;

            Console.WriteLine("🧩 Собираю фрагменты ответа...");
            while ((line = await reader.ReadLineAsync()) != null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    // 🔍 Десериализация фрагмента
                    var chunk = JsonSerializer.Deserialize<OllamaResponseChunk>(line);
                    if (chunk == null) continue;

                    fullResponse.Append(chunk.Response);

                    if (chunk.Done)
                    {
                        Console.WriteLine("🧩 Последний фрагмент получен");
                        break;
                    }
                }
                catch (JsonException ex)
                {
                    Console.WriteLine($"⚠️ Ошибка парсинга JSON: {ex.Message}");
                }
            }

            var result = fullResponse.ToString();
            Console.WriteLine($"📨 Полный ответ ({result.Length} символов)");
            return result;
        }

        static async Task<List<string>> DecomposeTask(string task)
        {
            // ✨ Форматируем запрос с инструкцией
            var formattedTask = $"{task}\n\nПожалуйста, верни декомпозированные задачи в формате:\n" +
                "```list\n" +
                "- Задача 1\n" +
                "- Задача 2\n" +
                "```\n";

            Console.WriteLine($"🔍 Декомпозирую задачу: {task}");
            var response = await CallOllamaApi(formattedTask);

            // 📝 Логируем сырой ответ
            Console.WriteLine($"📋 Сырой ответ от API:\n{response}");

            var decomposedTasks = ParseDecomposedTasks(response);
            Console.WriteLine($"📌 Найдено подзадач: {decomposedTasks.Count}");

            // ♻️ Рекурсивная декомпозиция
            List<string> atomicTasks = new List<string>();
            foreach (var subTask in decomposedTasks)
            {
                Console.WriteLine($"🔎 Анализирую подзадачу: {subTask}");
                if (IsAtomic(subTask))
                {
                    Console.WriteLine($"🟢 Атомарная: {subTask}");
                    atomicTasks.Add(subTask);
                }
                else
                {
                    Console.WriteLine($"🔄 Рекурсивная декомпозиция: {subTask}");
                    var subAtomicTasks = await DecomposeTask(subTask);
                    atomicTasks.AddRange(subAtomicTasks);
                }
            }

            return atomicTasks;
        }

        static List<string> ParseDecomposedTasks(string response)
        {
            var tasks = new List<string>();
            var lines = response.Split('\n');
            bool inListBlock = false;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                // 🔍 Поиск маркера начала списка
                if (trimmedLine.Contains("```list"))
                {
                    Console.WriteLine("📋 Найден блок list");
                    inListBlock = true;
                    continue;
                }

                // 🛑 Конец блока
                if (trimmedLine.StartsWith("```") && inListBlock)
                {
                    inListBlock = false;
                    continue;
                }

                // ✨ Добавление элементов списка
                if (inListBlock && !string.IsNullOrWhiteSpace(trimmedLine))
                {
                    // Убираем маркеры списка (-, *, • и т.д.)
                    var cleanLine = trimmedLine.TrimStart('-', '*', '•', ' ');
                    if (!string.IsNullOrEmpty(cleanLine))
                    {
                        tasks.Add(cleanLine);
                    }
                }
            }

            return tasks;
        }

        static bool IsAtomic(string task)
        {
            // 🧪 Упрощенная логика определения атомарности
            return task.Split(' ').Length < 8; // Задачи с <5 словами считаем атомарными
        }

        static async Task Main(string[] args)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                var task = @"Ты - аналитик высшего уровня, и ты занимаешься декомпозицией задачи на атомарные n\" +
                    @"Озновная задача которую надо декомпозировать на подзадачи: создание программы для управления задачами";
                Console.WriteLine($"🎯 Главная задача: {task}");

                var atomicTasks = await DecomposeTask(task);

                Console.WriteLine("\n📋 Итоговый список атомарных задач:");
                foreach (var atomicTask in atomicTasks)
                {
                    Console.WriteLine($"✅ {atomicTask}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Критическая ошибка: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}