namespace IntelliDecomposer.M2;

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

public enum TaskStatus
{
    New,                 // Новая задача
    Evaluating,           // Оценка необходимости декомпозиции
    Decomposing,          // В процессе декомпозиции
    Completed,            // Декомпозиция не требуется/завершена
    MaxDepthExceeded,     // Превышена максимальная глубина
    Failed,               // Ошибка при обработке
    WaitingForChildren    // Ожидает завершения подзадач
}

public class TaskNode
{
    public string Description { get; set; }
    public TaskStatus Status { get; set; }
    public int Depth { get; set; }
    public List<TaskNode> Children { get; } = new List<TaskNode>();
}

class Program
{
    private static readonly HttpClient _httpClient = new HttpClient();
    private static TaskNode _rootTask;
    private static int _maxDepth = 4;
    private static string _saveFilePath = "task_tree.json";
    private static readonly object _fileLock = new object();

    static async Task Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        Console.WriteLine("Введите основную задачу:");
        string rootDescription = Console.ReadLine();

        if(string.IsNullOrEmpty(rootDescription))
            rootDescription = "Cоздание экспертной системы по предсказанию погоды по наблюдениям за природными явлениями, " +
                "растениями и животными, получаемыми от человека";
        
        _rootTask = new TaskNode
        {
            Description = rootDescription,
            Status = TaskStatus.New,
            Depth = 0
        };

        await ProcessTask(_rootTask);
        SaveTreeToFile();
        Console.WriteLine("\nДекомпозиция завершена!");
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

        // Запрос к LLM о необходимости декомпозиции
        bool shouldDecompose = await CheckDecompositionNeedAsync(task.Description);

        if (!shouldDecompose)
        {
            task.Status = TaskStatus.Completed;
            SaveTreeToFile();
            return;
        }

        // Декомпозиция задачи
        task.Status = TaskStatus.Decomposing;
        PrintTree();

        List<string> subtasks = await DecomposeTaskAsync(task.Description);

        if (subtasks == null || subtasks.Count == 0)
        {
            task.Status = TaskStatus.Completed;
            SaveTreeToFile();
            return;
        }

        // Создание подзадач
        task.Status = TaskStatus.WaitingForChildren;
        foreach (var subtask in subtasks)
        {
            task.Children.Add(new TaskNode
            {
                Description = subtask,
                Depth = task.Depth + 1,
                Status = TaskStatus.New
            });
        }

        SaveTreeToFile();
        PrintTree();

        // Рекурсивная обработка подзадач
        foreach (var child in task.Children)
        {
            await ProcessTask(child);
        }

        // Обновление статуса после завершения детей
        task.Status = TaskStatus.Completed;
        SaveTreeToFile();
    }

    private static async Task<bool> CheckDecompositionNeedAsync(string taskDescription)
    {
        string prompt = $@"[Задача]
{taskDescription}

[Вопрос]
Требуется ли декомпозиция этой задачи на подзадачи? Ответь только JSON: {{""decompose"": true/false}}";

        try
        {
            string response = await GetLlmResponseAsync(prompt);
            var result = JsonSerializer.Deserialize<JsonElement>(response);
            return result.GetProperty("decompose").GetBoolean();
        }
        catch
        {
            return false;
        }
    }

    private static async Task<List<string>> DecomposeTaskAsync(string taskDescription)
    {
        string prompt = $@"[Задача]
{taskDescription}

[Инструкция]
Декомпозируй задачу на подзадачи. Ответь только в формате JSON: {{""subtasks"": [""задача1"", ""задача2"", ...]}}";

        try
        {
            string response = await GetLlmResponseAsync(prompt);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

            var subtasks = new List<string>();
            foreach (var task in result.GetProperty("subtasks").EnumerateArray())
            {
                subtasks.Add(task.GetString());
            }
            return subtasks;
        }
        catch
        {
            return new List<string>();
        }
    }

    private static async Task<string> GetLlmResponseAsync(string prompt)
    {
        var request = new
        {
            model = "qwen3:8b", // Используемая модель
            prompt = prompt,
            format = "json",
            stream = false
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
        return JsonSerializer.Deserialize<JsonElement>(responseContent)
            .GetProperty("response")
            .GetString();
    }

    private static void PrintTree()
    {
        Console.Clear();
        PrintNode(_rootTask, 0);
    }

    private static void PrintNode(TaskNode node, int indent)
    {
        string indentStr = new string(' ', indent * 2);
        string statusIcon = GetStatusIcon(node.Status);

        Console.WriteLine($"{indentStr}{statusIcon} [{node.Depth}] {node.Description}");

        foreach (var child in node.Children)
        {
            PrintNode(child, indent + 1);
        }
    }

    private static string GetStatusIcon(TaskStatus status)
    {
        return status switch
        {
            TaskStatus.New => "🔵",
            TaskStatus.Evaluating => "⏳",
            TaskStatus.Decomposing => "🔄",
            TaskStatus.Completed => "✅",
            TaskStatus.MaxDepthExceeded => "⛔",
            TaskStatus.Failed => "❌",
            TaskStatus.WaitingForChildren => "⌛",
            _ => "�"
        };
    }

    private static void SaveTreeToFile()
    {
        lock (_fileLock)
        {
            var options = new JsonSerializerOptions { WriteIndented = true,  };
            string json = JsonSerializer.Serialize(_rootTask, options, );
            File.WriteAllText(_saveFilePath, json);
        }
    }
}