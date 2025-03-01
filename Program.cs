using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using McpSemanticKernel;
using DotNetEnv;
using System.Text.Json;

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
                var config = LoadConfiguration("mcp_servers_config.json");
                if (config == null)
                {
                    logger.LogError("Failed to load MCP server configuration");
                    return;
                }
                // Connect to multiple MCP servers from configuration
                await ConnectToServersFromConfig(mcpClient, config, logger);
                
                // Create the Semantic Kernel
                var builder = Kernel.CreateBuilder()
                    .AddOpenAIChatCompletion(
                        modelId: "gpt-4o", 
                        apiKey: Env.GetString("OPENAI_API_KEY"));

                builder.Services.AddLogging(b => b.AddConsole());

                
                var kernel = builder.Build();

                // Register all MCP tools as Semantic Kernel plugins
                foreach (var serverName in mcpClient.GetConnectedServers())
                {
                    // The plugin name in Semantic Kernel will be the same as the server name
                    logger.LogInformation("Registering {ServerName} tools as plugin", serverName);
                    await mcpClient.RegisterToolsAsPluginAsync(kernel, serverName);
                }

                // List tools for each registered plugin
                foreach (KernelPlugin plugin in kernel.Plugins)
                {
                    var serverName = plugin.Name;
                    plugin.TryGetFunction("ListTools", out var getToolsFunction);
                    if (getToolsFunction != null)
                    {
                        var toolsResult = await kernel.InvokeAsync(getToolsFunction);
                        logger.LogInformation("Tools for {ServerName}:", serverName);
                            logger.LogInformation("- {ToolName}", toolsResult);
                    }
                }

                var prompts = new [] {
                    "What is the weather in Seattle?",
                    "What is the weather in Miami?",
                    "Research modern JavaScript frameworks using Brave Search, focusing on performance metrics and community support. Then find the top 3 repositories for each framework on GitHub, analyze their documentation, stars, and recent commit activity. Save this research as a structured markdown document to my filesystem. Finally, use Google Maps to identify tech hubs where these frameworks are being actively developed, such as San Francisco, New York, and Seattle.",
                    "Check the weather forecast for Miami and San Francisco for the next 3 days. Then use Google Maps to find the distance between them and calculate a road trip route. Use Puppeteer to visit Airbnb.com and search for accommodations in the city with better weather. Take a screenshot of the results and save it to my filesystem.",
                    "What is the lat lon of 1 Microsoft Way, Redmond, WA?",
                    "Generate a summary of the last 3 commits to https://github.com/sphenry/sk-mcp-cs",
                    "Visit spotify.com and test their signup flow by filling out the registration form (but don't submit it). Take screenshots at each step.",
                    "put the weather report for New York, Tokyo and Paris in files in a weather_report directory",
                    "Go to bing.com, search for the weather in San Francisco, and save a screenshot of the forecast.",
                    "Go to weather.gov, search for the weather in San Francisco, and save a screenshot of the forecast."
                };

                var result = await kernel.InvokePromptAsync(
                prompts[0], new KernelArguments(new PromptExecutionSettings() { 
                        FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() 
                }));

                Console.WriteLine("AI Response:");
                Console.WriteLine(result);

                // Save the Bing homepage screenshot to a file
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string screenshotPath = Path.Combine(desktopPath, "bing_screenshot.png");
                string screenshotName="bing_homepage";

                await mcpClient.SaveScreenshotToFileAsync(
                    screenshotName,    // The name from your output
                    screenshotPath,     // Where to save it 
                    "puppeteer"         // Server name - replace with your actual server name
                );

                Console.WriteLine($"Screenshot saved to: {screenshotPath}");

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

        
        static async Task ExampleBasicUsage(McpClient mcpClient, ILogger logger)
        {
            // Connect to one or more MCP servers
            // Example: Connect to a weather server
            await mcpClient.ConnectToServerAsync(
                serverName: "weather", 
                command: "node", 
                arguments: new[] { "C:\\src\\sk-mcp-cs\\mcp-calculator\\weather_server.js"}
            );

            await mcpClient.ConnectToServerAsync(
                serverName: "filesystem", 
                command: "node", 
                arguments: new[] { "C:\\src\\servers\\src\\filesystem\\dist\\index.js", "C:\\src\\temp"}
            );
            
            logger.LogInformation("Connected to MCP servers: {Servers}", 
                string.Join(", ", mcpClient.GetConnectedServers()));

            // Create the Semantic Kernel
            var builder = Kernel.CreateBuilder()
                .AddOpenAIChatCompletion(
                    modelId: "gpt-4o-mini", 
                    apiKey: Env.GetString("OPENAI_API_KEY"));
            
            builder.Services.AddLogging(b => b.AddConsole());
            
            var kernel = builder.Build();

            // Register MCP tools as a Semantic Kernel plugin
            await mcpClient.RegisterToolsAsPluginAsync(kernel, "weather", "WeatherTools");
            await mcpClient.RegisterToolsAsPluginAsync(kernel, "filesystem", "FileSystemTools");

            var result = await kernel.InvokePromptAsync(
                "put the weather report for New York, Tokyo and Paris in files in a weather_report directory", new KernelArguments(new PromptExecutionSettings() { 
                        FunctionChoiceBehavior = FunctionChoiceBehavior.Auto() 
                }));
            
            Console.WriteLine("AI Response:");
            Console.WriteLine(result);
            

            // Clean up
            foreach (var server in mcpClient.GetConnectedServers())
            {
                await mcpClient.DisconnectFromServerAsync(server);
            }
        }

static async Task ConnectToServersFromConfig(
            McpClient mcpClient, 
            Dictionary<string, Dictionary<string, object>> serverConfigs, 
            ILogger logger)
        {
            foreach (var (serverName, config) in serverConfigs)
            {
                try
                {
                    string command = config["command"].ToString();
                   // if(command == "npx") command = @"C:\Program Files\nodejs\npx.cmd";
                    string[] args = JsonSerializer.Deserialize<string[]>(
                        JsonSerializer.Serialize(config["args"]));
                    
                    Dictionary<string, string> env = null;
                    if (config.TryGetValue("env", out var envObj) && envObj != null)
                    {
                        env = new Dictionary<string, string>();
                        var envDict = JsonSerializer.Deserialize<Dictionary<string, object>>(
                            JsonSerializer.Serialize(envObj));
                            
                        foreach (var (key, value) in envDict)
                        {
                            // Handle environment variable substitution
                            string strValue = value.ToString();
                            if (strValue.StartsWith("$"))
                            {
                                // Remove $ and get from environment
                                string envVarName = strValue.Substring(1);
                                strValue = Environment.GetEnvironmentVariable(envVarName) ?? "";
                            }
                            env[key] = strValue;
                        }
                    }

                    await mcpClient.ConnectToServerAsync(serverName, command, args, env);
                    logger.LogInformation("Connected to MCP server: {ServerName}", serverName);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to connect to MCP server: {ServerName}", serverName);
                }
            }
        }
        static Dictionary<string, Dictionary<string, object>> LoadConfiguration(string filePath)
        {
            try
            {
                if (!File.Exists(filePath))
                {
                    Console.WriteLine($"Configuration file not found: {filePath}");
                    return null;
                }

                var jsonString = File.ReadAllText(filePath);
                var config = JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, Dictionary<string, object>>>>(
                    jsonString);
                
                return config["mcpServers"];
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading configuration: {ex.Message}");
                return null;
            }
        }
    }
}