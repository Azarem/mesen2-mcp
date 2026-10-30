-- description: Generic helpers for Mesen2 MCP scripts: memory access, per-frame state machines, input pulses, counters.
-- requires:
--
-- Usage (via run_script includes=["mesen_kit"]):
--   kit.r8(addr, memType) / kit.r16 / kit.w8 / kit.w16     (default memory type = CPU work RAM when available)
--   kit.onFrame(fn)               fn(frame) runs at every end of frame; returns a handle for kit.off
--   kit.machine{ start = "a", states = { a = function(m) ... m:go("b") end, ... } }
--   kit.pulse(buttons, framesDown, framesUp)   toggles buttons from an inputPolled callback until kit.stopPulse()
--   kit.finish(result)            emu.mcp.result(result) + emu.mcp.done() (also allowed at script load: the run ends at once)
--   kit.wramOffset(addr)          SNES: WRAM offset of the CPU address a callback received ($7E/$7F banks, $0000-$1FFF of banks $00-$3F/$80-$BF), or nil
--                                 Memory callbacks get the FULL effective address (24-bit when banked), run BEFORE a write (emu.read still returns
--                                 the old value) and see a PC that is already past the accessing instruction. Mask with & 0xFFFF or use this helper.
kit = kit or {}

local function defaultMem()
  local mt = emu.memType
  return mt.snesWorkRam or mt.workRam or mt.nesInternalRam or mt.gbWorkRam or mt.snesMemory or mt.cpu
end

function kit.r8(a, m, signed) return emu.read(a, m or defaultMem(), signed or false) end
function kit.r16(a, m, signed) return emu.read16(a, m or defaultMem(), signed or false) end
function kit.w8(a, v, m) emu.write(a, v, m or defaultMem()) end
function kit.w16(a, v, m) emu.write16(a, v, m or defaultMem()) end

function kit.setBit(addr, bit, m)
  local byteAddr = addr + (bit >> 3)
  kit.w8(byteAddr, kit.r8(byteAddr, m) | (1 << (bit & 7)), m)
end

function kit.getBit(addr, bit, m)
  return (kit.r8(addr + (bit >> 3), m) & (1 << (bit & 7))) ~= 0
end

kit.frame = kit.frame or 0
local frameHandlers = {}
local frameRef = nil

local function ensureFrameCallback()
  if frameRef then return end
  frameRef = emu.addEventCallback(function()
    kit.frame = kit.frame + 1
    for i = 1, #frameHandlers do
      local h = frameHandlers[i]
      if h and h.fn then h.fn(kit.frame) end
    end
  end, emu.eventType.endFrame)
end

-- kit.frame counts end-of-frame events from the moment the kit is loaded.
ensureFrameCallback()

function kit.onFrame(fn)
  ensureFrameCallback()
  local h = { fn = fn }
  frameHandlers[#frameHandlers + 1] = h
  return h
end

function kit.off(handle)
  if handle then handle.fn = nil end
end

-- Finite state machine; each state function is called every frame with the machine as argument.
local Machine = {}
Machine.__index = Machine

function Machine:go(state)
  self.state = state
  self.stateFrame = kit.frame
end

function Machine:elapsed() return kit.frame - self.stateFrame end

function kit.machine(def)
  local m = setmetatable({ states = def.states, state = def.start, stateFrame = kit.frame }, Machine)
  m.handle = kit.onFrame(function()
    local fn = m.states[m.state]
    if fn then fn(m) end
  end)
  function m:stop() kit.off(self.handle) end
  return m
end

-- Button pulsing: down for `down` frames, up for `up` frames, until stopPulse().
local pulseRef, pulseCount = nil, 0
function kit.pulse(buttons, down, up, port)
  down, up = down or 3, up or 3
  pulseCount = 0
  if pulseRef then emu.removeEventCallback(pulseRef, emu.eventType.inputPolled) end
  pulseRef = emu.addEventCallback(function()
    pulseCount = pulseCount + 1
    local pressed = (pulseCount % (down + up)) < down
    local input = {}
    for _, b in ipairs(buttons) do input[b] = pressed end
    emu.setInput(input, port or 0)
  end, emu.eventType.inputPolled)
end

function kit.stopPulse()
  if pulseRef then
    emu.removeEventCallback(pulseRef, emu.eventType.inputPolled)
    pulseRef = nil
  end
end

-- Sorted list of {key, count} from a counts table, highest first.
function kit.topCounts(counts, limit)
  local list = {}
  for k, v in pairs(counts) do list[#list + 1] = { key = k, count = v } end
  table.sort(list, function(a, b)
    if a.count ~= b.count then return a.count > b.count end
    return tostring(a.key) < tostring(b.key)
  end)
  if limit and #list > limit then
    for i = #list, limit + 1, -1 do list[i] = nil end
  end
  return list
end

-- Callbacks registered on snesMemory (or reached through a mirror) report the CPU address the code used:
-- 0x81065A, 0x7E065A and 0x00065A are all WRAM offset 0x065A. Returns the 17-bit WRAM offset, or nil if addr is not WRAM.
function kit.wramOffset(addr)
  local bank, lo = (addr >> 16) & 0xFF, addr & 0xFFFF
  if bank == 0x7E then return lo end
  if bank == 0x7F then return 0x10000 + lo end
  if (bank & 0x7F) <= 0x3F and lo < 0x2000 then return lo end
  return nil
end

function kit.finish(result)
  if result ~= nil then emu.mcp.result(result) end
  emu.mcp.done()
end
