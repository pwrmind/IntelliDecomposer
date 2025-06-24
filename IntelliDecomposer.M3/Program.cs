namespace ReqFlow.M3;

using Scriban;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

public class AgentDefinition
{
    public string Id { get; set; }
    public string Prompt { get; set; }
}

public class AgentSystemConfig
{
    public List<AgentDefinition> Agents { get; set; } = new List<AgentDefinition>();
    public List<string> Pipeline { get; set; } = new List<string>();
}

public class AgentProcessor
{
    private readonly Dictionary<string, AgentDefinition> _agents;
    private readonly List<string> _pipeline;
    private static readonly HttpClient _httpClient = new HttpClient()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    public AgentProcessor(AgentSystemConfig config)
    {
        _agents = config.Agents.ToDictionary(a => a.Id);
        _pipeline = config.Pipeline;
    }

    public async Task<string> ExecutePipelineAsync(string input)
    {
        string currentData = input;

        foreach (var agentId in _pipeline)
        {
            currentData = await ProcessAgentAsync(agentId, currentData);
        }

        return currentData;
    }

    private async Task<string> ProcessAgentAsync(string agentId, string input)
    {
        if (!_agents.TryGetValue(agentId, out var agent))
            throw new KeyNotFoundException($"Agent {agentId} not found");

        Console.WriteLine($"🦶 STEP: {agentId}");

        var template = Template.Parse(agent.Prompt);
        var renderedPrompt = template.Render(new { data = input });

        var result = await CallLLMAsync(renderedPrompt);

        // Console.WriteLine(result);
        Console.Beep();

        return result;
    }

    private async Task<string> CallLLMAsync(string prompt)
    {
        try
        {
            var requestData = new
            {
                model = "qwen3:8b",
                prompt = prompt,
                stream = false,
                options = new
                {
                    temperature = 0.3,
                }
            };

            var response = await _httpClient.PostAsJsonAsync(
                "http://localhost:11434/api/generate",
                requestData
            );

            response.EnsureSuccessStatusCode();

            var jsonResponse = await response.Content.ReadAsStringAsync();
            var responseObject = System.Text.Json.JsonSerializer.Deserialize<OllamaResponse>(
                jsonResponse,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }
            );

            return ExtractYamlFromResponse(responseObject?.Response);
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private string ExtractYamlFromResponse(string response)
    {
        const string startMarker = "```yaml";
        const string endMarker = "```";

        int startIndex = response.IndexOf(startMarker);
        if (startIndex == -1)
        {
            // Попробуем найти блок без указания языка
            startIndex = response.IndexOf("```");
            if (startIndex == -1) return response;

            startIndex += 3; // Пропускаем открывающий маркер
        }
        else
        {
            startIndex += startMarker.Length; // Пропускаем "```yaml"
        }

        // Ищем закрывающий маркер после стартовой позиции
        int endIndex = response.IndexOf(endMarker, startIndex);
        if (endIndex == -1) return response.Substring(startIndex).Trim();

        // Извлекаем содержимое между маркерами
        return response.Substring(startIndex, endIndex - startIndex).Trim();
    }

    private class OllamaResponse
    {
        public string Model { get; set; }
        public DateTime Created_At { get; set; }
        public string Response { get; set; }
        public bool Done { get; set; }
    }
}

internal class Program
{
    static async Task Main(string[] args)
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        // Выбор конфигурации
        var configFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*.pipeline.yaml");
        if (configFiles.Length == 0)
        {
            Console.WriteLine("❌ Конфигурационные файлы не найдены в текущей директории");
            return;
        }

        Console.WriteLine("📁 Доступные конфигурации:");
        for (int i = 0; i < configFiles.Length; i++)
        {
            Console.WriteLine($"  {i + 1}. {Path.GetFileName(configFiles[i])}");
        }

        Console.Write("\nВведите номер конфигурации: ");
        string? input = Console.ReadLine();

        if (!int.TryParse(input, out int selectedIndex) ||
            selectedIndex < 1 ||
            selectedIndex > configFiles.Length)
        {
            Console.WriteLine("⚠️ Неверный выбор конфигурации");
            return;
        }

        string selectedConfig = configFiles[selectedIndex - 1];
        Console.WriteLine($"\n⚙️ Загружается конфиг: {Path.GetFileName(selectedConfig)}\n");

        // Загрузка конфига
        var yaml = File.ReadAllText(selectedConfig);
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();

        var config = deserializer.Deserialize<AgentSystemConfig>(yaml);
        var processor = new AgentProcessor(config);

        // Тестовый запуск
        Console.WriteLine("Введите описание задачи:");
        var inputData = Console.ReadLine();
        var result = await processor.ExecutePipelineAsync(inputData);

        Console.Beep();
        Console.WriteLine("\n\n✅ Final result:");
        Console.WriteLine(result);
    }
}