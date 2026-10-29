-- description: Count executions of every address in a CPU range (default the whole bank 00-3F ROM area) and report the hit list.
-- includes: mesen_kit
-- frames: 600
-- args: start (default 0x008000), stop (default 0x00FFFF), top (default 200), minHits (default 1)
local start = args.start or 0x008000
local stop = args.stop or 0x00FFFF
local top = args.top or 200
local minHits = args.minHits or 1
local hits, distinct, total = {}, 0, 0
emu.addMemoryCallback(function(address)
  total = total + 1
  local n = hits[address]
  if n == nil then distinct = distinct + 1; n = 0 end
  hits[address] = n + 1
end, emu.callbackType.exec, start, stop)

emu.addEventCallback(function()
  local list = {}
  for a, n in pairs(hits) do
    if n >= minHits then list[#list + 1] = { address = string.format("%06X", a), hits = n } end
  end
  table.sort(list, function(x, y)
    if x.hits ~= y.hits then return x.hits > y.hits end
    return x.address < y.address
  end)
  for i = #list, top + 1, -1 do list[i] = nil end
  emu.mcp.result({ distinctAddresses = distinct, totalExecutions = total, range = { start = start, stop = stop }, top = list })
end, emu.eventType.endFrame)
