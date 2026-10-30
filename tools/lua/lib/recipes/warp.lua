-- description: IOG: warp to a scene (default Edward's Castle), dismiss the banner and return the player state.
-- includes: iog_kit
-- frames: 1500
-- args: scene, x, y, facing, scroll, flags[], delay, dismissBanner, quick (true = write sceneNext only, default spawn; x/y optional)
iog.warp({
  scene = args.scene, x = args.x, y = args.y, facing = args.facing, scroll = args.scroll,
  flags = args.flags, delay = args.delay, dismissBanner = args.dismissBanner, quick = args.quick,
  onReady = function(s) kit.finish(s) end,
})
