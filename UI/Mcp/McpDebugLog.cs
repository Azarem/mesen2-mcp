using System;

namespace Mesen.Mcp
{
	internal static class McpDebugLog
	{
		private static readonly bool _enabled =
			!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("MESEN_MCP_DEBUG")) &&
			Environment.GetEnvironmentVariable("MESEN_MCP_DEBUG") != "0";

		public static void Info(string message)
		{
			if(!_enabled) {
				return;
			}
			Console.Error.WriteLine($"[{DateTime.UtcNow:O}] [mcp] {message}");
		}

		/// <summary>Always written to stderr (never stdout), regardless of MESEN_MCP_DEBUG. For watchdog and fatal diagnostics.</summary>
		public static void Always(string message)
		{
			try {
				Console.Error.WriteLine($"[{DateTime.UtcNow:O}] [mcp][watchdog] {message}");
			} catch {
			}
		}

		public static void Error(string message, Exception? ex = null)
		{
			if(!_enabled) {
				return;
			}
			if(ex != null) {
				Console.Error.WriteLine($"[{DateTime.UtcNow:O}] [mcp][error] {message} :: {ex}");
			} else {
				Console.Error.WriteLine($"[{DateTime.UtcNow:O}] [mcp][error] {message}");
			}
		}
	}
}
