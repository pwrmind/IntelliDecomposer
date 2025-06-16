using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;

namespace IntelliDecomposer.CLI
{
    // Класс для представления узла дерева задач
    public class TaskNode
    {
        public string Description { get; set; } = "";
        public List<TaskNode> SubTasks { get; set; } = new List<TaskNode>();

        public TaskNode(string description)
        {
            Description = description;
        }
    }

    // Класс для запроса к Ollama API
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

            var requestBody = JsonSerializer.Serialize(request);
            var content = new StringContent(requestBody, Encoding.UTF8, "application/json");

            Console.WriteLine("🚀 Отправляю запрос к Ollama API...");
            Console.WriteLine($"🔧 Модель: {request.Model}");
            Console.WriteLine($"📝 Промпт: {prompt[..Math.Min(prompt.Length, 50)]}...");

            var response = await client.PostAsync(url, content);
            response.EnsureSuccessStatusCode();
            Console.WriteLine("✅ Ответ успешно получен");

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

        static async Task<TaskNode> DecomposeTask(string task)
        {
            var formattedTask = $"{task}\n\nПожалуйста, верни декомпозированные задачи в формате:\n" +
                "```list\n" +
                "- Задача 1\n" +
                "- Задача 2\n" +
                "```\n";

            Console.WriteLine($"🔍 Декомпозирую задачу: {task}");
            var response = await CallOllamaApi(formattedTask);

            Console.WriteLine($"📋 Сырой ответ от API:\n{response}");

            var decomposedTasks = ParseDecomposedTasks(response);
            Console.WriteLine($"📌 Найдено подзадач: {decomposedTasks.Count}");

            TaskNode currentNode = new TaskNode(task);

            foreach (var subTask in decomposedTasks)
            {
                Console.WriteLine($"🔎 Анализирую подзадачу: {subTask}");
                if (IsAtomic(subTask))
                {
                    Console.WriteLine($"🟢 Атомарная: {subTask}");
                    currentNode.SubTasks.Add(new TaskNode(subTask));
                }
                else
                {
                    Console.WriteLine($"🔄 Рекурсивная декомпозиция: {subTask}");
                    var subNode = await DecomposeTask(subTask);
                    currentNode.SubTasks.Add(subNode);
                }
            }

            return currentNode;
        }

        static List<string> ParseDecomposedTasks(string response)
        {
            var tasks = new List<string>();
            var lines = response.Split('\n');
            bool inListBlock = false;

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();

                if (trimmedLine.Contains("```list"))
                {
                    Console.WriteLine("📋 Найден блок list");
                    inListBlock = true;
                    continue;
                }

                if (trimmedLine.StartsWith("```") && inListBlock)
                {
                    inListBlock = false;
                    continue;
                }

                if (inListBlock && !string.IsNullOrWhiteSpace(trimmedLine))
                {
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
            return task.Split(' ').Length < 8;
        }

        // Метод для сохранения дерева в файл
        static void SaveTreeToFile(TaskNode root, string filePath)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine("## План декомпозиции задач ##\n");
                WriteNode(writer, root, 0);
            }
        }

        // Рекурсивный метод записи узлов дерева
        static void WriteNode(StreamWriter writer, TaskNode node, int level)
        {
            // Создаем отступ в зависимости от уровня вложенности
            string indent = new string(' ', level * 2);
            writer.WriteLine($"{indent}- {node.Description}");

            foreach (var child in node.SubTasks)
            {
                WriteNode(writer, child, level + 1);
            }
        }

        static async Task Main(string[] args)
        {
            Console.InputEncoding = Encoding.UTF8;
            Console.OutputEncoding = Encoding.UTF8;

            try
            {
                string task = "";
                string outputFile = "decomposition_plan.md";

                if (args.Length > 0)
                {
                    task = string.Join(" ", args);
                }
                else
                {
                    task = @"Ты - аналитик высшего уровня, и ты занимаешься декомпозицией задачи на атомарные подзадачи.\n" +
                        @"Основная задача: создание программы для управления задачами";
                }

                Console.WriteLine($"🎯 Главная задача: {task}");

                var rootNode = await DecomposeTask(task);

                // Сохраняем результат в файл
                SaveTreeToFile(rootNode, outputFile);
                Console.WriteLine($"\n💾 Результат сохранен в файл: {outputFile}");

                // Дополнительно выводим дерево в консоль
                Console.WriteLine("\n🌳 Итоговый план задач:");
                Console.WriteLine(File.ReadAllText(outputFile));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Критическая ошибка: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}