using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
#pragma warning disable IL2026
#pragma warning disable IL3050
	internal sealed class McpToolResult
	{
		public bool IsError { get; set; }
		public JsonArray Content { get; } = new JsonArray();

		public static McpToolResult Text(string text, bool isError = false)
		{
			McpToolResult result = new McpToolResult() { IsError = isError };
			result.Content.Add(new JsonObject() {
				["type"] = "text",
				["text"] = text
			});
			return result;
		}

		public static McpToolResult Image(string mimeType, string base64Data)
		{
			McpToolResult result = new McpToolResult();
			result.Content.Add(new JsonObject() {
				["type"] = "image",
				["mimeType"] = mimeType,
				["data"] = base64Data
			});
			return result;
		}
	}

	internal sealed class McpToolDefinition
	{
		public required string Name { get; init; }
		public required string Description { get; init; }
		public required JsonObject InputSchema { get; init; }
		public required Func<JsonElement, McpToolResult> Handler { get; init; }
	}

	internal sealed class McpStdioTransport : IDisposable
	{
		private enum MessageFraming
		{
			Unknown,
			ContentLength,
			NewlineJson
		}

		private readonly Stream _stdin = Console.OpenStandardInput();
		private readonly Stream _stdout;
		private readonly object _writeLock = new object();
		private bool _disposed;
		private MessageFraming _framing = MessageFraming.Unknown;

		public McpStdioTransport(Stream? protocolOutput = null)
		{
			_stdout = protocolOutput ?? Console.OpenStandardOutput();
		}

		public JsonDocument? ReadMessage()
		{
			while(true) {
				int? contentLength = null;
				string? firstLine = ReadHeaderLine();
				if(firstLine == null) {
					McpDebugLog.Info("stdin EOF while reading message.");
					return null;
				}

				string firstTrimmed = firstLine.Trim();
				if(firstTrimmed.Length == 0) {
					// Ignore keepalive/stray blank lines.
					continue;
				}

				// Cursor and some clients use newline-delimited JSON (no Content-Length headers).
				if(firstTrimmed.StartsWith("{")) {
					try {
						_framing = MessageFraming.NewlineJson;
						McpDebugLog.Info("Detected newline-delimited JSON framing.");
						return JsonDocument.Parse(firstTrimmed);
					} catch(Exception ex) {
						McpDebugLog.Error($"Failed to parse newline JSON message: '{firstTrimmed}'", ex);
						continue;
					}
				}

				// Header-framed (LSP style) mode.
				if(TryParseContentLength(firstLine, out int firstLength)) {
					contentLength = firstLength;
				}

				while(true) {
					string? line = ReadHeaderLine();
					if(line == null) {
						McpDebugLog.Info("stdin EOF while reading headers.");
						return null;
					}

					if(line.Length == 0) {
						if(contentLength == null) {
							// Some clients can emit non-length headers before sending full header block; keep waiting.
							continue;
						}
						break;
					}

					if(TryParseContentLength(line, out int length)) {
						contentLength = length;
					}
				}

				if(contentLength == null || contentLength <= 0) {
					McpDebugLog.Info("No valid Content-Length received; waiting for next message.");
					continue;
				}

				try {
					byte[] buffer = new byte[contentLength.Value];
					ReadExact(_stdin, buffer, 0, buffer.Length);
					_framing = MessageFraming.ContentLength;
					McpDebugLog.Info("Detected Content-Length framing.");
					JsonDocument document = JsonDocument.Parse(buffer);
					McpDebugLog.Info($"Read message payload (bytes={buffer.Length}).");
					return document;
				} catch(Exception ex) {
					McpDebugLog.Error("Failed to read/parse message payload.", ex);
					// Keep running and wait for next frame.
				}
			}
		}

		public void Write(JsonNode payload)
		{
			string json = payload.ToJsonString(new JsonSerializerOptions() {
				WriteIndented = false
			});

			byte[] data = Encoding.UTF8.GetBytes(json);
			lock(_writeLock) {
				if(_framing == MessageFraming.NewlineJson) {
					byte[] line = Encoding.UTF8.GetBytes(json + "\n");
					_stdout.Write(line, 0, line.Length);
				} else {
					byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {data.Length}\r\n\r\n");
					_stdout.Write(header, 0, header.Length);
					_stdout.Write(data, 0, data.Length);
				}
				_stdout.Flush();
			}
			McpDebugLog.Info($"Wrote message payload (bytes={data.Length}).");
		}

		private static bool TryParseContentLength(string line, out int length)
		{
			length = 0;
			int separator = line.IndexOf(':');
			if(separator <= 0) {
				return false;
			}

			string key = line.Substring(0, separator).Trim();
			string value = line.Substring(separator + 1).Trim();
			return key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out length);
		}

		private string? ReadHeaderLine()
		{
			List<byte> bytes = new List<byte>(64);
			while(true) {
				int read = _stdin.ReadByte();
				if(read < 0) {
					if(bytes.Count == 0) {
						return null;
					}
					break;
				}

				if(read == '\n') {
					break;
				}

				if(read == '\r') {
					int next = _stdin.ReadByte();
					if(next == '\n') {
						break;
					}

					if(next >= 0) {
						if(next != '\n') {
							bytes.Add((byte)read);
							bytes.Add((byte)next);
						}
						continue;
					}
					break;
				}

				bytes.Add((byte)read);
			}

			return Encoding.ASCII.GetString(bytes.ToArray());
		}

		private static void ReadExact(Stream stream, byte[] buffer, int offset, int count)
		{
			while(count > 0) {
				int read = stream.Read(buffer, offset, count);
				if(read <= 0) {
					throw new EndOfStreamException("Unexpected EOF while reading MCP payload.");
				}
				offset += read;
				count -= read;
			}
		}

		public void Dispose()
		{
			if(_disposed) {
				return;
			}
			_disposed = true;
			_stdin.Dispose();
			_stdout.Dispose();
		}
	}
#pragma warning restore IL3050
#pragma warning restore IL2026
}
