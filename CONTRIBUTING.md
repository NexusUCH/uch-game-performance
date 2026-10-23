# Contributing

Thanks for helping! A few rules keep this mod safe to use:

- **Gameplay must feel identical.** No changes to physics timing (fixedDeltaTime, max timestep), solver iterations, gravity, layers, hitboxes or trajectories.
- **Every optimization is its own config switch** (`Feature` in `Source/Feature.cs`) and falls back to vanilla if anything unexpected happens.
- **Patches check the game code first.** Transpilers verify the original IL (`Source/Guards.cs`, `Source/GuardManifest.cs`) and bypass themselves on other game builds.
- **Never commit game files** (`Assembly-CSharp.dll`, decompiled code, other DLLs). Only our own source.

## Building

You need UCH with BepInEx 5 installed. Then in PowerShell:

```powershell
.\build.ps1                 # builds build\UCHGamePerformance.dll
.\build.ps1 -Install        # also copies it into BepInEx\plugins
```

## Pull requests

Describe what you changed, why it's faster (ideally with before/after numbers from the same map), and how you checked that gameplay looks the same.
