using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Plugins.Core;
using System.ComponentModel;



namespace McpSemanticKernel
{
    /// <summary>
    /// MCP client that handles connections to Model Context Protocol servers
    /// and exposes tools as Semantic Kernel plugins.
    /// </summary>
    public class McpClient : IDisposable
    {
        private readonly ILogger<McpClient> _logger;
        private readonly Dictionary<string, Process> _serverProcesses = new();
        private readonly Dictionary<string, McpServerConnection> _connections = new();
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpClient"/> class.
        /// </summary>
        /// <param name="logger">Logger for the MCP client.</param>
        public McpClient(ILogger<McpClient>? logger = null)
        {
            _logger = logger ?? LoggerFactory.Create(builder => builder.AddConsole()).CreateLogger<McpClient>();
        }

        /// <summary>
        /// Connects to an MCP server using stdio transport.
        /// </summary>
        /// <param name="serverName">Unique name for this server connection.</param>
        /// <param name="command">Command to execute.</param>
        /// <param name="arguments">Command line arguments.</param>
        /// <param name="environmentVariables">Optional environment variables.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task ConnectToServerAsync(
            string serverName,
            string command,
            string[] arguments,
            Dictionary<string, string>? environmentVariables = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (_connections.ContainsKey(serverName))
            {
                throw new InvalidOperationException($"A server named '{serverName}' is already connected.");
            }

            _logger?.LogInformation("Starting MCP server '{ServerName}' with command: {Command}", serverName, command);

            var processStartInfo = new ProcessStartInfo
            {
                FileName = command,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            if (arguments != null)
            {
                foreach (var arg in arguments)
                {
                    processStartInfo.ArgumentList.Add(arg);
                }
            }

            if (environmentVariables != null)
            {
                foreach (var (key, value) in environmentVariables)
                {
                    processStartInfo.Environment[key] = value;
                }
            }

            var process = new Process
            {
                StartInfo = processStartInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start MCP server '{serverName}'.");
            }

            _serverProcesses[serverName] = process;

            var connection = new McpServerConnection(process, _logger ?? throw new ArgumentNullException(nameof(_logger)));
            _connections[serverName] = connection;

            // Start listening for messages
            _ = Task.Run(() => connection.StartListeningAsync(cancellationToken), cancellationToken);

            // Initialize the connection
            await connection.InitializeAsync(cancellationToken);

            _logger?.LogInformation("Connected to MCP server '{ServerName}'", serverName);
        }

        /// <summary>
        /// Disconnects from a specific MCP server.
        /// </summary>
        /// <param name="serverName">The name of the server to disconnect from.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task DisconnectFromServerAsync(string serverName)
        {
            ThrowIfDisposed();

            if (!_connections.TryGetValue(serverName, out var connection))
            {
                _logger?.LogWarning("Attempted to disconnect from non-existent server '{ServerName}'", serverName);
                return;
            }

            await connection.CloseAsync();
            _connections.Remove(serverName);

            if (_serverProcesses.TryGetValue(serverName, out var process))
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error killing process for server '{ServerName}'", serverName);
                }

                process.Dispose();
                _serverProcesses.Remove(serverName);
            }

            _logger?.LogInformation("Disconnected from MCP server '{ServerName}'", serverName);
        }

        /// <summary>
        /// Gets names of all connected servers.
        /// </summary>
        /// <returns>List of server names.</returns>
        public IReadOnlyList<string> GetConnectedServers()
        {
            ThrowIfDisposed();
            return _connections.Keys.ToList();
        }

        /// <summary>
        /// Registers MCP tools from a server as a Semantic Kernel plugin.
        /// </summary>
        /// <param name="kernel">The Semantic Kernel instance.</param>
        /// <param name="serverName">The name of the server to register tools from.</param>
        /// <param name="pluginName">The name to use for the plugin (defaults to serverName).</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task<object> RegisterToolsAsPluginAsync(
            Kernel kernel,
            string serverName,
            string? pluginName = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (!_connections.TryGetValue(serverName, out var connection))
            {
                throw new InvalidOperationException($"No connection to server '{serverName}' exists.");
            }

            // Use the server name as the plugin name if not specified
            pluginName ??= serverName;

            //A plugin name can contain only ASCII letters, digits, and underscores
            if (!pluginName.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                //fix it
                pluginName = new string(pluginName.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
            }

            // Get available tools from the server
            var tools = await connection.ListToolsAsync(cancellationToken);
            _logger?.LogInformation("Found {ToolCount} tools on server '{ServerName}'", tools.Count, serverName);

            // Create a dynamic plugin object
            var pluginInstance = new McpPlugin(connection, tools, _logger ?? throw new ArgumentNullException(nameof(_logger)));
            
            // Register with Semantic Kernel
            kernel.Plugins.AddFromObject(pluginInstance, pluginName);
            
            _logger?.LogInformation("Registered MCP server '{ServerName}' as plugin '{PluginName}'", serverName, pluginName);
            
            return pluginInstance;
        }

        /// <summary>
        /// Reads a resource from the MCP server.
        /// </summary>
        /// <param name="uri">The URI of the resource to read.</param>
        /// <param name="serverName">Optional server name if you have multiple connections.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation with the resource content.</returns>
        public async Task<byte[]> ReadResourceAsync(
            string uri,
            string? serverName = null,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            // If no server name specified, use the first one
            if (string.IsNullOrEmpty(serverName))
            {
                var servers = GetConnectedServers();
                if (servers.Count == 0)
                {
                    throw new InvalidOperationException("No connected MCP servers available.");
                }
                serverName = servers[0];
            }

            if (!_connections.TryGetValue(serverName, out var connection))
            {
                throw new InvalidOperationException($"No connection to server '{serverName}' exists.");
            }

            return await connection.ReadResourceAsync(uri, cancellationToken);
        }

        /// <summary>
        /// Saves a screenshot from the MCP server to a file.
        /// </summary>
        /// <param name="screenshotName">The name of the screenshot.</param>
        /// <param name="filePath">The file path where to save the screenshot.</param>
        /// <param name="serverName">Optional server name if you have multiple connections.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task SaveScreenshotToFileAsync(
            string screenshotName,
            string filePath,
            string? serverName = null,
            CancellationToken cancellationToken = default)
        {
            var screenshotUri = $"screenshot://{screenshotName}";
            var data = await ReadResourceAsync(screenshotUri, serverName, cancellationToken);
            
            // Ensure directory exists
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            
            await File.WriteAllBytesAsync(filePath, data, cancellationToken);
            _logger?.LogInformation("Screenshot saved to {FilePath}", filePath);
        }

        /// <summary>
        /// Releases all resources used by the <see cref="McpClient"/> instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="McpClient"/> and
        /// optionally releases the managed resources.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> to release both managed and unmanaged resources;
        /// <c>false</c> to release only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Dispose managed state (managed objects)
                foreach (var serverName in _connections.Keys.ToArray())
                {
                    try
                    {
                        DisconnectFromServerAsync(serverName).GetAwaiter().GetResult();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error disconnecting from server '{ServerName}' during disposal", serverName);
                    }
                }

                _connections.Clear();
                _serverProcesses.Clear();
            }

            _disposed = true;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(McpClient));
            }
        }
    }

    /// <summary>
    /// Represents a connection to an MCP server, handling communication and message processing.
    /// </summary>
    public class McpServerConnection : IDisposable
    {
        private readonly Process _process;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeSemaphore = new(1, 1);
        private readonly Dictionary<string, TaskCompletionSource<JsonElement>> _pendingRequests = new();
        private readonly List<McpTool> _cachedTools = new();
        private bool _initialized;
        private bool _disposed;
        private int _idCounter;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpServerConnection"/> class.
        /// </summary>
        /// <param name="process">The process for the MCP server.</param>
        /// <param name="logger">Logger instance.</param>
        public McpServerConnection(Process process, ILogger logger)
        {
            _process = process ?? throw new ArgumentNullException(nameof(process));
            _logger = logger;
        }

        /// <summary>
        /// Initializes the MCP connection.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task InitializeAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            
            // Check if the process is still running
            if (_process.HasExited)
            {
                int exitCode = _process.ExitCode;
                string stderr = _process.StandardError.ReadToEnd();
                throw new InvalidOperationException(
                    $"MCP server process exited before initialization with code {exitCode}. Error output: {stderr}");
            }

            _logger?.LogInformation("Starting MCP initialization");
            
            try
            {
                // var request = new
                // {
                //     jsonrpc = "2.0",
                //     id = GetNextId(),
                //     method = "initialize",
                //     @params = new
                //     {
                //         protocolVersion = "0.1.0",
                //         name = "semantic-kernel-client",
                //         version = "1.0.0",
                //         capabilities = new
                //         {
                //             tools = new { }
                //         }
                //     }
                // };

                var request = new
                {
                    jsonrpc = "2.0",
                    id = GetNextId(),
                    method = "initialize",
                    @params = new
                    {
                        protocolVersion = "0.1.0",
                        clientInfo = new  // Added this object
                        {
                            name = "semantic-kernel-client",
                            version = "1.0.0"
                        },
                        capabilities = new
                        {
                            tools = new { }
                        }
                    }
                };

                // Use a shorter timeout for just the initialization
                using var initCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                initCts.CancelAfter(TimeSpan.FromSeconds(10)); // 10 second timeout for init
                
                _logger?.LogInformation("Sending initialize request to MCP server");
                try
                {
                    var response = await SendRequestAsync(request, initCts.Token);
                    _logger?.LogInformation("Received initialize response from MCP server");
                    
                    // Send initialized notification
                    var notification = new
                    {
                        jsonrpc = "2.0",
                        method = "initialized"
                    };

                    await SendNotificationAsync(notification, cancellationToken);
                    _initialized = true;
                    _logger?.LogInformation("MCP server successfully initialized");
                }
                catch (OperationCanceledException) when (initCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    // This was our timeout, not the user's cancellation
                    throw new TimeoutException("MCP server initialization timed out after 10 seconds. Check if the server is running and responding.");
                }
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                // If this is not a normal cancellation, add diagnostic info
                string processState = _process.HasExited 
                    ? $"Process exited with code {_process.ExitCode}" 
                    : "Process is still running";
                
                string errorOutput = "";
                try
                {
                    // Try to get any error output
                    errorOutput = _process.StandardError.ReadToEnd();
                }
                catch
                {
                    errorOutput = "Could not read error output";
                }
                
                _logger?.LogError("MCP initialization failed: {Error}\nProcess state: {State}\nError output: {Output}", 
                    ex.Message, processState, errorOutput);
                
                throw new InvalidOperationException($"Failed to initialize MCP connection: {ex.Message}", ex);
            }
        }
        /// <summary>
        /// Reads a resource from the MCP server.
        /// </summary>
        /// <param name="uri">The URI of the resource to read.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The resource content as a byte array.</returns>
        public async Task<byte[]> ReadResourceAsync(
            string uri,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            var request = new
            {
                jsonrpc = "2.0",
                id = GetNextId(),
                method = "resources/read",
                @params = new
                {
                    uri = uri
                }
            };

            var response = await SendRequestAsync(request, cancellationToken);
            
            if (response.TryGetProperty("contents", out var contentsArray) && contentsArray.GetArrayLength() > 0)
            {
                var content = contentsArray[0];
                
                // Check if there's text content
                if (content.TryGetProperty("text", out var textElement))
                {
                    string text = textElement.GetString() ?? string.Empty;
                    if (text != null)
                    {
                        return Encoding.UTF8.GetBytes(text);
                    }
                    throw new InvalidOperationException($"Resource '{uri}' has no readable text content");
                }
                
                // Check if there's blob content (base64-encoded binary data)
                if (content.TryGetProperty("blob", out var blobElement))
                {
                    string? base64Data = blobElement.GetString();
                    if (!string.IsNullOrEmpty(base64Data))
                    {
                        return Convert.FromBase64String(base64Data);
                    }
                    throw new InvalidOperationException($"Resource '{uri}' has no readable blob content");
                }
            }
            
            throw new InvalidOperationException($"Resource '{uri}' has no readable content");
        }
        

        /// <summary>
        /// Lists the tools available on the connected MCP server.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A list of MCP tools.</returns>
        public async Task<IReadOnlyList<McpTool>> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            if (_cachedTools.Count > 0)
            {
                return _cachedTools.AsReadOnly();
            }

            var request = new
            {
                jsonrpc = "2.0",
                id = GetNextId(),
                method = "tools/list"
            };

            var response = await SendRequestAsync(request, cancellationToken);
            var tools = response.GetProperty("tools").EnumerateArray();

            foreach (var tool in tools)
            {
                var name = tool.GetProperty("name").GetString();
                var description = tool.TryGetProperty("description", out var descProp) 
                    ? descProp.GetString() 
                    : string.Empty;
                var inputSchema = tool.GetProperty("inputSchema");

                var mcpTool = new McpTool
                {
                    Name = name ?? throw new InvalidOperationException("Tool name cannot be null"),
                    Description = description ?? string.Empty,
                    InputSchema = inputSchema
                };

                _cachedTools.Add(mcpTool);
            }

            return _cachedTools.AsReadOnly();
        }

        /// <summary>
        /// Calls a tool on the MCP server.
        /// </summary>
        /// <param name="toolName">Name of the tool to call.</param>
        /// <param name="arguments">Arguments for the tool.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the tool call.</returns>
        public async Task<string> CallToolAsync(
            string toolName,
            Dictionary<string, object> arguments,
            CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();
            EnsureInitialized();

            var request = new
            {
                jsonrpc = "2.0",
                id = GetNextId(),
                method = "tools/call",
                @params = new
                {
                    name = toolName,
                    arguments
                }
            };

            var response = await SendRequestAsync(request, cancellationToken);
            
            // Extract the text content from the response
            if (response.TryGetProperty("content", out var contentArray))
            {
                var stringBuilder = new StringBuilder();
                
                foreach (var content in contentArray.EnumerateArray())
                {
                    if (content.TryGetProperty("type", out var typeElement) && 
                        typeElement.GetString() == "text" &&
                        content.TryGetProperty("text", out var textElement))
                    {
                        stringBuilder.AppendLine(textElement.GetString());
                    }
                }
                
                return stringBuilder.ToString().Trim();
            }
            
            // Return the whole response if we couldn't find the text content
            return response.ToString();
        }

        /// <summary>
        /// Closes the connection to the MCP server.
        /// </summary>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task CloseAsync()
        {
            if (_disposed)
            {
                return;
            }

            if (_initialized)
            {
                try
                {
                    var request = new
                    {
                        jsonrpc = "2.0",
                        id = GetNextId(),
                        method = "shutdown"
                    };

                    await SendRequestAsync(request, CancellationToken.None);

                    var notification = new
                    {
                        jsonrpc = "2.0",
                        method = "exit"
                    };

                    await SendNotificationAsync(notification, CancellationToken.None);
                }
                catch (InvalidOperationException ex ) when (ex.Message.Contains("Method not found"))
                {
                    // Ignore this error - the server doesn't support the shutdown method
                    _logger?.LogWarning("Server doesn't support shutdown method, continuing with exit");
                    try
                    {
                        var notification = new
                        {
                            jsonrpc = "2.0",
                            method = "exit"
                        };

                        await SendNotificationAsync(notification, CancellationToken.None);
                    }
                    catch (Exception exitEx)
                    {
                        _logger?.LogWarning("Error sending exit notification: {Error}", exitEx.Message);
                    }
                    
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error during MCP connection shutdown");
                }
            }

            // Cancel all pending requests
            foreach (var (id, tcs) in _pendingRequests)
            {
                tcs.TrySetCanceled();
            }

            _pendingRequests.Clear();
        }

        /// <summary>
        /// Starts listening for messages from the MCP server.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A task representing the asynchronous operation.</returns>
        public async Task StartListeningAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            try
            {
                _logger?.LogInformation("Starting MCP message listener");
                
                // Setup stderr handling first to capture any early errors
                var errorBuilder = new StringBuilder();
                _process.ErrorDataReceived += (sender, args) => 
                {
                    if (!string.IsNullOrEmpty(args.Data))
                    {
                        errorBuilder.AppendLine(args.Data);
                        _logger?.LogWarning("MCP Server Error: {ErrorMessage}", args.Data);
                    }
                };
                
                _process.BeginErrorReadLine();
                
                // Setup stdout handling
                using var streamReader = new StreamReader(_process.StandardOutput.BaseStream, Encoding.UTF8);
                
                _logger?.LogInformation("MCP listener started - waiting for messages from server");

                string? line;
                while ((line = await streamReader.ReadLineAsync(cancellationToken)) != null)
                {
                    if (string.IsNullOrWhiteSpace(line))
                    {
                        continue;
                    }

                    _logger?.LogTrace("Received: {Message}", line);

                    try
                    {
                        var message = JsonDocument.Parse(line);
                        var root = message.RootElement;

                        // Handle responses to requests
                        if (root.TryGetProperty("id", out var idElement))
                        {
                            var id = idElement.GetString();
                            if (!string.IsNullOrEmpty(id) && _pendingRequests.TryGetValue(id, out var tcs))
                            {
                                _logger?.LogDebug("Processing response for request ID: {Id}", id);
                                
                                if (root.TryGetProperty("result", out var resultElement))
                                {
                                    _logger?.LogDebug("Request {Id} completed successfully", id);
                                    tcs.SetResult(resultElement);
                                }
                                else if (root.TryGetProperty("error", out var errorElement))
                                {
                                    var errorCode = errorElement.TryGetProperty("code", out var codeElement)
                                        ? codeElement.GetInt32()
                                        : -1;
                                        
                                    var errorMessage = errorElement.TryGetProperty("message", out var messageElement)
                                        ? messageElement.GetString()
                                        : "Unknown error";
                                    
                                    _logger?.LogError("Request {Id} failed with error code {Code}: {Message}", 
                                        id, errorCode, errorMessage);
                                    
                                    tcs.SetException(new InvalidOperationException($"MCP error {errorCode}: {errorMessage}"));
                                }
                                else
                                {
                                    _logger?.LogError("Request {Id} received malformed response (missing result and error)", id);
                                    tcs.SetException(new InvalidOperationException("Malformed MCP response: neither result nor error present"));
                                }

                                _pendingRequests.Remove(id);
                            }
                            else if (!string.IsNullOrEmpty(id))
                            {
                                _logger?.LogWarning("Received response for unknown request ID: {Id}", id);
                            }
                        }
                        // Handle notifications (no id)
                        else if (root.TryGetProperty("method", out var methodElement))
                        {
                            var method = methodElement.GetString();
                            _logger?.LogDebug("Received notification: {Method}", method);
                            
                            // Handle different notification types if needed
                            // Currently we don't do anything specific with notifications
                        }
                        else
                        {
                            _logger?.LogWarning("Received message with neither id nor method: {Message}", line);
                        }
                    }
                    catch (JsonException ex)
                    {
                        _logger?.LogError(ex, "Error parsing JSON from MCP server: {Message}", line);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error processing message from MCP server: {Message}", line);
                    }
                }
                
                _logger?.LogInformation("MCP server stdout stream ended");
                
                // If we've reached here, the process output has ended
                // Check if there were any errors
                if (errorBuilder.Length > 0)
                {
                    _logger?.LogError("MCP server had error output: {Errors}", errorBuilder.ToString());
                }
                
                // Check if the process is still running
                if (!_process.HasExited)
                {
                    _logger?.LogWarning("MCP server stdout ended but process is still running");
                }
                else
                {
                    _logger?.LogInformation("MCP server process has exited with code: {ExitCode}", 
                        _process.ExitCode);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _logger?.LogInformation("MCP message listening canceled");
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in MCP message listening loop");
                
                // Cancel any pending requests
                foreach (var (id, tcs) in _pendingRequests)
                {
                    tcs.TrySetException(new InvalidOperationException("MCP listener encountered an error", ex));
                }
                _pendingRequests.Clear();
            }
        }

        /// <summary>
        /// Releases resources used by the <see cref="McpServerConnection"/> instance.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Releases the unmanaged resources used by the <see cref="McpServerConnection"/> instance.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> to release both managed and unmanaged resources;
        /// <c>false</c> to release only unmanaged resources.
        /// </param>
        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
            {
                return;
            }

            if (disposing)
            {
                // Dispose managed state (managed objects)
                _writeSemaphore.Dispose();
            }

            _disposed = true;
        }

        private async Task<JsonElement> SendRequestAsync(object request, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();

            var jsonRequest = JsonSerializer.Serialize(request);
            var id = GetIdFromRequest(jsonRequest);
            var methodName = GetMethodFromRequest(jsonRequest);

            _logger?.LogDebug("Preparing to send request: {Method} (id: {Id})", methodName, id);

            var tcs = new TaskCompletionSource<JsonElement>();
            _pendingRequests[id] = tcs;

            try
            {
                // Send the request
                await SendMessageAsync(jsonRequest, cancellationToken);
                _logger?.LogDebug("Request sent: {Method} (id: {Id})", methodName, id);
                
                // Create a timeout
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(30)); // Add timeout
                
                using var registration = cts.Token.Register(() => 
                {
                    var message = $"Request {methodName} (id: {id}) timed out after 30 seconds";
                    _logger?.LogWarning(message);
                    tcs.TrySetException(new TimeoutException(message));
                });
                
                _logger?.LogDebug("Waiting for response to: {Method} (id: {Id})", methodName, id);
                var result = await tcs.Task;
                _logger?.LogDebug("Received response for: {Method} (id: {Id})", methodName, id);
                return result;
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "Error in request {Method} (id: {Id}): {Message}", methodName, id, ex.Message);
                _pendingRequests.Remove(id);
                throw;
            }
        }
        
        private string GetMethodFromRequest(string jsonRequest)
        {
            try
            {
                var document = JsonDocument.Parse(jsonRequest);
                return document.RootElement.GetProperty("method").GetString() ?? "unknown";
            }
            catch
            {
                return "unknown";
            }
        }

        private async Task SendNotificationAsync(object notification, CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            var jsonNotification = JsonSerializer.Serialize(notification);
            await SendMessageAsync(jsonNotification, cancellationToken);
        }

        private async Task SendMessageAsync(string message, CancellationToken cancellationToken)
        {
            await _writeSemaphore.WaitAsync(cancellationToken);

            try
            {
                _logger?.LogTrace("Sending: {Message}", message);
                
                await _process.StandardInput.WriteLineAsync(message.AsMemory(), cancellationToken);
                await _process.StandardInput.FlushAsync();
            }
            finally
            {
                _writeSemaphore.Release();
            }
        }

        private string GetIdFromRequest(string jsonRequest)
        {
            try
            {
                var document = JsonDocument.Parse(jsonRequest);
                return document.RootElement.GetProperty("id").GetString() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetNextId()
        {
            return (++_idCounter).ToString();
        }

        private void EnsureInitialized()
        {
            if (!_initialized)
            {
                throw new InvalidOperationException("MCP connection not initialized. Call InitializeAsync first.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(McpServerConnection));
            }
        }
    }

    /// <summary>
    /// Represents an MCP tool definition.
    /// </summary>
    public class McpTool
    {
        /// <summary>
        /// Gets or sets the name of the tool.
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Gets or sets the description of the tool.
        /// </summary>
        public required string Description { get; set; }

        /// <summary>
        /// Gets or sets the input schema for the tool.
        /// </summary>
        public JsonElement InputSchema { get; set; }

        /// <summary>
        /// Gets a list of parameters from the input schema.
        /// </summary>
        /// <returns>A list of parameter definitions.</returns>
        public IReadOnlyList<ParameterDefinition> GetParameters()
        {
            var parameters = new List<ParameterDefinition>();

            if (InputSchema.TryGetProperty("properties", out var properties))
            {
                var requiredParams = InputSchema.TryGetProperty("required", out var required)
                    ? required.EnumerateArray().Select(r => r.GetString()).Where(s => s != null).ToHashSet()!
                    : new HashSet<string>();

                foreach (var property in properties.EnumerateObject())
                {
                    var paramName = property.Name;
                    var paramSchema = property.Value;
                    var description = paramSchema.TryGetProperty("description", out var desc)
                        ? desc.GetString()
                        : "";
                    var type = paramSchema.TryGetProperty("type", out var typeElement)
                        ? typeElement.GetString()
                        : "string";
                    var isRequired = requiredParams.Contains(paramName);

                    parameters.Add(new ParameterDefinition
                    {
                        Name = paramName,
                        Type = type ?? "string",
                        Description = description ?? string.Empty,
                        IsRequired = isRequired
                    });
                }
            }

            return parameters;
        }
    }

    /// <summary>
    /// Represents a parameter definition for an MCP tool.
    /// </summary>
    public class ParameterDefinition
    {
        /// <summary>
        /// Gets or sets the name of the parameter.
        /// </summary>
        public required string Name { get; set; }

        /// <summary>
        /// Gets or sets the type of the parameter.
        /// </summary>
        public required string Type { get; set; }

        /// <summary>
        /// Gets or sets the description of the parameter.
        /// </summary>
        public required string Description { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the parameter is required.
        /// </summary>
        public bool IsRequired { get; set; }
    }

    /// <summary>
    /// Dynamic plugin class that exposes MCP tools as kernel functions.
    /// </summary>
    public class McpPlugin
    {
        private readonly McpServerConnection _connection;
        private readonly IReadOnlyList<McpTool> _tools;
        private readonly ILogger _logger;

        /// <summary>
        /// Initializes a new instance of the <see cref="McpPlugin"/> class.
        /// </summary>
        /// <param name="connection">The MCP server connection.</param>
        /// <param name="tools">List of available tools on the server.</param>
        /// <param name="logger">Logger instance.</param>
        public McpPlugin(McpServerConnection connection, IReadOnlyList<McpTool> tools, ILogger logger)
        {
            _connection = connection;
            _tools = tools;
            _logger = logger;
        }

        /// <summary>
        /// Gets the tools available in this plugin.
        /// </summary>
        public IReadOnlyList<McpTool> Tools => _tools;

        // Each tool is exposed as a method that can be called through Semantic Kernel
        // These methods are dynamically generated by the KernelPluginFactory using reflection

        // Tools are dynamically registered using the following method naming pattern:
        // public async Task<string> ToolNameAsync([optionally typed arguments])
        
        // For example, for a tool named "calculate_sum":
        // public async Task<string> CalculateSumAsync(double a, double b, CancellationToken cancellationToken = default)
        // {
        //     var arguments = new Dictionary<string, object>
        //     {
        //         ["a"] = a,
        //         ["b"] = b
        //     };
        //     return await _connection.CallToolAsync("calculate_sum", arguments, cancellationToken);
        // }

        /// <summary>
        /// Dispatches a call to any MCP tool.
        /// </summary>
        /// <param name="toolName">Name of the tool to call.</param>
        /// <param name="arguments">Dictionary of arguments to pass to the tool.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>The result of the tool execution.</returns>
        [KernelFunction]
        public async Task<string> InvokeToolAsync(
            [Description("Name of the MCP tool to invoke")] string toolName,
            [Description("JSON string containing tool arguments")] string arguments,
            CancellationToken cancellationToken = default)
        {
            if(toolName == "filesystem")//TODO: HACK for filesystem demo
                arguments = arguments.Replace('\\', '/');
            var toolArgs = JsonSerializer.Deserialize<Dictionary<string, object>>(arguments);
            if (toolArgs == null)
            {
                throw new ArgumentNullException(nameof(arguments), "Tool arguments cannot be null");
            }
            return await _connection.CallToolAsync(toolName, toolArgs, cancellationToken);
        }

        /// <summary>
        /// Lists available tools from the MCP server.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A string representation of the available tools.</returns>
        [KernelFunction]
        public Task<string> ListToolsAsync(CancellationToken cancellationToken = default)
        {
            var toolsInfo = new StringBuilder();
            toolsInfo.AppendLine("Available MCP Tools:");
            
            foreach (var tool in _tools)
            {
                toolsInfo.AppendLine($"- {tool.Name}: {tool.Description}");
                
                var parameters = tool.GetParameters();
                if (parameters.Count > 0)
                {
                    toolsInfo.AppendLine("  Parameters:");
                    foreach (var param in parameters)
                    {
                        toolsInfo.AppendLine($"    {param.Name} ({param.Type}){(param.IsRequired ? " [Required]" : "")}: {param.Description}");
                    }
                }
                
                toolsInfo.AppendLine();
            }
            
            return Task.FromResult(toolsInfo.ToString());
        }
    }
}