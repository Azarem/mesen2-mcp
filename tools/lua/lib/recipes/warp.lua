-- description: IOG: warp to a scene (default Edward's Castle), dismiss the banner and return the player state.
-- includes: iog_kit
-- frames: 1500
-- args: scene, x, y, facing, scroll, flags[], delay, dismissBanner, quick (true = write sceneNext only, default spawn; x/y optional), unlockWait (extra frames to wait for the joypad, default 60)
iog.warp({
  scene = args.scene, x = args.x, y = args.y, facing = args.facing, scroll = args.scroll,
  flags = args.flags, delay = args.delay, dismissBanner = args.dismissBanner, quick = args.quick, unlockWait = args.unlockWait,
  onReady = function(s) kit.finish(s) end,
})
