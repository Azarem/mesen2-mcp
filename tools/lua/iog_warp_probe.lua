-- Illusion of Gaia: probe scene warp by writing sceneNext + spawn params.
local WRAM = emu.memType.snesWorkRam
local function rw(a) return emu.read16(a, WRAM) end
local frame = 0
local staged = false
local TARGET = { scene = 0x0A, x = 0x0060, y = 0x0370, facing = 0x03, scroll = 0x4430 }

emu.addEventCallback(function()
  frame = frame + 1
  if frame == 120 and not staged then
    emu.write16(0x064C, TARGET.x, WRAM)
    emu.write16(0x064E, TARGET.y, WRAM)
    emu.write16(0x0650, TARGET.facing, WRAM)
    emu.write16(0x0652, TARGET.scroll, WRAM)
    emu.write16(0x0642, TARGET.scene, WRAM)
    staged = true
    emu.log("staged warp at frame " .. frame)
  end
  if frame % 30 == 0 then
    emu.log(string.format("f=%d next=%04X cur=%04X px=%04X py=%04X pActor=%04X",
      frame, rw(0x0642), rw(0x0644), rw(0x09A2), rw(0x09A4), rw(0x09AA)))
  end
end, emu.eventType.endFrame)
emu.log("probe loaded")
