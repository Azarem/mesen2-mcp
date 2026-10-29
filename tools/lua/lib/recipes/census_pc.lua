-- description: Sample the program counter at every end of frame and report the most frequent addresses.
-- includes: mesen_kit
-- frames: 600
-- args: top (default 40), sampleEvery (default 1)
local top = args.top or 40
local every = args.sampleEvery or 1
local counts, samples = {}, 0
kit.onFrame(function(frame)
  if frame % every ~= 0 then return end
  local st = emu.getState()
  local pc = st["cpu.pc"] or 0
  local bank = st["cpu.k"] or 0
  local key = string.format("%02X:%04X", bank, pc)
  counts[key] = (counts[key] or 0) + 1
  samples = samples + 1
end)
-- The result is published at the last frame the host advances; the host calls emu.mcp.result on unload via this callback.
emu.addEventCallback(function()
  emu.mcp.result({ samples = samples, top = kit.topCounts(counts, top) })
end, emu.eventType.endFrame)
