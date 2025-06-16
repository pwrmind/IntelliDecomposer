using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace IntelliDecomposer.CLI
{
    public class TaskNode
    {
        public string Description { get; set; } = "";
        public List<TaskNode> SubTasks { get; set; } = new List<TaskNode>();

        public TaskNode(string description)
        {
            Description = description;
        }
    }

    public class OllamaGenerateRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "deepseek-coder-v2:latest";

        [JsonPropertyName("prompt")]
        public string Prompt { get; set; } = "";
    }

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
        private static readonly ConcurrentDictionary<string, bool> atomicityCache = new();

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
                if (await IsAtomic(subTask))
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

        static async Task<bool> IsAtomic(string task)
        {
            if (atomicityCache.TryGetValue(task, out bool cachedResult))
            {
                Console.WriteLine($"♻️ Использую кэшированный результат для: {task}");
                return cachedResult;
            }

            var prompt = $@"Проанализируй, является ли следующая задача атомарной (не требует дальнейшей декомпозиции). 
Ответь строго в формате JSON:
{{
  ""atomic"": true/false,
  ""reason"": ""краткое объяснение""
}}

Задача: {task}";

            Console.WriteLine($"🔬 Проверяю атомарность: {task}");
            var response = await CallOllamaApi(prompt);
            Console.WriteLine($"📄 Ответ на проверку атомарности: {response}");

            try
            {
                // Пытаемся найти JSON в ответе
                var jsonMatch = Regex.Match(response, @"\{.*\}");
                if (jsonMatch.Success)
                {
                    response = jsonMatch.Value;
                }

                using JsonDocument doc = JsonDocument.Parse(response);
                JsonElement root = doc.RootElement;

                if (root.TryGetProperty("atomic", out JsonElement atomicElement) &&
                    atomicElement.ValueKind == JsonValueKind.True)
                {
                    atomicityCache[task] = true;
                    return true;
                }
            }
            catch (JsonException)
            {
                // Fallback: анализ текстового ответа
                if (response.Contains("true", StringComparison.OrdinalIgnoreCase) ||
                    response.Contains("да", StringComparison.OrdinalIgnoreCase) ||
                    response.Contains("atomic", StringComparison.OrdinalIgnoreCase) ||
                    response.Contains("атомарн", StringComparison.OrdinalIgnoreCase))
                {
                    atomicityCache[task] = true;
                    return true;
                }
            }

            atomicityCache[task] = false;
            return false;
        }

        static void SaveTreeToFile(TaskNode root, string filePath)
        {
            using (var writer = new StreamWriter(filePath, false, Encoding.UTF8))
            {
                writer.WriteLine("## План декомпозиции задач ##\n");
                WriteNode(writer, root, 0);
            }
        }

        static void WriteNode(StreamWriter writer, TaskNode node, int level)
        {
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
                    task = "Ты - аналитик высшего уровня, и ты занимаешься декомпозицией задачи на атомарные подзадачи.\n" +
                        "Основная задача: создание экспертной системы по предсказанию погоды";
                }

                Console.WriteLine($"🎯 Главная задача: {task}\n");

                var rootNode = await DecomposeTask(task);

                SaveTreeToFile(rootNode, outputFile);
                Console.WriteLine($"\n💾 Результат сохранен в файл: {outputFile}\n");

                Console.WriteLine("\n🌳 Итоговый план задач:");
                Console.WriteLine(File.ReadAllText(outputFile));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"💥 Критическая ошибка: {ex.Message}\n\n");
                Console.WriteLine(ex.StackTrace);
            }
        }
    }
}