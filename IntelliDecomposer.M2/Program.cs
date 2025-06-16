using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

public enum TaskStatus
{
    New,
    Evaluating,
    Decomposing,
    Completed,
    MaxDepthExceeded,
    Failed,
    WaitingForChildren
}

public class TaskNode
{
    public string Description { get; set; }
    public TaskStatus Status { get; set; }
    public int Depth { get; set; }

    [JsonIgnore]
    public TaskNode Parent { get; set; }

    public List<TaskNode> Children { get; } = new List<TaskNode>();

    public string FullContextPath
    {
        get
        {
            var path = new List<string>();
            var current = this;
            while (current != null)
            {
                path.Add(current.Description);
                current = current.Parent;
            }
            path.Reverse();
            return string.Join(" → ", path);
        }
    }
}

class Program
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private static TaskNode _rootTask;
    private static int _maxDepth = 4;
    private static string _saveFilePath = "task_tree.json";
    private static readonly object _fileLock = new object();
    private static int _requestCounter = 0;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.InputEncoding = Encoding.UTF8;

        Console.WriteLine("Введите основную задачу:");
        string rootDescription = Console.ReadLine();

        if (string.IsNullOrEmpty(rootDescription))
        {
            rootDescription = "Создание экспертной системы для предсказания погоды по данным получаемым от человека о природных явлениях, поведении животных и растений";
        }

        _rootTask = new TaskNode
        {
            Description = rootDescription,
            Status = TaskStatus.New,
            Depth = 0
        };

        await ProcessTask(_rootTask);
        SaveTreeToFile();
        Console.WriteLine("\nДекомпозиция завершена! Результаты сохранены в " + _saveFilePath);
    }

    private static async Task ProcessTask(TaskNode task)
    {
        // Обновление статуса и отображение
        task.Status = TaskStatus.Evaluating;
        
        PrintTree();

        // Проверка глубины
        if (task.Depth >= _maxDepth)
        {
            task.Status = TaskStatus.MaxDepthExceeded;
            SaveTreeToFile();
            return;
        }

        // Запрос к LLM о необходимости декомпозиции с контекстом
        bool shouldDecompose = await CheckDecompositionNeedAsync(task);

        if (!shouldDecompose)
        {
            task.Status = TaskStatus.Completed;
            SaveTreeToFile();
            return;
        }

        // Декомпозиция задачи с полным контекстом
        task.Status = TaskStatus.Decomposing;
        PrintTree();

        List<string> subtasks = await DecomposeTaskAsync(task);

        if (subtasks == null || subtasks.Count == 0)
        {
            task.Status = TaskStatus.Completed;
            SaveTreeToFile();
            return;
        }

        // Создание подзадач с указанием родителя
        task.Status = TaskStatus.WaitingForChildren;
        foreach (var subtask in subtasks)
        {
            task.Children.Add(new TaskNode
            {
                Description = subtask,
                Depth = task.Depth + 1,
                Status = TaskStatus.New,
                Parent = task
            });
        }

        SaveTreeToFile();
        PrintTree();

        // Параллельная обработка подзадач
        var processingTasks = new List<Task>();
        foreach (var child in task.Children)
        {
            processingTasks.Add(ProcessTask(child));
        }
        await Task.WhenAll(processingTasks);

        // Обновление статуса после завершения детей
        task.Status = TaskStatus.Completed;
        SaveTreeToFile();
    }

    private static async Task<bool> CheckDecompositionNeedAsync(TaskNode task)
    {
        string contextPrompt = BuildContextPrompt(task);

        string prompt = $@"{contextPrompt}

[Вопрос]
Требуется ли декомпозиция последней задачи ({task.Description}) на подзадачи? 
Учти историю декомпозиции и текущий уровень вложенности ({task.Depth}/{_maxDepth}). 
Ответь только JSON: {{""decompose"": true/false, ""reason"": ""краткое обоснование""}}";

        try
        {
            string response = await GetLlmResponseAsync(prompt);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            bool shouldDecompose = result.GetProperty("decompose").GetBoolean();
            string reason = result.GetProperty("reason").GetString();

            Console.WriteLine($"\nОценка декомпозиции: {(shouldDecompose ? "ТРЕБУЕТСЯ" : "НЕ ТРЕБУЕТСЯ")}");
            Console.WriteLine($"Причина: {reason}");

            return shouldDecompose;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<List<string>> DecomposeTaskAsync(TaskNode task)
    {
        string contextPrompt = BuildContextPrompt(task);

        string prompt = $@"{contextPrompt}

[Инструкция]
Декомпозируй последнюю задачу ({task.Description}) на 3-7 конкретных подзадач, учитывая:
1. Весь контекст родительских задач
2. Текущий уровень вложенности ({task.Depth}/{_maxDepth})
3. Подзадачи должны быть независимыми и выполнимыми
4. Избегай избыточной детализации

Ответь только в формате JSON: {{
  ""subtasks"": [""задача1"", ""задача2"", ...],
  ""rationale"": ""логика декомпозиции""
}}";

        try
        {
            string response = await GetLlmResponseAsync(prompt);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            // Логируем rationale
            string rationale = result.GetProperty("rationale").GetString();
            Console.WriteLine($"\nДекомпозиция задачи: {task.Description}");
            Console.WriteLine($"Логика: {rationale}");

            var subtasks = new List<string>();
            foreach (var taskElem in result.GetProperty("subtasks").EnumerateArray())
            {
                subtasks.Add(taskElem.GetString());
            }

            Console.WriteLine($"Сгенерировано подзадач: {subtasks.Count}");
            return subtasks;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string BuildContextPrompt(TaskNode task)
    {
        var contextBuilder = new StringBuilder("[Контекст декомпозиции]\n");

        var current = task;
        var path = new Stack<string>();

        while (current != null)
        {
            path.Push(current.Description);
            current = current.Parent;
        }

        int level = 1;
        while (path.Count > 0)
        {
            contextBuilder.AppendLine($"Уровень {level++}: {path.Pop()}");
        }

        return contextBuilder.ToString();
    }

    private static async Task<string> GetLlmResponseAsync(string prompt)
    {
        _requestCounter++;
        Console.WriteLine($"\n⌛ Запрос #{_requestCounter} к LLM...");

        var request = new
        {
            model = "qwen3:8b",
            prompt = prompt,
            format = "json",
            stream = false,
            options = new { temperature = 0.3 }
        };

        var content = new StringContent(
            JsonSerializer.Serialize(request),
            Encoding.UTF8,
            "application/json"
        );

        var response = await _httpClient.PostAsync(
            "http://localhost:11434/api/generate",
            content
        );

        var responseContent = await response.Content.ReadAsStringAsync();
        var jsonResponse = JsonSerializer.Deserialize<JsonElement>(responseContent);

        Console.WriteLine($"✅ Ответ #{_requestCounter} получен");
        return jsonResponse.GetProperty("response").GetString();
    }

    private static void PrintTree()
    {
        Console.Clear();
        Console.WriteLine($"🌳 Дерево задач [Глубина: {_maxDepth} | Запросов: {_requestCounter}]\n");
        PrintNode(_rootTask, 0);
        Console.WriteLine("\n🔄 Автосохранение...");
    }

    private static void PrintNode(TaskNode node, int indent)
    {
        string indentStr = new string(' ', indent * 3);
        string statusIcon = GetStatusIcon(node.Status);
        string depthInfo = $"[{node.Depth}]";

        Console.WriteLine($"{indentStr}{statusIcon} {depthInfo} {node.Description}");

        foreach (var child in node.Children)
        {
            PrintNode(child, indent + 1);
        }
    }

    private static string GetStatusIcon(TaskStatus status)
    {
        return status switch
        {
            TaskStatus.New => "🆕",
            TaskStatus.Evaluating => "🧠",
            TaskStatus.Decomposing => "🔨",
            TaskStatus.Completed => "✅",
            TaskStatus.MaxDepthExceeded => "⛔",
            TaskStatus.Failed => "❌",
            TaskStatus.WaitingForChildren => "⏳",
            _ => "�"
        };
    }

    private static void SaveTreeToFile()
    {
        lock (_fileLock)
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            };

            string json = JsonSerializer.Serialize(_rootTask, options);
            File.WriteAllText(_saveFilePath, json, Encoding.UTF8);
        }
    }
}