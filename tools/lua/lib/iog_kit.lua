-- description: Illusion of Gaia (US) helpers: scene warp state machine, event flags, player snapshot.
-- requires: mesen_kit
--
-- WRAM map (gaia-iog-baserom/docs/wram-memory-map.md):
--   $0642 sceneNext  $0644 sceneCurrent  $064C warpDestX  $064E warpDestY  $0650 warpFacing
--   $0652 warpScrollParams  $00B4 scene-title wait flag  $0A00 eventFlags  $065A joypadMaskStd
--   $09A2/$09A4 playerX/Y  $0ACA/$0ACE maxHp/hp
--
-- iog.warp{ scene=0x0A, x=0x60, y=0x370, facing=3, scroll=0x4430, flags={0x3F}, delay=120,
--           dismissBanner=true, quick=false, onReady=function(info) end }
--   quick=true  writes only sceneNext (the scene's default spawn is used; warpDest* stay untouched). The event flags
--               in `flags` are set first, then the scene and the banner are awaited. x, y, facing, scroll are ignored.
--               It also works from the title screen (scene $FC) once the title is polling sceneNext (delay >= ~60 frames).
--   The result also has inputLocked ($065A != 0) and bannerShown; when the joypad is still locked after the settle wait
--   (unlockWait extra frames, default 60) ok is false and reason/error is "input_locked".
--   If a scene other than the default (Edward's Castle) is given without x and y, quick is used automatically.
-- iog.state() -> table with scene/player/hp/input mask.
iog = iog or {}

iog.addr = {
  sceneNext = 0x0642, sceneCurrent = 0x0644,
  warpX = 0x064C, warpY = 0x064E, warpFacing = 0x0650, warpScroll = 0x0652,
  bannerFlag = 0x00B4, eventFlags = 0x0A00, inputMask = 0x065A,
  playerX = 0x09A2, playerY = 0x09A4, maxHp = 0x0ACA, hp = 0x0ACE,
}

iog.EDWARDS_CASTLE = { scene = 0x0A, x = 0x0060, y = 0x0370, facing = 0x03, scroll = 0x4430, flags = { 0x3F } }

function iog.setEventFlag(id) kit.setBit(iog.addr.eventFlags, id) end
function iog.getEventFlag(id) return kit.getBit(iog.addr.eventFlags, id) end

function iog.state()
  local a = iog.addr
  return {
    scene = kit.r16(a.sceneCurrent), sceneNext = kit.r16(a.sceneNext),
    x = kit.r16(a.playerX), y = kit.r16(a.playerY),
    hp = kit.r16(a.hp), maxHp = kit.r16(a.maxHp),
    inputMask = kit.r16(a.inputMask), banner = kit.r8(a.bannerFlag),
  }
end

function iog.warp(opts)
  opts = opts or {}
  local t = {}
  for k, v in pairs(opts.target or iog.EDWARDS_CASTLE) do t[k] = v end
  for _, k in ipairs({ "scene", "x", "y", "facing", "scroll", "flags" }) do
    if opts[k] ~= nil then t[k] = opts[k] end
  end
  local quick = opts.quick == true
  if not quick and opts.target == nil and opts.scene ~= nil and opts.scene ~= iog.EDWARDS_CASTLE.scene
     and (opts.x == nil or opts.y == nil) then
    quick = true
    emu.log("[iog.warp] no spawn coordinates for scene " .. string.format("%02X", opts.scene) .. ": using quick warp (default spawn)")
  end
  local delay = opts.delay or 120
  local unlockWait = opts.unlockWait or 60
  local dismiss = opts.dismissBanner
  if dismiss == nil then dismiss = true end
  local a = iog.addr
  local startFrame = kit.frame

  local machine
  machine = kit.machine({
    start = "waiting",
    states = {
      waiting = function(m)
        if kit.frame - startFrame >= delay and kit.r16(a.sceneNext) == 0 then
          for _, id in ipairs(t.flags or {}) do iog.setEventFlag(id) end
          if quick then
            kit.w16(a.sceneNext, t.scene)
            emu.log(string.format("[iog.warp] quick warp: staged scene %02X at frame %d", t.scene, kit.frame))
          else
            kit.w16(a.warpX, t.x or 0); kit.w16(a.warpY, t.y or 0)
            kit.w16(a.warpFacing, t.facing or 0); kit.w16(a.warpScroll, t.scroll or 0)
            kit.w16(a.sceneNext, t.scene)
            emu.log(string.format("[iog.warp] staged scene %02X (%04X,%04X) at frame %d", t.scene, t.x or 0, t.y or 0, kit.frame))
          end
          m:go("warping")
        end
      end,
      warping = function(m)
        if kit.r16(a.sceneCurrent) == t.scene and kit.r16(a.sceneNext) == 0 then
          m:go(dismiss and "banner" or "ready")
        elseif m:elapsed() > 900 then
          emu.log("[iog.warp] ERROR: scene transition did not complete")
          m.failed = "warp_timeout"
          m:go("ready")
        end
      end,
      banner = function(m)
        if kit.r8(a.bannerFlag) ~= 0 then
          m.bannerShown = true
          kit.pulse({ "a" }, 3, 3)
          m:go("dismiss")
        elseif m:elapsed() > 300 then
          m:go("ready")
        end
      end,
      dismiss = function(m)
        if kit.r8(a.bannerFlag) == 0 then
          kit.stopPulse()
          m:go("settle")
        elseif m:elapsed() > 600 then
          kit.stopPulse()
          emu.log("[iog.warp] WARNING: banner not dismissed after 600 frames")
          m:go("ready")
        end
      end,
      settle = function(m)
        if m:elapsed() >= 20 and (kit.r16(a.inputMask) == 0 or m:elapsed() >= 20 + unlockWait) then m:go("ready") end
      end,
      ready = function(m)
        local s = iog.state()
        s.frame = kit.frame
        s.bannerShown = m.bannerShown == true
        s.inputLocked = s.inputMask ~= 0
        s.ok = m.failed == nil
        s.error = m.failed
        if s.ok and s.inputLocked then
          s.ok = false
          s.reason = "input_locked"
          s.error = "input_locked"
          emu.log(string.format("[iog.warp] WARNING: joypad still locked (inputMask=%04X) after the wait", s.inputMask))
        end
        s.quick = quick
        emu.log(string.format("[iog.warp] READY scene=%04X pos=(%04X,%04X) hp=%d/%d inputMask=%04X frame=%d",
          s.scene, s.x, s.y, s.hp, s.maxHp, s.inputMask, s.frame))
        m:stop()
        iog.lastWarp = s
        if opts.onReady then opts.onReady(s) end
      end,
    },
  })
  return machine
end
