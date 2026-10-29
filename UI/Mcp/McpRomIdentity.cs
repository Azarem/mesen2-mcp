using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
	/// <summary>
	/// Identity of a ROM file on disk (size, SHA-1, CRC32 and, for SNES, the internal header).
	/// Used by get_rom_info/load_rom and to tag save states with the ROM they belong to.
	/// </summary>
	internal sealed class McpRomIdentity
	{
		public string Path { get; init; } = "";
		public long FileSizeBytes { get; init; }
		public string FileSha1 { get; init; } = "";
		public string FileCrc32 { get; init; } = "";
		public bool HasHeaderBytes { get; init; }
		public JsonObject? Header { get; init; }

		private static readonly object _cacheLock = new object();
		private static string? _cacheKey;
		private static McpRomIdentity? _cacheValue;

		/// <summary>
		/// Returns the identity of a ROM file, or null when the path is not a plain file on disk
		/// (for example an entry inside an archive). The last result is cached by path, size and mtime.
		/// </summary>
		public static McpRomIdentity? TryCompute(string? romPath, bool isSnes)
		{
			if(string.IsNullOrWhiteSpace(romPath) || !File.Exists(romPath)) {
				return null;
			}

			FileInfo info = new FileInfo(romPath);
			string key = $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{isSnes}";
			lock(_cacheLock) {
				if(_cacheKey == key && _cacheValue != null) {
					return _cacheValue;
				}
			}

			byte[] data = File.ReadAllBytes(romPath);
			bool hasCopierHeader = (data.Length % 1024) == 512;

			McpRomIdentity identity = new McpRomIdentity() {
				Path = info.FullName,
				FileSizeBytes = data.Length,
				FileSha1 = Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant(),
				FileCrc32 = Crc32(data).ToString("x8"),
				HasHeaderBytes = hasCopierHeader,
				Header = isSnes ? ParseSnesHeader(data, hasCopierHeader) : null
			};

			lock(_cacheLock) {
				_cacheKey = key;
				_cacheValue = identity;
			}
			return identity;
		}

		public JsonObject ToJson()
		{
			return new JsonObject() {
				["path"] = Path,
				["fileSizeBytes"] = FileSizeBytes,
				["fileSha1"] = FileSha1,
				["fileCrc32"] = FileCrc32,
				["hasHeaderBytes"] = HasHeaderBytes,
				["header"] = Header?.DeepClone()
			};
		}

		public static string Sha1OfFile(string path)
		{
			using FileStream stream = File.OpenRead(path);
			return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
		}

		public static string Sha1OfBytes(byte[] data)
		{
			return Convert.ToHexString(SHA1.HashData(data)).ToLowerInvariant();
		}

		private static JsonObject? ParseSnesHeader(byte[] rom, bool hasCopierHeader)
		{
			int baseOffset = hasCopierHeader ? 512 : 0;
			int[] candidates = new int[] { 0x7FC0, 0xFFC0, 0x40FFC0 };

			int bestScore = -1;
			int bestOffset = -1;
			foreach(int candidate in candidates) {
				int offset = baseOffset + candidate;
				if(offset + 0x20 > rom.Length) {
					continue;
				}

				int score = 0;
				int complement = rom[offset + 0x1C] | (rom[offset + 0x1D] << 8);
				int checksum = rom[offset + 0x1E] | (rom[offset + 0x1F] << 8);
				if((complement ^ checksum) == 0xFFFF) {
					score += 4;
				}

				int mapMode = rom[offset + 0x15];
				bool hiRom = (mapMode & 0x01) != 0;
				if(candidate == 0x7FC0 && !hiRom) score += 2;
				if(candidate == 0xFFC0 && hiRom) score += 2;
				if(candidate == 0x40FFC0 && (mapMode & 0x0F) == 0x05) score += 2;
				if((mapMode & 0xE0) == 0x20) score += 1;

				int printable = 0;
				for(int i = 0; i < 21; i++) {
					byte b = rom[offset + i];
					if(b >= 0x20 && b < 0x7F) printable++;
				}
				if(printable >= 15) score += 1;

				if(score > bestScore) {
					bestScore = score;
					bestOffset = offset;
				}
			}

			if(bestOffset < 0) {
				return null;
			}

			int map = rom[bestOffset + 0x15];
			int comp = rom[bestOffset + 0x1C] | (rom[bestOffset + 0x1D] << 8);
			int sum = rom[bestOffset + 0x1E] | (rom[bestOffset + 0x1F] << 8);
			string title = Encoding.ASCII.GetString(rom, bestOffset, 21).TrimEnd('\0', ' ');
			StringBuilder cleanTitle = new StringBuilder();
			foreach(char c in title) {
				cleanTitle.Append(c >= 0x20 && c < 0x7F ? c : '?');
			}

			string layout = (map & 0x0F) switch {
				0x00 => "LoROM",
				0x01 => "HiROM",
				0x02 => "LoROM (S-DD1)",
				0x03 => "LoROM (SA-1)",
				0x05 => "ExHiROM",
				0x0A => "ExHiROM (SPC7110)",
				_ => "Unknown"
			};

			return new JsonObject() {
				["headerOffset"] = bestOffset,
				["mapMode"] = $"0x{map:X2}",
				["mapLayout"] = layout,
				["speed"] = (map & 0x10) != 0 ? "FastROM" : "SlowROM",
				["title"] = cleanTitle.ToString(),
				["checksum"] = $"0x{sum:X4}",
				["complement"] = $"0x{comp:X4}",
				["checksumComplementValid"] = (sum ^ comp) == 0xFFFF
			};
		}

		private static uint[]? _crcTable;

		private static uint Crc32(byte[] data)
		{
			if(_crcTable == null) {
				uint[] table = new uint[256];
				for(uint i = 0; i < 256; i++) {
					uint c = i;
					for(int k = 0; k < 8; k++) {
						c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
					}
					table[i] = c;
				}
				_crcTable = table;
			}

			uint crc = 0xFFFFFFFFu;
			foreach(byte b in data) {
				crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
			}
			return crc ^ 0xFFFFFFFFu;
		}
	}
}
