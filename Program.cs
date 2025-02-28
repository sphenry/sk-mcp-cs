using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using McpSemanticKernel;
using DotNetEnv;

namespace McpSemanticKernelExample
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Env.Load();
            // Configure services with logging
            var services = new ServiceCollection();
            services.AddLogging(builder =>
            {
                builder.SetMinimumLevel(LogLevel.Information);
                builder.AddConsole();
            });

            var serviceProvider = services.BuildServiceProvider();
            var loggerFactory = serviceProvider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<Program>();

            // Initialize the MCP client
            using var mcpClient = new McpClient(loggerFactory.CreateLogger<McpClient>());
            
            try
            {
                // Connect to one or more MCP servers
                // Example: Connect to a weather server
                await mcpClient.ConnectToServerAsync(
                    serverName: "calculator", 
                    command: "python", 
                    //arguments: new[] { "-y", "@modelcontextprotocol/server-weather" },
                    arguments: new[] {"C:\\src\\sk-mcp-cs\\calculator_server.py"},
                    environmentVariables: new Dictionary<string, string>
                    {
                        ["WEATHER_API_KEY"] = Environment.GetEnvironmentVariable("WEATHER_API_KEY")
                    });
                
                logger.LogInformation("Connected to MCP servers: {Servers}", 
                    string.Join(", ", mcpClient.GetConnectedServers()));

                // Create the Semantic Kernel
                var builder = Kernel.CreateBuilder()
                    .AddOpenAIChatCompletion(
                        modelId: "gpt-4o", 
                        apiKey: Env.GetString("OPENAI_API_KEY"));
                
                builder.Services.AddLogging(b => b.AddConsole());
                
                var kernel = builder.Build();

                // Register MCP tools as a Semantic Kernel plugin
                await mcpClient.RegisterToolsAsPluginAsync(kernel, "calculator", "CalculatorTools");

                // Use the tools with Semantic Kernel's AI
                var result = await kernel.InvokePromptAsync(
                    "What is 2 + 2?");
                
                Console.WriteLine("AI Response:");
                Console.WriteLine(result);

                // You can also use the tools directly
                await ExampleDirectToolUse(kernel);
                
                // Or create a function that combines multiple tools
                // await ExampleCreatePlanWithMcpTools(kernel);
                
                // Clean up
                foreach (var server in mcpClient.GetConnectedServers())
                {
                    await mcpClient.DisconnectFromServerAsync(server);
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during MCP server communication");
            }
        }

        static async Task ExampleDirectToolUse(Kernel kernel)
        {
            Console.WriteLine("\nDirect Tool Use Example:");
            
            // Get the weather function from the registered plugin
            var getCurrentWeather = kernel.Plugins["WeatherTools"]["GetCurrentWeather"];
            
            // Call the function directly
            var weatherResult = await kernel.InvokeAsync(getCurrentWeather, 
                new KernelArguments { ["location"] = "New York" });
            
            Console.WriteLine($"Current weather in New York: {weatherResult}");
        }
        
        // static async Task ExampleCreatePlanWithMcpTools(Kernel kernel)
        // {
        //     Console.WriteLine("\nPlanning Example with MCP Tools:");
            
        //     // Create a plan using Semantic Kernel's planner
        //     var planner = new Microsoft.SemanticKernel.Planning.Handlebars.HandlebarsPlanner();
            
        //     var planResult = await planner.CreatePlanAsync(kernel, 
        //         "Get the weather forecast for the next 3 days in Seattle and San Francisco. Compare them and recommend the better city to visit.");
            
        //     Console.WriteLine("Generated Plan:");
        //     Console.WriteLine(planResult.Plan?.ToString());
            
        //     // Execute the plan
        //     Console.WriteLine("\nExecuting Plan...");
        //     var planResult2 = await planResult.InvokeAsync(kernel);
            
        //     Console.WriteLine("Plan Result:");
        //     Console.WriteLine(planResult2);
        // }
    }
}