using Mesen.Config;
using Mesen.Interop;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Mesen.Mcp
{
#pragma warning disable IL2026
#pragma warning disable IL3050
#pragma warning disable IL2070
#pragma warning disable IL2067
#pragma warning disable IL2090

	/// <summary>Direct native calls that need larger buffers than the managed wrappers use.</summary>
	internal static class McpNative
	{
		[DllImport(EmuApi.DllName, EntryPoint = "GetScriptLog")]
		private static extern unsafe void GetScriptLogRaw(int scriptId, byte* outScriptLog, uint maxLength);

		[ThreadStatic] private static byte[]? _logBuffer;
		[ThreadStatic] private static int _logBufferUsed;

		public static unsafe string GetScriptLog(int scriptId, int bufferSize)
		{
			byte[]? buffer = _logBuffer;
			if(buffer == null || buffer.Length != bufferSize) {
				buffer = _logBuffer = new byte[bufferSize];
				_logBufferUsed = 0;
			}
			//The native copy does not write a terminator, so wipe what the previous call left behind.
			Array.Clear(buffer, 0, Math.Min(buffer.Length, _logBufferUsed + 1));
			fixed(byte* ptr = buffer) {
				GetScriptLogRaw(scriptId, ptr, (uint)bufferSize);
			}
			int length = Array.IndexOf<byte>(buffer, 0);
			if(length < 0) {
				length = buffer.Length;
			}
			_logBufferUsed = length;
			return Encoding.UTF8.GetString(buffer, 0, length);
		}
	}

	/// <summary>Minimal PNG writer (RGB, 8 bit).</summary>
	internal static class McpPng
	{
		private static readonly uint[] CrcTable = BuildTable();

		private static uint[] BuildTable()
		{
			uint[] table = new uint[256];
			for(uint n = 0; n < 256; n++) {
				uint c = n;
				for(int k = 0; k < 8; k++) {
					c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
				}
				table[n] = c;
			}
			return table;
		}

		private static uint Crc(byte[] type, byte[] data)
		{
			uint c = 0xFFFFFFFFu;
			foreach(byte b in type) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
			foreach(byte b in data) c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
			return c ^ 0xFFFFFFFFu;
		}

		private static void WriteChunk(Stream stream, string type, byte[] data)
		{
			byte[] typeBytes = Encoding.ASCII.GetBytes(type);
			byte[] length = BitConverter.GetBytes((uint)data.Length);
			if(BitConverter.IsLittleEndian) Array.Reverse(length);
			stream.Write(length, 0, 4);
			stream.Write(typeBytes, 0, 4);
			stream.Write(data, 0, data.Length);
			byte[] crc = BitConverter.GetBytes(Crc(typeBytes, data));
			if(BitConverter.IsLittleEndian) Array.Reverse(crc);
			stream.Write(crc, 0, 4);
		}

		public static byte[] Encode(byte[] rgb, int width, int height)
		{
			using MemoryStream output = new MemoryStream();
			output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

			byte[] header = new byte[13];
			byte[] w = BitConverter.GetBytes((uint)width);
			byte[] h = BitConverter.GetBytes((uint)height);
			if(BitConverter.IsLittleEndian) { Array.Reverse(w); Array.Reverse(h); }
			Array.Copy(w, 0, header, 0, 4);
			Array.Copy(h, 0, header, 4, 4);
			header[8] = 8;  // bit depth
			header[9] = 2;  // RGB
			WriteChunk(output, "IHDR", header);

			using MemoryStream raw = new MemoryStream();
			using(ZLibStream z = new ZLibStream(raw, CompressionLevel.Optimal, true)) {
				byte[] line = new byte[1 + width * 3];
				for(int y = 0; y < height; y++) {
					line[0] = 0;
					Buffer.BlockCopy(rgb, y * width * 3, line, 1, width * 3);
					z.Write(line, 0, line.Length);
				}
			}
			WriteChunk(output, "IDAT", raw.ToArray());
			WriteChunk(output, "IEND", Array.Empty<byte>());
			return output.ToArray();
		}
	}

	/// <summary>Screenshots without a folder poll (MCP-11) and structured PPU/DMA/OAM state (MCP-12).</summary>
	internal sealed partial class McpToolService
	{
		private const string ShotPrefix = "@@SHOT@@";
		private static readonly string[] SnesLayerNames = { "bg1", "bg2", "bg3", "bg4", "obj" };
		private static readonly string[] SnesColorLayerNames = { "bg1", "bg2", "bg3", "bg4", "obj", "color" };

		private void AddVideoTools(Dictionary<string, McpToolDefinition> tools)
		{
			AddTool(tools, "take_screenshot",
				"Capture the current frame in-process (no screenshot folder involved). Returns JSON {frame, width, height, path?, sha1} and the PNG image. " +
				"crop {x,y,w,h} selects a region of the rendered frame, scale resizes (nearest neighbour), path/label write the PNG (label gives <results>/screenshots/<label>_<frame>.png). " +
				"layers (SNES: bg1 bg2 bg3 bg4 obj) shows only those layers: the server saves the state, renders the next frame with the other layers hidden, and restores the state (the image is then one frame later; renderedFrame says which).",
				ObjectSchema(
					("crop", AnyObjectSchema("Region {x, y, w, h} in rendered-frame pixels"), false),
					("scale", new JsonObject() { ["type"] = "number", ["description"] = "Scale factor 0.25-8 (default 1)" }, false),
					("path", StringSchema("Write the PNG here (relative paths go to the results folder)"), false),
					("label", StringSchema("Caller label used for a deterministic file name"), false),
					("layers", StringArraySchema("Layers to show, for example [\"obj\"]"), false),
					("returnImage", BooleanSchema("Return the image in the response (default true)"), false)
				),
				ToolTakeScreenshotInProcess);

			AddTool(tools, "get_ppu_state",
				"Structured PPU state. On SNES: every register field plus derived values (enabled layers on main/sub screen, colour math, windows, Mode 7 matrix, OAM settings). Other consoles: the raw PPU state.",
				EmptySchema(), ToolGetPpuState);

			AddTool(tools, "get_dma_state",
				"SNES DMA/HDMA channels 0-7: control flags, B-bus register, A-bus address, size or indirect address, HDMA table address, line counter and enable flags, plus hdmaEnableMask.",
				EmptySchema(), ToolGetDmaState);

			AddTool(tools, "get_oam",
				"Decoded sprite memory (SNES): 128 sprites with x, y, tile, name table, palette, priority, flips, size and on-screen flag. onlyOnScreen filters; first/count page the list.",
				ObjectSchema(
					("decoded", BooleanSchema("Return decoded sprites (default true); false returns the raw 544 bytes as hex"), false),
					("onlyOnScreen", BooleanSchema("Only sprites that overlap the screen"), false),
					("first", IntegerSchema("First sprite index"), false),
					("count", IntegerSchema("Number of sprites (default 128)"), false)
				),
				ToolGetOam);

			AddTool(tools, "get_tilemap_info",
				"Per-background tilemap info (SNES): tilemap and character base addresses, scroll, tilemap size, large tiles, plus the current BG mode.",
				ObjectSchema(("cpuType", StringSchema("Optional CPU type"), false)), ToolGetTilemapInfo);
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-11
		// ---------------------------------------------------------------------------------------------

		private sealed class ShotData
		{
			public int ScreenWidth, ScreenHeight, X, Y, Width, Height;
			public byte[] Rgb = Array.Empty<byte>();
		}

		private static string LuaShotScript(int? x, int? y, int? w, int? h)
		{
			string num(int? v) => v == null ? "nil" : v.Value.ToString(CultureInfo.InvariantCulture);
			return
				"local s = emu.getScreenSize() local W, H = s.width, s.height " +
				"local x0, y0, cw, ch = " + num(x) + " or 0, " + num(y) + " or 0, " + num(w) + " or W, " + num(h) + " or H " +
				"if x0 < 0 then x0 = 0 end if y0 < 0 then y0 = 0 end if x0 >= W then x0 = W - 1 end if y0 >= H then y0 = H - 1 end " +
				"if x0 + cw > W then cw = W - x0 end if y0 + ch > H then ch = H - y0 end " +
				"local buf = emu.getScreenBuffer() local parts, n = {}, 0 " +
				"for y = y0, y0 + ch - 1 do local base = y * W + 1 for x = x0, x0 + cw - 1 do local p = buf[base + x] n = n + 1 parts[n] = string.char((p >> 16) & 255, (p >> 8) & 255, p & 255) end end " +
				"local raw = table.concat(parts) " +
				"local chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/' local out, o = {}, 0 " +
				"for i = 1, #raw, 3 do local a, b, c = raw:byte(i, i + 2) b = b or 0 c = c or 0 local v = (a << 16) | (b << 8) | c " +
				"o = o + 1 out[o] = chars:sub((v >> 18) + 1, (v >> 18) + 1) .. chars:sub(((v >> 12) & 63) + 1, ((v >> 12) & 63) + 1) .. chars:sub(((v >> 6) & 63) + 1, ((v >> 6) & 63) + 1) .. chars:sub((v & 63) + 1, (v & 63) + 1) end " +
				"emu.log('" + ShotPrefix + "' .. W .. ',' .. H .. ',' .. x0 .. ',' .. y0 .. ',' .. cw .. ',' .. ch .. ',' .. #raw .. ',' .. table.concat(out))";
		}

		private ShotData CaptureScreen(int? x, int? y, int? w, int? h)
		{
			CpuType cpu = GetPrimaryCpuType();
			string name = "__shot";
			int id = LoadScriptInternal(name, LuaShotScript(x, y, w, h), false, null, cpu, out _);
			string log;
			try {
				log = McpNative.GetScriptLog(id, 8 * 1024 * 1024);
			} finally {
				try { DebugApi.RemoveScript(id); } catch { }
				_scriptIds.Remove(name);
				_scriptLogOffsets.Remove(id);
				_logStore.ForgetScript(id);
			}

			string? line = log.Split('\n').FirstOrDefault(l => l.StartsWith(ShotPrefix, StringComparison.Ordinal));
			if(line == null) {
				string detail = log.Length > 300 ? log.Substring(0, 300) : log;
				throw new McpToolException("screenshot_failed", "The screen buffer could not be read. Script log: " + detail);
			}

			string[] fields = line.Substring(ShotPrefix.Length).Split(new[] { ',' }, 8);
			ShotData shot = new ShotData() {
				ScreenWidth = int.Parse(fields[0], CultureInfo.InvariantCulture),
				ScreenHeight = int.Parse(fields[1], CultureInfo.InvariantCulture),
				X = int.Parse(fields[2], CultureInfo.InvariantCulture),
				Y = int.Parse(fields[3], CultureInfo.InvariantCulture),
				Width = int.Parse(fields[4], CultureInfo.InvariantCulture),
				Height = int.Parse(fields[5], CultureInfo.InvariantCulture)
			};
			int rawLength = int.Parse(fields[6], CultureInfo.InvariantCulture);
			shot.Rgb = Convert.FromBase64String(fields[7]);
			if(shot.Rgb.Length != rawLength || rawLength != shot.Width * shot.Height * 3) {
				throw new McpToolException("screenshot_failed", $"Screen data is incomplete ({shot.Rgb.Length} of {rawLength} bytes; the native log buffer may have cut it off).");
			}
			return shot;
		}

		private static byte[] ScaleNearest(byte[] rgb, int w, int h, double scale, out int newW, out int newH)
		{
			newW = Math.Max(1, (int)Math.Round(w * scale));
			newH = Math.Max(1, (int)Math.Round(h * scale));
			if(newW == w && newH == h) {
				return rgb;
			}
			byte[] result = new byte[newW * newH * 3];
			for(int y = 0; y < newH; y++) {
				int sy = Math.Min(h - 1, (int)(y / scale));
				for(int x = 0; x < newW; x++) {
					int sx = Math.Min(w - 1, (int)(x / scale));
					int s = (sy * w + sx) * 3;
					int d = (y * newW + x) * 3;
					result[d] = rgb[s]; result[d + 1] = rgb[s + 1]; result[d + 2] = rgb[s + 2];
				}
			}
			return result;
		}

		private ShotData CaptureWithLayers(List<string> layers, int? x, int? y, int? w, int? h, out long renderedFrame)
		{
			CpuType cpu = GetPrimaryCpuType();
			if(cpu != CpuType.Snes) {
				throw new InvalidOperationException("layers is supported for SNES only.");
			}
			HashSet<string> show = new HashSet<string>(layers.Select(l => l.ToLowerInvariant()));
			foreach(string layer in show) {
				if(!SnesLayerNames.Contains(layer)) {
					throw new InvalidOperationException($"Unknown layer '{layer}'. Use bg1, bg2, bg3, bg4, obj.");
				}
			}

			SnesConfig config = ConfigManager.Config.Snes;
			bool h1 = config.HideBgLayer1, h2 = config.HideBgLayer2, h3 = config.HideBgLayer3, h4 = config.HideBgLayer4, hs = config.HideSprites;
			string tempState = Path.Combine(Path.GetTempPath(), $"mcp_layers_{Guid.NewGuid():N}.mss");
			EmuApi.SaveStateFile(tempState);
			try {
				config.HideBgLayer1 = !show.Contains("bg1");
				config.HideBgLayer2 = !show.Contains("bg2");
				config.HideBgLayer3 = !show.Contains("bg3");
				config.HideBgLayer4 = !show.Contains("bg4");
				config.HideSprites = !show.Contains("obj");
				config.ApplyConfig();

				EnsurePaused(2000);
				RunFrames(cpu, 1, 10000);
				renderedFrame = GetFrameCount(cpu);
				return CaptureScreen(x, y, w, h);
			} finally {
				config.HideBgLayer1 = h1; config.HideBgLayer2 = h2; config.HideBgLayer3 = h3; config.HideBgLayer4 = h4; config.HideSprites = hs;
				config.ApplyConfig();
				try { EmuApi.LoadStateFile(tempState); } catch { }
				try { File.Delete(tempState); } catch { }
			}
		}

		private McpToolResult ToolTakeScreenshotInProcess(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			int? cx = null, cy = null, cw = null, ch = null;
			if(TryGetProperty(args, "crop", out JsonElement crop) && crop.ValueKind == JsonValueKind.Object) {
				cx = GetOptionalInt(crop, "x");
				cy = GetOptionalInt(crop, "y");
				cw = GetFirstInt(crop, "w", "width");
				ch = GetFirstInt(crop, "h", "height");
				if(cw != null && cw <= 0 || ch != null && ch <= 0) {
					throw new InvalidOperationException("crop w and h must be positive.");
				}
			}
			double scale = 1.0;
			if(TryGetProperty(args, "scale", out JsonElement scaleElement) && scaleElement.ValueKind == JsonValueKind.Number) {
				scale = Math.Clamp(scaleElement.GetDouble(), 0.25, 8.0);
			}

			EnsurePaused(2000);
			List<string>? layers = GetStringList(args, "layers");
			long renderedFrame = GetFrameCount(cpu);
			long requestedFrame = renderedFrame;
			ShotData shot;
			if(layers != null && layers.Count > 0) {
				shot = CaptureWithLayers(layers, cx, cy, cw, ch, out renderedFrame);
			} else {
				shot = CaptureScreen(cx, cy, cw, ch);
			}

			byte[] rgb = ScaleNearest(shot.Rgb, shot.Width, shot.Height, scale, out int outW, out int outH);
			byte[] png = McpPng.Encode(rgb, outW, outH);

			string? path = GetOptionalString(args, "path");
			string? label = GetOptionalString(args, "label");
			string? writtenPath = null;
			if(path != null || label != null) {
				if(path == null) {
					string safe = new string(label!.Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
					path = Path.Combine("screenshots", $"{safe}_{renderedFrame}.png");
				}
				writtenPath = ResolveOutputPath(path);
				string? folder = Path.GetDirectoryName(writtenPath);
				if(!string.IsNullOrWhiteSpace(folder)) {
					Directory.CreateDirectory(folder);
				}
				File.WriteAllBytes(writtenPath, png);
			}

			JsonObject node = new JsonObject() {
				["frame"] = Num(requestedFrame),
				["renderedFrame"] = Num(renderedFrame),
				["screenWidth"] = shot.ScreenWidth,
				["screenHeight"] = shot.ScreenHeight,
				["crop"] = new JsonObject() { ["x"] = shot.X, ["y"] = shot.Y, ["w"] = shot.Width, ["h"] = shot.Height },
				["width"] = outW,
				["height"] = outH,
				["scale"] = scale,
				["sizeBytes"] = png.Length,
				["sha1"] = McpRomIdentity.Sha1OfBytes(png),
				["path"] = writtenPath,
				["layers"] = layers != null ? new JsonArray(layers.Select(l => (JsonNode?)JsonValue.Create(l)).ToArray()) : null
			};

			McpToolResult result = Json(node);
			if(GetOptionalBool(args, "returnImage") ?? true) {
				result.Content.Add(new JsonObject() {
					["type"] = "image",
					["mimeType"] = "image/png",
					["data"] = Convert.ToBase64String(png)
				});
			}
			return result;
		}

		// ---------------------------------------------------------------------------------------------
		// MCP-12
		// ---------------------------------------------------------------------------------------------

		private static JsonArray BitNames(int mask, string[] names)
		{
			JsonArray list = new JsonArray();
			for(int i = 0; i < names.Length; i++) {
				if((mask & (1 << i)) != 0) {
					list.Add(JsonValue.Create(names[i]));
				}
			}
			return list;
		}

		private static JsonObject FixedColorNode(int raw)
		{
			return new JsonObject() {
				["raw"] = raw,
				["r"] = raw & 0x1F,
				["g"] = (raw >> 5) & 0x1F,
				["b"] = (raw >> 10) & 0x1F
			};
		}

		private McpToolResult ToolGetPpuState(JsonElement _)
		{
			CpuType cpu = GetPrimaryCpuType();
			if(cpu != CpuType.Snes) {
				return Json(new JsonObject() { ["cpuType"] = cpu.ToString(), ["ppu"] = GetPpuStateNode(cpu) });
			}

			SnesPpuState ppu = DebugApi.GetPpuState<SnesPpuState>(cpu);
			JsonObject derived = new JsonObject() {
				["bgMode"] = (int)ppu.BgMode,
				["mode1Bg3Priority"] = ppu.Mode1Bg3Priority,
				["forcedBlank"] = ppu.ForcedBlank,
				["brightness"] = (int)ppu.ScreenBrightness,
				["mainScreenLayers"] = BitNames(ppu.MainScreenLayers, SnesLayerNames),
				["subScreenLayers"] = BitNames(ppu.SubScreenLayers, SnesLayerNames),
				["hiRes"] = ppu.HiResMode,
				["interlace"] = ppu.ScreenInterlace,
				["overscan"] = ppu.OverscanMode,
				["directColor"] = ppu.DirectColorMode,
				["extBg"] = ppu.ExtBgEnabled,
				["mosaic"] = new JsonObject() { ["size"] = (int)ppu.MosaicSize, ["layers"] = BitNames(ppu.MosaicEnabled, SnesLayerNames.Take(4).ToArray()) },
				["colorMath"] = new JsonObject() {
					["layers"] = BitNames(ppu.ColorMathEnabled, SnesColorLayerNames),
					["mode"] = ppu.ColorMathSubtractMode ? "subtract" : "add",
					["halve"] = ppu.ColorMathHalveResult,
					["useSubscreen"] = ppu.ColorMathAddSubscreen,
					["clipMode"] = ppu.ColorMathClipMode.ToString(),
					["preventMode"] = ppu.ColorMathPreventMode.ToString(),
					["fixedColor"] = FixedColorNode(ppu.FixedColor)
				},
				["oam"] = new JsonObject() {
					["sizeMode"] = (int)ppu.OamMode,
					["baseAddressWords"] = (int)ppu.OamBaseAddress,
					["addressOffsetWords"] = (int)ppu.OamAddressOffset,
					["priorityRotation"] = ppu.EnableOamPriority,
					["sizes"] = OamSizeText(ppu.OamMode)
				}
			};

			JsonArray windows = new JsonArray();
			for(int i = 0; i < 2; i++) {
				WindowConfig w = ppu.Window[i];
				JsonObject perLayer = new JsonObject();
				for(int l = 0; l < 6; l++) {
					perLayer[SnesColorLayerNames[l]] = new JsonObject() { ["active"] = w.ActiveLayers[l] != 0, ["inverted"] = w.InvertedLayers[l] != 0 };
				}
				windows.Add((JsonNode)new JsonObject() { ["window"] = i + 1, ["left"] = (int)w.Left, ["right"] = (int)w.Right, ["layers"] = perLayer });
			}
			derived["windows"] = windows;

			JsonObject masks = new JsonObject();
			for(int l = 0; l < 6; l++) {
				masks[SnesColorLayerNames[l]] = new JsonObject() {
					["logic"] = ppu.MaskLogic[l].ToString(),
					["onMain"] = l < 5 && ppu.WindowMaskMain[l] != 0,
					["onSub"] = l < 5 && ppu.WindowMaskSub[l] != 0
				};
			}
			derived["windowMasks"] = masks;

			Mode7Config m7 = ppu.Mode7;
			derived["mode7"] = new JsonObject() {
				["matrix"] = new JsonObject() { ["a"] = (int)m7.Matrix[0], ["b"] = (int)m7.Matrix[1], ["c"] = (int)m7.Matrix[2], ["d"] = (int)m7.Matrix[3] },
				["hScroll"] = (int)m7.HScroll,
				["vScroll"] = (int)m7.VScroll,
				["centerX"] = (int)m7.CenterX,
				["centerY"] = (int)m7.CenterY,
				["largeMap"] = m7.LargeMap,
				["fillWithTile0"] = m7.FillWithTile0,
				["hMirror"] = m7.HorizontalMirroring,
				["vMirror"] = m7.VerticalMirroring
			};

			JsonArray layers = new JsonArray();
			for(int i = 0; i < 4; i++) {
				LayerConfig l = ppu.Layers[i];
				layers.Add((JsonNode)new JsonObject() {
					["layer"] = SnesLayerNames[i],
					["tilemapAddressWords"] = (int)l.TilemapAddress,
					["chrAddressWords"] = (int)l.ChrAddress,
					["hScroll"] = (int)l.HScroll,
					["vScroll"] = (int)l.VScroll,
					["doubleWidth"] = l.DoubleWidth,
					["doubleHeight"] = l.DoubleHeight,
					["largeTiles"] = l.LargeTiles
				});
			}
			derived["layers"] = layers;
			derived["cursor"] = new JsonObject() { ["frame"] = Num((long)ppu.FrameCount), ["scanline"] = (int)ppu.Scanline, ["cycle"] = (int)ppu.Cycle, ["hClock"] = (int)ppu.HClock };

			return Json(new JsonObject() { ["cpuType"] = cpu.ToString(), ["derived"] = derived, ["registers"] = StructToJsonNode(ppu) });
		}

		private static string OamSizeText(int mode)
		{
			return mode switch {
				0 => "8x8 / 16x16",
				1 => "8x8 / 32x32",
				2 => "8x8 / 64x64",
				3 => "16x16 / 32x32",
				4 => "16x16 / 64x64",
				5 => "32x32 / 64x64",
				6 => "16x32 / 32x64",
				_ => "16x32 / 32x32"
			};
		}

		private static (int small, int largeW, int largeH, int smallH) OamSizes(int mode)
		{
			// (small width, large width, large height, small height)
			return mode switch {
				0 => (8, 16, 16, 8),
				1 => (8, 32, 32, 8),
				2 => (8, 64, 64, 8),
				3 => (16, 32, 32, 16),
				4 => (16, 64, 64, 16),
				5 => (32, 64, 64, 32),
				6 => (16, 32, 64, 32),
				_ => (16, 32, 32, 32)
			};
		}

		private McpToolResult ToolGetDmaState(JsonElement _)
		{
			CpuType cpu = GetPrimaryCpuType();
			if(cpu != CpuType.Snes) {
				throw new InvalidOperationException("get_dma_state is SNES only.");
			}

			SnesState state = DebugApi.GetConsoleState<SnesState>(ConsoleType.Snes);
			JsonArray channels = new JsonArray();
			int hdmaMask = state.Dma.HdmaChannels;
			for(int i = 0; i < 8; i++) {
				DmaChannelConfig c = state.Dma.Channels[i];
				bool hdmaEnabled = (hdmaMask & (1 << i)) != 0;
				channels.Add((JsonNode)new JsonObject() {
					["channel"] = i,
					["direction"] = c.InvertDirection ? "B->A" : "A->B",
					["mode"] = (int)c.TransferMode,
					["decrement"] = c.Decrement,
					["fixed"] = c.FixedTransfer,
					["hdmaIndirect"] = c.HdmaIndirectAddressing,
					["bBusRegister"] = $"21{c.DestAddress:X2}",
					["aBus"] = $"{c.SrcBank:X2}:{c.SrcAddress:X4}",
					["sizeOrIndirect"] = (int)c.TransferSize,
					["hdmaTable"] = $"{c.HdmaBank:X2}:{c.HdmaTableAddress:X4}",
					["hdmaLineCounter"] = c.HdmaLineCounterAndRepeat & 0x7F,
					["hdmaRepeat"] = (c.HdmaLineCounterAndRepeat & 0x80) != 0,
					["dmaActive"] = c.DmaActive,
					["hdmaEnabled"] = hdmaEnabled,
					["hdmaFinished"] = c.HdmaFinished,
					["doTransfer"] = c.DoTransfer
				});
			}

			return Json(new JsonObject() {
				["hdmaEnableMask"] = hdmaMask,
				["hdmaEnableMaskHex"] = $"{hdmaMask:X2}",
				["channels"] = channels,
				["frame"] = Num(GetFrameCount(cpu))
			});
		}

		private McpToolResult ToolGetOam(JsonElement args)
		{
			CpuType cpu = GetPrimaryCpuType();
			if(cpu != CpuType.Snes) {
				throw new InvalidOperationException("get_oam is SNES only.");
			}

			byte[] oam = DebugApi.GetMemoryState(MemoryType.SnesSpriteRam);
			if(!(GetOptionalBool(args, "decoded") ?? true)) {
				return Json(new JsonObject() { ["length"] = oam.Length, ["hex"] = Hex(oam, 0, oam.Length) });
			}

			SnesPpuState ppu = DebugApi.GetPpuState<SnesPpuState>(cpu);
			var sizes = OamSizes(ppu.OamMode);
			int screenHeight = ppu.OverscanMode ? 239 : 224;
			int first = Math.Clamp(GetOptionalInt(args, "first") ?? 0, 0, 127);
			int count = Math.Clamp(GetOptionalInt(args, "count") ?? 128, 1, 128 - first);
			bool onlyOnScreen = GetOptionalBool(args, "onlyOnScreen") ?? false;

			JsonArray sprites = new JsonArray();
			int visible = 0;
			for(int i = 0; i < 128; i++) {
				int x = oam[i * 4];
				int y = oam[i * 4 + 1];
				int tile = oam[i * 4 + 2];
				int attr = oam[i * 4 + 3];
				int high = (oam[512 + (i >> 2)] >> ((i & 3) * 2)) & 0x03;
				bool large = (high & 0x02) != 0;
				if((high & 0x01) != 0) {
					x -= 256;
				}
				int width = large ? sizes.largeW : sizes.small;
				int height = large ? sizes.largeH : sizes.smallH;

				bool onX = x + width > 0 && x < 256;
				// Sprites are drawn from line y+1 and wrap at 256.
				bool onY = false;
				for(int line = 0; line < height && !onY; line++) {
					int ly = (y + 1 + line) & 0xFF;
					onY = ly < screenHeight;
				}
				bool onScreen = onX && onY;
				if(onScreen) {
					visible++;
				}
				if(i < first || i >= first + count || (onlyOnScreen && !onScreen)) {
					continue;
				}
				sprites.Add((JsonNode)new JsonObject() {
					["index"] = i,
					["x"] = x,
					["y"] = y,
					["tile"] = tile,
					["nameTable"] = attr & 0x01,
					["palette"] = (attr >> 1) & 0x07,
					["paletteIndex"] = 128 + ((attr >> 1) & 0x07) * 16,
					["priority"] = (attr >> 4) & 0x03,
					["hFlip"] = (attr & 0x40) != 0,
					["vFlip"] = (attr & 0x80) != 0,
					["large"] = large,
					["width"] = width,
					["height"] = height,
					["onScreen"] = onScreen
				});
			}

			return Json(new JsonObject() {
				["sizeMode"] = (int)ppu.OamMode,
				["sizes"] = OamSizeText(ppu.OamMode),
				["screenHeight"] = screenHeight,
				["onScreenCount"] = visible,
				["returned"] = sprites.Count,
				["sprites"] = sprites
			});
		}

		private McpToolResult ToolGetTilemapInfo(JsonElement args)
		{
			CpuType cpu = ResolveCpuType(GetOptionalString(args, "cpuType")) ?? GetPrimaryCpuType();
			if(cpu != CpuType.Snes) {
				return ToolGetTilemapSize(args);
			}

			SnesPpuState ppu = DebugApi.GetPpuState<SnesPpuState>(cpu);
			JsonArray layers = new JsonArray();
			for(int i = 0; i < 4; i++) {
				LayerConfig l = ppu.Layers[i];
				JsonObject row = new JsonObject() {
					["layer"] = i,
					["name"] = SnesLayerNames[i],
					["enabledMain"] = (ppu.MainScreenLayers & (1 << i)) != 0,
					["enabledSub"] = (ppu.SubScreenLayers & (1 << i)) != 0,
					["tilemapAddressWords"] = (int)l.TilemapAddress,
					["tilemapAddressBytes"] = l.TilemapAddress * 2,
					["chrAddressWords"] = (int)l.ChrAddress,
					["chrAddressBytes"] = l.ChrAddress * 2,
					["hScroll"] = (int)l.HScroll,
					["vScroll"] = (int)l.VScroll,
					["doubleWidth"] = l.DoubleWidth,
					["doubleHeight"] = l.DoubleHeight,
					["largeTiles"] = l.LargeTiles
				};
				try {
					FrameInfo size = DebugApi.GetTilemapSize(cpu, new GetTilemapOptions() { Layer = (byte)i }, ppu);
					row["widthPixels"] = (int)size.Width;
					row["heightPixels"] = (int)size.Height;
				} catch { }
				layers.Add((JsonNode)row);
			}

			return Json(new JsonObject() {
				["cpuType"] = cpu.ToString(),
				["bgMode"] = (int)ppu.BgMode,
				["layers"] = layers,
				["mode7"] = new JsonObject() { ["hScroll"] = (int)ppu.Mode7.HScroll, ["vScroll"] = (int)ppu.Mode7.VScroll, ["centerX"] = (int)ppu.Mode7.CenterX, ["centerY"] = (int)ppu.Mode7.CenterY }
			});
		}
	}
#pragma warning restore IL2090
#pragma warning restore IL2067
#pragma warning restore IL2070
#pragma warning restore IL3050
#pragma warning restore IL2026
}
