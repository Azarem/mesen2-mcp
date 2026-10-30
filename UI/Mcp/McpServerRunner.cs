using Mesen.Config;
using Mesen.Interop;
using Mesen.Utilities;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
#pragma warning disable IL2026
#pragma warning disable IL3050
	internal sealed class McpRuntimeOptions
	{
		public bool NoVideo { get; set; } = true;
		public bool NoAudio { get; set; } = true;
		public bool NoInput { get; set; } = true;
		public string? StartupRomPath { get; set; }
		public string? SaveStatePath { get; set; }
		public int? SaveStateSlot { get; set; }
		/// <summary>Sandbox folder for emu.mcp.writeFile and relative output paths (--mcp-results-dir).</summary>
		public string? ResultsDir { get; set; }
		/// <summary>Extra folders with Lua kit modules and recipes (--mcp-lib-dir, repeatable or ';'-separated). Searched after the server's own lib folder.</summary>
		public List<string> LibDirs { get; } = new List<string>();
	}

	public static class McpServerRunner
	{
		public static int Run(string[] args)
		{
			// Must run before any native code loads: native std::cout output would otherwise
			// be interleaved with JSON-RPC frames and break the MCP client connection.
			Stream protocolOut = McpStdoutGuard.ProtectProtocolStream();

			McpRuntimeOptions options = ParseOptions(args);
			McpDebugLog.Info($"MCP host starting. args='{string.Join(" ", args)}'");

			EnsureNativeResolver();
			McpDebugLog.Info("Native resolver configured.");

			try {
				// A release zip has never been run through the Mesen UI, so the native core
				// (MesenCore) is not on disk yet: unpack it from the embedded Dependencies.zip.
				DependencyHelper.ExtractNativeDependencies(ConfigManager.HomeFolder);
				McpDebugLog.Info($"Native dependencies are in '{ConfigManager.HomeFolder}'.");
			} catch(Exception ex) {
				McpDebugLog.Error("Could not extract the native dependencies.", ex);
			}

			try {
				ConfigManager.DisableSaveSettings = true;
				ConfigManager.LoadConfig();
				McpDebugLog.Info("Config loaded.");
			} catch {
				// Keep defaults when config cannot be loaded.
				McpDebugLog.Info("Config load failed; using defaults.");
			}

			EmuApi.InitDll();
			McpDebugLog.Info("EmuApi.InitDll done.");
			EmuApi.InitializeEmu(
				ConfigManager.HomeFolder,
				IntPtr.Zero,
				IntPtr.Zero,
				useSoftwareRenderer: true,
				noAudio: options.NoAudio,
				noVideo: options.NoVideo,
				noInput: options.NoInput
			);
			ApplyNativeConfig();
			ConfigApi.SetEmulationFlag(EmulationFlags.ConsoleMode, true);
			EmuApi.Pause();
			McpDebugLog.Info("Emulator initialized and paused.");

			McpToolService? service = null;
			McpStdioTransport? transport = null;
			try {
				service = new McpToolService(options);
				transport = new McpStdioTransport(protocolOut);
				McpHost host = new McpHost(transport, service);
				McpDebugLog.Info("MCP host services created.");

				service.Initialize();
				McpDebugLog.Info("MCP service initialized. Entering host loop.");
				int exitCode = host.Run();
				McpDebugLog.Info($"Host loop exited with code {exitCode}.");
				return exitCode;
			} catch(Exception ex) {
				McpDebugLog.Error("Unhandled exception in MCP host loop.", ex);
				return 1;
			} finally {
				// The tool service calls into the native core (RemoveScript/ReleaseDebugger) when it is
				// disposed, so it must be disposed BEFORE the emulator is stopped and released. Doing it
				// afterwards caused a fatal AccessViolationException on every shutdown.
				try { service?.Dispose(); } catch(Exception ex) { McpDebugLog.Error("Service dispose failed.", ex); }
				McpDebugLog.Info("Shutting down emulator.");
				try { EmuApi.Stop(); } catch { }
				try { EmuApi.Release(); } catch { }
				try { transport?.Dispose(); } catch { }
			}
		}

		/// <summary>
		/// The normal UI pushes the managed configuration into the native core at start-up
		/// (ConfigManager.Config.ApplyConfig). The MCP host skipped this, so the core ran with a
		/// zeroed configuration: no controller was attached to port 1 (input overrides and Lua
		/// emu.setInput were silently ignored), and video/emulation settings were unset.
		/// </summary>
		private static void ApplyNativeConfig()
		{
			try {
				// Must come after InitializeEmu, otherwise key bindings would be empty.
				ConfigManager.Config.InitializeDefaults();
				ConfigManager.Config.UpgradeConfig();
				ConfigManager.Config.ApplyConfig();
				McpDebugLog.Info("Native configuration applied.");
			} catch(Exception ex) {
				McpDebugLog.Error("Failed to apply native configuration.", ex);
			}
		}

		private static void EnsureNativeResolver()
		{
			try {
				NativeLibrary.SetDllImportResolver(typeof(EmuApi).Assembly, DllImportResolver);
			} catch {
				// Resolver can only be set once; ignore if already configured.
			}
		}

		private static IntPtr DllImportResolver(string libraryName, Assembly _, DllImportSearchPath? __)
		{
			if(!libraryName.Contains("Mesen") && !libraryName.Contains("SkiaSharp") && !libraryName.Contains("HarfBuzz")) {
				return IntPtr.Zero;
			}

			if(libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
				libraryName = libraryName.Substring(0, libraryName.Length - 4);
			}

			if(OperatingSystem.IsLinux() && !libraryName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)) {
				libraryName += ".so";
			} else if(OperatingSystem.IsWindows() && !libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) {
				libraryName += ".dll";
			} else if(OperatingSystem.IsMacOS() && !libraryName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)) {
				libraryName += ".dylib";
			}

			//Mesen home folder first (where the UI unpacks it), then next to the host (a release zip ships it there).
			string homePath = Path.Combine(ConfigManager.HomeFolder, libraryName);
			if(File.Exists(homePath)) {
				return NativeLibrary.Load(homePath);
			}
			string localPath = Path.Combine(AppContext.BaseDirectory, libraryName);
			if(File.Exists(localPath)) {
				return NativeLibrary.Load(localPath);
			}
			return NativeLibrary.Load(homePath);
		}

		private static McpRuntimeOptions ParseOptions(string[] args)
		{
			McpRuntimeOptions options = new McpRuntimeOptions();
			foreach(string arg in args) {
				string normalized = NormalizeArg(arg);
				if(normalized.Equals("mcp-server", StringComparison.OrdinalIgnoreCase)) {
					continue;
				}
				if(normalized.Equals("mcp-video", StringComparison.OrdinalIgnoreCase)) {
					options.NoVideo = false;
				} else if(normalized.Equals("mcp-audio", StringComparison.OrdinalIgnoreCase)) {
					options.NoAudio = false;
				} else if(normalized.Equals("mcp-input", StringComparison.OrdinalIgnoreCase)) {
					options.NoInput = false;
				} else if(normalized.StartsWith("mcp-rom=", StringComparison.OrdinalIgnoreCase)) {
					options.StartupRomPath = ResolvePath(normalized.Substring("mcp-rom=".Length));
				} else if(normalized.StartsWith("mcp-savestatepath=", StringComparison.OrdinalIgnoreCase)) {
					options.SaveStatePath = ResolvePath(normalized.Substring("mcp-savestatepath=".Length));
				} else if(normalized.StartsWith("mcp-results-dir=", StringComparison.OrdinalIgnoreCase)) {
					options.ResultsDir = ResolvePath(normalized.Substring("mcp-results-dir=".Length));
				} else if(normalized.StartsWith("mcp-lib-dir=", StringComparison.OrdinalIgnoreCase)) {
					foreach(string part in normalized.Substring("mcp-lib-dir=".Length).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) {
						options.LibDirs.Add(ResolvePath(part.Trim('"')));
					}
				} else if(normalized.StartsWith("mcp-savestateslot=", StringComparison.OrdinalIgnoreCase)) {
					if(int.TryParse(normalized.Substring("mcp-savestateslot=".Length), out int slot) && slot >= 1 && slot <= 10) {
						options.SaveStateSlot = slot;
					}
				}
			}
			return options;
		}

		private static string ResolvePath(string path)
		{
			if(Path.IsPathRooted(path)) {
				return path;
			}
			return Path.GetFullPath(path, Program.OriginalFolder);
		}

		private static string NormalizeArg(string arg)
		{
			arg = arg.Trim();
			if(arg.StartsWith("--")) {
				return arg.Substring(2);
			}
			if(arg.StartsWith("-") || arg.StartsWith("/")) {
				return arg.Substring(1);
			}
			return arg;
		}
	}

	internal sealed class McpHost
	{
		private readonly McpStdioTransport _transport;
		private readonly McpToolService _service;
		private bool _initialized;

		public McpHost(McpStdioTransport transport, McpToolService service)
		{
			_transport = transport;
			_service = service;
			_service.OnEvent += Service_OnEvent;
		}

		public int Run()
		{
			while(!_service.ShouldExit) {
				JsonDocument? message = _transport.ReadMessage();
				if(message == null) {
					McpDebugLog.Info("ReadMessage returned null; exiting host loop.");
					break;
				}

				ProcessMessage(message.RootElement);
			}

			return _service.ExitCode;
		}

		private void ProcessMessage(JsonElement msg)
		{
			if(msg.ValueKind != JsonValueKind.Object) {
				McpDebugLog.Info($"Ignoring non-object message kind: {msg.ValueKind}");
				return;
			}

			bool hasMethod = msg.TryGetProperty("method", out JsonElement methodElement) && methodElement.ValueKind == JsonValueKind.String;
			bool hasId = msg.TryGetProperty("id", out JsonElement idElement);

			if(!hasMethod) {
				McpDebugLog.Info("Ignoring message without method.");
				return;
			}

			string method = methodElement.GetString() ?? "";
			string idLog = hasId ? idElement.GetRawText() : "<notification>";
			McpDebugLog.Info($"Handling method='{method}' id={idLog}");
			JsonElement paramsElement = msg.TryGetProperty("params", out JsonElement p) ? p : default;

			if(!hasId) {
				ProcessNotification(method, paramsElement);
				return;
			}

			switch(method) {
				case "initialize":
					_initialized = true;
					SendResult(idElement, new JsonObject() {
						["protocolVersion"] = "2024-11-05",
						["capabilities"] = new JsonObject() {
							["tools"] = new JsonObject()
						},
						["serverInfo"] = new JsonObject() {
							["name"] = "mesen2-csharp-mcp",
							["version"] = McpToolService.ServerVersion
						}
					});
					break;

				case "tools/list":
					if(!EnsureInitialized(idElement)) {
						return;
					}
					SendResult(idElement, new JsonObject() { ["tools"] = _service.ListTools() });
					break;

				case "tools/call":
					if(!EnsureInitialized(idElement)) {
						return;
					}
					HandleToolCall(idElement, paramsElement);
					break;

				case "ping":
					SendResult(idElement, new JsonObject());
					break;

				default:
					SendError(idElement, -32601, $"Method not found: {method}");
					break;
			}
		}

		private void ProcessNotification(string method, JsonElement parameters)
		{
			if(method == "notifications/initialized") {
				return;
			}

			if(method == "exit") {
				_service.RequestExit(0);
			}
		}

		private bool EnsureInitialized(JsonElement id)
		{
			if(_initialized) {
				return true;
			}
			SendError(id, -32002, "Server is not initialized.");
			return false;
		}

		private void HandleToolCall(JsonElement id, JsonElement parameters)
		{
			if(parameters.ValueKind != JsonValueKind.Object) {
				McpDebugLog.Info("tools/call rejected: params is not object.");
				SendError(id, -32602, "Invalid params for tools/call.");
				return;
			}

			if(!parameters.TryGetProperty("name", out JsonElement nameElement) || nameElement.ValueKind != JsonValueKind.String) {
				McpDebugLog.Info("tools/call rejected: missing tool name.");
				SendError(id, -32602, "Missing tool name.");
				return;
			}

			string toolName = nameElement.GetString() ?? "";
			McpDebugLog.Info($"tools/call name='{toolName}'");
			JsonElement args = parameters.TryGetProperty("arguments", out JsonElement argsElement) ? argsElement : default;

			try {
				McpToolResult result = _service.CallTool(toolName, args);
				SendResult(id, new JsonObject() {
					["content"] = result.Content,
					["isError"] = result.IsError
				});
				McpDebugLog.Info($"tools/call '{toolName}' completed (isError={result.IsError}).");
			} catch(Exception ex) {
				McpDebugLog.Error($"tools/call '{toolName}' threw exception.", ex);
				SendResult(id, new JsonObject() {
					["content"] = new JsonArray() {
						new JsonObject() {
							["type"] = "text",
							["text"] = $"Error: {ex.Message}"
						}
					},
					["isError"] = true
				});
			}
		}

		private void SendResult(JsonElement id, JsonNode result)
		{
			JsonObject payload = new JsonObject() {
				["jsonrpc"] = "2.0",
				["id"] = JsonNode.Parse(id.GetRawText()),
				["result"] = result
			};
			_transport.Write(payload);
		}

		private void SendError(JsonElement id, int code, string message)
		{
			JsonObject payload = new JsonObject() {
				["jsonrpc"] = "2.0",
				["id"] = JsonNode.Parse(id.GetRawText()),
				["error"] = new JsonObject() {
					["code"] = code,
					["message"] = message
				}
			};
			_transport.Write(payload);
		}

		private void Service_OnEvent(JsonObject eventPayload)
		{
			JsonObject payload = new JsonObject() {
				["jsonrpc"] = "2.0",
				["method"] = "notifications/mesen/event",
				["params"] = eventPayload
			};
			_transport.Write(payload);
		}
	}
#pragma warning restore IL3050
#pragma warning restore IL2026
}
