# Harmony Startup Optimizer

A standalone mod for Sea Power that caches shared Harmony type lookup results, speeding up startup work in mods that repeatedly search for the same types.

- Mod name: `Harmony Startup Optimizer`
- Folder / DLL: `HarmonyStartupOptimizer` / `HarmonyStartupOptimizer.dll`
- Internal ID / Harmony owner: `mebiustos.harmonystartupoptimizer`
- Version: `0.1.4`
- Requires: Anchor Chain
- No runtime dependency on BDCM or Startup Diagnostics.

## Scope

The optimizer targets `HarmonyLib.AccessTools.TypeByName(string)` in the shared Harmony assembly loaded by the game.
It applies to all mods that call this function, without identifying callers by mod name or targeting BDCM's internal methods.
It does not optimize custom lookups that bypass this function, field/property/method lookups,
patch installation, or game logic itself.

Successful type lookup results for identical name strings are shared within the current game process.
The first lookup runs the original search, and its result is cached only if a type is found.
Cache keys distinguish case, namespaces, and assembly specifications.
Missing results and exceptions are not cached, allowing types that become available later to be found on subsequent lookups.
When a new assembly is detected, the entire cache is cleared, and results from lookups started before that event are not stored.
The cache is also cleared when patches are added to or removed from the target function and Harmony rebuilds it.
By default, the cache remains available after startup and is discarded when the game closes. Type lookup results are not saved to disk.

The first lookup for each type still takes time. Measure the improvement with your actual set of enabled mods.
Time saved by the earlier BDCM-specific optimizer does not indicate an additional benefit from this general optimizer.

## Compatibility with other Harmony patches

The cache return path is inserted inside TypeByName's original method body.
Other mods' prefixes and postfixes on TypeByName still run normally on a cache hit.
The cache uses the argument after other prefixes have modified it and stores the original Type before other postfixes modify the result.
This prevents postfix transformations from being applied twice to a cached result.
If another prefix skips the original method, the cache lookup is skipped as well.
If another patch throws an exception, that exception is propagated, and no new result is stored from the failed call.

Cache access is synchronized across threads, with recursive call state tracked separately for each thread.
Concurrent first lookups for the same type may each run the original search.
Tests use the Mono runtime bundled with the game to verify patch installation and removal, result identity, exceptions, assembly loading, and related behavior.
API mismatches and failures to install or remove patches are logged as errors.

## Installation

Close Sea Power and copy the packaged `dist/HarmonyStartupOptimizer` folder into
the game's `Sea Power_Data/StreamingAssets/` directory.
The DLL should be located at `Sea Power_Data/StreamingAssets/HarmonyStartupOptimizer/HarmonyStartupOptimizer.dll`.

Enable Anchor Chain and Harmony Startup Optimizer in the mod manager, then restart Sea Power.
Lookups performed before activation are not affected retroactively. Enabling or disabling the mod takes effect on the next game launch.

Disable the old `BDCM Startup Optimizer` when comparing performance.
If that optimizer skips lookups first, the effect of this optimizer alone and the overall call count cannot be measured accurately.
This mod does not automatically change the old mod's settings or files.

Startup Diagnostics is optional. Enable it to compare startup, BDCM initialization, and Check timings.
With this optimizer, cache hits are still recorded as TypeByName calls by the diagnostic mod,
so its lookup count includes cache hits, unlike with the BDCM-specific optimizer. Consider elapsed time together with this optimizer's cache hit count.

## Startup status display

Displays the optimizer's status and cache hit count at the bottom center of the screen during Sea Power startup.

## Settings

### Overlay settings

To disable the overlay, manually create `BepInEx/config/mebiustos.harmonystartupoptimizer.cfg` inside the game directory with the following contents:

```ini
[Overlay]
Enabled = false
```

The overlay is enabled when the file is absent or `Enabled = true`.

### Cache lifetime

To stop caching after startup, add the following to the same settings file:

```ini
[Cache]
StopAfterStartup = true
```

This option also works with the overlay disabled. If omitted or set to `false`, caching continues until the game closes.

## Logging

Look for `Harmony Startup Optimizer` entries in `BepInEx/LogOutput.log`.
When active, the mod writes one startup line: `Active: shared TypeByName cache (Harmony ...)`.
When disabled, it writes one startup line indicating that state.
Otherwise, initialization, rollback, and shutdown failures are logged as errors.
Original exceptions from the game or other patches continue to propagate to the caller.

Version 0.1.1 removed separate JSON output, output directory creation, periodic statistics and logging,
per-type statistics, and per-lookup timing.
The cache retains only successful results, invalidation generations, and the state needed to track successful call completion.

Version 0.1.2 adds only a cache hit counter for the overlay. It does not add separate log files, periodic detailed statistics, or per-lookup timing.

Existing `HarmonyStartupOptimizer/*.summary.json` files created by version 0.1.0 are not automatically deleted.
They are no longer updated, and no new files are created. Keep them as historical measurements if needed.
If Startup Diagnostics is enabled, its own logging continues independently.

## Building and testing

Use .NET SDK 10 on Windows. No NuGet packages are required.
Set `SeaPowerDir` and `AnchorChainDir` in `Directory.Build.local.props`,
or pass `-GameDirectory` and `-AnchorChainDirectory` to `build.ps1`.

```powershell
.\build.ps1 -Test
```

In addition to standalone cache behavior tests, the test suite uses Sea Power's bundled Mono runtime and Harmony DLL to patch and exercise the shared API.
It covers other prefixes and postfixes, argument and result transformations, skipped original calls, original exceptions, missing types,
dynamic assembly loading, recursion, multiple threads, and patch removal by owner.
It also verifies that results from before a change are not reused when adding or removing another transpiler changes the lookup implementation.
If Startup Diagnostics is installed, tests use its actual DLL to check both patch installation orders.
If the diagnostic DLL is absent, only the optional compatibility tests are reported as SKIP.
The tests do not launch the game or initialize BDCM campaigns.

The distribution package is generated in `dist/HarmonyStartupOptimizer`.
To build, test, and install it locally, close the game and run:

```powershell
.\build.ps1 -Test -InstallLocal
```

## Source responsibilities

- `TypeLookupCache.cs`: Sharing successful results, invalidation, and concurrency control, without dependencies on the game or Harmony.
- `LookupHooks.cs`: Connecting to the shared type lookup API while preserving execution order, arguments, results, and exceptions with other patches.
- `Plugin.cs`: Anchor Chain integration, activation checks, minimal logging, and lifecycle management.
- `StartupOverlay.cs`: Startup status display, resolution scaling, and cleanup of the display component.
- `StartupMonitor.cs` / `StartupLifetime.cs`: Shared startup completion monitoring, display cleanup, and optional cache shutdown.
- `StartupSettings.cs`: Reading optional overlay and cache lifetime settings.
- `StartupScreen.cs`: Reading the game's menu and loading screen state.
- `ModActivation.cs`: Reading Sea Power's mod activation state.
- `PatchRegistry.cs`: Installing, rolling back, and removing only this mod's Harmony patches.

Support for lookups other than types would use separate caches and hooks, each with its own keys and invalidation rules.
