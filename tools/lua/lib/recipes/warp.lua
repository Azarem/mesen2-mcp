-- description: IOG: warp to a scene (default Edward's Castle), dismiss the banner and return the player state.
-- includes: iog_kit
-- frames: 1500
-- args: scene, x, y, facing, scroll, flags[], delay, dismissBanner
iog.warp({
  scene = args.scene, x = args.x, y = args.y, facing = args.facing, scroll = args.scroll,
  flags = args.flags, delay = args.delay, dismissBanner = args.dismissBanner,
  onReady = function(s) kit.finish(s) end,
})
