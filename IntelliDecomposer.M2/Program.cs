using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static string _saveFilePath;
    private static readonly object _fileLock = new object();
    private static int _requestCounter = 0;
    private static readonly Encoding _utf8 = Encoding.UTF8;
    private static bool _isNewSession = true;

    static async Task Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("🌲 Система декомпозиции задач с использованием LLM\n");

        // Проверяем наличие сохранённых сессий
        var savedSessions = Directory.GetFiles(Directory.GetCurrentDirectory(), "task_tree_*.json")
                                     .OrderByDescending(f => new FileInfo(f).LastWriteTime)
                                     .ToList();

        if (savedSessions.Count > 0)
        {
            Console.WriteLine("Обнаружены сохранённые сессии:");
            for (int i = 0; i < savedSessions.Count; i++)
            {
                var sessionName = Path.GetFileName(savedSessions[i]);
                Console.WriteLine($"{i + 1}. {sessionName}");
            }

            Console.WriteLine("\n0. Начать новую сессию");
            Console.Write("\nВыберите действие: ");

            if (int.TryParse(Console.ReadLine(), out int choice))
            {
                if (choice > 0 && choice <= savedSessions.Count)
                {
                    _saveFilePath = savedSessions[choice - 1];
                    _isNewSession = false;
                    await LoadAndContinueSession();
                }
            }
        }

        await StartNewSessionAsync();
    }

    private static async Task StartNewSessionAsync()
    {
        Console.WriteLine("\nВведите основную задачу:");
        string rootDescription = Console.ReadLine();

        // Генерируем уникальное имя файла
        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        _saveFilePath = $"task_tree_{timestamp}.json";

        _rootTask = new TaskNode
        {
            Description = rootDescription,
            Status = TaskStatus.New,
            Depth = 0
        };

        SaveTreeToFile();
        Console.WriteLine($"\n🚀 Новая сессия создана: {Path.GetFileName(_saveFilePath)}");

        //_ = ProcessTask(_rootTask); // Запускаем обработку без ожидания
        await ProcessTask(_rootTask);
        SaveTreeToFile();
        Console.WriteLine("\nДекомпозиция завершена! Результаты сохранены в " + _saveFilePath);
    }

    private static async Task LoadAndContinueSession()
    {
        try
        {
            Console.WriteLine($"\n⏳ Загружаем сессию: {Path.GetFileName(_saveFilePath)}");
            string json = File.ReadAllText(_saveFilePath, _utf8);
            _rootTask = JsonSerializer.Deserialize<TaskNode>(json);

            // Восстанавливаем связи после загрузки
            RestoreParentLinks(_rootTask, null);

            Console.WriteLine($"✅ Сессия загружена. Всего задач: {CountNodes(_rootTask)}");
            PrintTree();

            // Находим и обрабатываем незавершённые задачи
            await ProcessUnfinishedTasks();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"❌ Ошибка загрузки: {ex.Message}");
            return;
        }
    }

    private static void RestoreParentLinks(TaskNode node, TaskNode parent)
    {
        node.Parent = parent;
        foreach (var child in node.Children)
        {
            RestoreParentLinks(child, node);
        }
    }

    private static int CountNodes(TaskNode node)
    {
        if (node == null) return 0;
        int count = 1;
        foreach (var child in node.Children)
        {
            count += CountNodes(child);
        }
        return count;
    }

    private static async Task ProcessUnfinishedTasks()
    {
        var unfinishedTasks = FindUnfinishedTasks(_rootTask);
        Console.WriteLine($"\n🔎 Найдено незавершённых задач: {unfinishedTasks.Count}");

        foreach (var task in unfinishedTasks)
        {
            await ProcessTask(task);
        }

        Console.WriteLine("\n✅ Все задачи обработаны!");
    }

    private static List<TaskNode> FindUnfinishedTasks(TaskNode node)
    {
        var result = new List<TaskNode>();

        // Добавляем текущую задачу, если она не завершена
        if (node.Status != TaskStatus.Completed &&
            node.Status != TaskStatus.MaxDepthExceeded &&
            node.Status != TaskStatus.Failed)
        {
            result.Add(node);
        }

        // Рекурсивно добавляем незавершённые подзадачи
        foreach (var child in node.Children)
        {
            result.AddRange(FindUnfinishedTasks(child));
        }

        return result;
    }

    private static async Task ProcessTask(TaskNode task)
    {
        if (task.Status == TaskStatus.Completed ||
            task.Status == TaskStatus.MaxDepthExceeded ||
            task.Status == TaskStatus.Failed)
        {
            return;
        }

        task.Status = TaskStatus.Evaluating;
        PrintTree();
        SaveTreeToFile();

        if (task.Depth >= _maxDepth)
        {
            task.Status = TaskStatus.MaxDepthExceeded;
            PrintTree();
            SaveTreeToFile();
            return;
        }

        bool shouldDecompose = await CheckDecompositionNeedAsync(task);

        if (!shouldDecompose)
        {
            task.Status = TaskStatus.Completed;
            PrintTree();
            SaveTreeToFile();
            return;
        }

        task.Status = TaskStatus.Decomposing;
        PrintTree();
        SaveTreeToFile();

        List<string> subtasks = await DecomposeTaskAsync(task);

        if (subtasks == null || subtasks.Count == 0)
        {
            task.Status = TaskStatus.Completed;
            PrintTree();
            SaveTreeToFile();
            return;
        }

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

        PrintTree();
        SaveTreeToFile();

        var processingTasks = new List<Task>();
        foreach (var child in task.Children)
        {
            processingTasks.Add(ProcessTask(child));
        }
        await Task.WhenAll(processingTasks);

        task.Status = TaskStatus.Completed;
        PrintTree();
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
4. Избегай избыточной детализации, но не в ущерб смыслу
5. Если родительскую задача содержит перечисление, то необходимо декомпозировать

Ответь только в формате JSON: {{
  ""subtasks"": [""задача1"", ""задача2"", ...],
  ""rationale"": ""логика декомпозиции""
}}";

        try
        {
            string response = await GetLlmResponseAsync(prompt);
            var result = JsonSerializer.Deserialize<JsonElement>(response);

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
            _utf8,
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
        Console.WriteLine($"🌳 Дерево задач [Глубина: {_maxDepth} | Запросов: {_requestCounter}]");
        Console.WriteLine($"📂 Сессия: {Path.GetFileName(_saveFilePath)}\n");
        PrintNode(_rootTask, 0);
        Console.WriteLine("\n🔄 Автосохранение...");
    }

    private static void PrintNode(TaskNode node, int indent)
    {
        if (node == null) return;

        string indentStr = new string(' ', indent * 3);
        string statusIcon = GetStatusIcon(node.Status);
        string depthInfo = $"[{node.Depth}]";

        Console.WriteLine($"{indentStr}{statusIcon} {node.Description}");

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
            TaskStatus.Completed => "-",
            TaskStatus.MaxDepthExceeded => "-",
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
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };

            string json = JsonSerializer.Serialize(_rootTask, options);
            File.WriteAllText(_saveFilePath, json, _utf8);
        }
    }
}