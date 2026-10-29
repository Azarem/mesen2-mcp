-- Illusion of Gaia (US) scene bootstrap for Mesen2 MCP.
--
-- Skips the title/intro by staging a scene warp through the same variables the game's own
-- ExecuteWarp uses, then dismisses the scene-title banner so the player is controllable.
--
-- Variables (WRAM, see gaia-iog-baserom/docs/wram-memory-map.md):
--   $0642 sceneNext   $0644 sceneCurrent
--   $064C warpDestX   $064E warpDestY   $0650 warpFacing   $0652 warpScrollParams
--   $00B4 scene-title wait flag (nonzero => ExecuteSceneTransition waits for any button)
--   $0A00 eventFlags bitfield (byte = id >> 3, mask = 1 << (id & 7))
--   $065A joypadMaskStd (non-zero => scripted input lock, e.g. guard/cutscene actors)
--   $09A2/$09A4 playerX/playerY   $0ACA/$0ACE maxHp/hp
--
-- Override TARGET / START_DELAY_FRAMES / DISMISS_BANNER as globals before this script runs
-- to bootstrap into another scene. Scene IDs: db-us/groups.json (edward_castle = 10 / $0A).

TARGET = TARGET or {
  scene  = 0x0A,    -- Edward's Castle
  x      = 0x0060,  -- spawn pixel X
  y      = 0x0370,  -- spawn pixel Y
  facing = 0x03,    -- spawn facing / flags (bit 7 = save warp)
  scroll = 0x4430,  -- packed camera window for InitCameraBounds (0 = normal bounds)
  -- Event flags to pre-set. $3F = "Will showed Lance's letter to the castle guard": without it
  -- the guard actor (ec0A_left_guard) locks the joypad and plays its cutscene on arrival.
  flags  = { 0x3F },
}
START_DELAY_FRAMES = START_DELAY_FRAMES or 120   -- let the boot/title code settle
if DISMISS_BANNER == nil then DISMISS_BANNER = true end

local WRAM = emu.memType.snesWorkRam
local function r8(a) return emu.read(a, WRAM) end
local function w8(a, v) emu.write(a, v, WRAM) end
local function r16(a) return emu.read16(a, WRAM) end
local function w16(a, v) emu.write16(a, v, WRAM) end

local function set_event_flag(id)
  local addr = 0x0A00 + (id >> 3)
  w8(addr, r8(addr) | (1 << (id & 7)))
end

local frame, state, stateFrame, pulse = 0, "waiting", 0, 0
local function goto_state(s) state = s; stateFrame = frame end

emu.addEventCallback(function()
  frame = frame + 1

  if state == "waiting" then
    if frame >= START_DELAY_FRAMES and r16(0x0642) == 0 then
      for _, id in ipairs(TARGET.flags or {}) do set_event_flag(id) end
      w16(0x064C, TARGET.x)
      w16(0x064E, TARGET.y)
      w16(0x0650, TARGET.facing)
      w16(0x0652, TARGET.scroll)
      w16(0x0642, TARGET.scene)
      goto_state("warping")
      emu.log(string.format("[bootstrap] staged warp -> scene %02X (%04X,%04X) at frame %d",
        TARGET.scene, TARGET.x, TARGET.y, frame))
    end

  elseif state == "warping" then
    -- sceneCurrent flips before ClearSceneState runs; the banner flag ($00B4) is set later.
    if r16(0x0644) == TARGET.scene and r16(0x0642) == 0 then
      emu.log(string.format("[bootstrap] scene %02X committed at frame %d", TARGET.scene, frame))
      goto_state(DISMISS_BANNER and "banner" or "ready")
    elseif frame - stateFrame > 900 then
      emu.log("[bootstrap] ERROR: scene transition did not complete")
      goto_state("done")
    end

  elseif state == "banner" then
    -- Wait for the scene-title banner to appear (up to 5s), then pulse A until it is gone.
    if r8(0x00B4) ~= 0 then
      goto_state("dismiss")
    elseif frame - stateFrame > 300 then
      goto_state("ready")
    end

  elseif state == "dismiss" then
    if r8(0x00B4) == 0 then
      goto_state("settle")
    elseif frame - stateFrame > 600 then
      emu.log("[bootstrap] WARNING: scene-title banner not dismissed after 600 frames")
      goto_state("ready")
    end

  elseif state == "settle" then
    if frame - stateFrame >= 20 then goto_state("ready") end   -- let actors run a few frames

  elseif state == "ready" then
    local locked = r16(0x065A) ~= 0
    emu.log(string.format("[bootstrap] READY scene=%04X pos=(%04X,%04X) hp=%d/%d inputMask=%04X%s frame=%d",
      r16(0x0644), r16(0x09A2), r16(0x09A4), r16(0x0ACE), r16(0x0ACA), r16(0x065A),
      locked and " (input locked by scene script)" or "", frame))
    goto_state("done")
  end
end, emu.eventType.endFrame)

-- Pulse A (3 frames down, 3 up) while the banner is up so the game sees a fresh press.
emu.addEventCallback(function()
  if state == "dismiss" then
    pulse = pulse + 1
    emu.setInput({ a = (pulse % 6) < 3 }, 0)
  end
end, emu.eventType.inputPolled)

emu.log("[bootstrap] loaded; waiting " .. START_DELAY_FRAMES .. " frames before warp")
