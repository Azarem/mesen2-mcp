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
