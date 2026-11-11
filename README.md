# Harmony Startup Optimizer

A standalone mod that helps reduce Sea Power's startup time through faster Harmony type searches, file-list processing, and encyclopedia generation.

- Mod name: `Harmony Startup Optimizer`
- Folder / DLL: `HarmonyStartupOptimizer` / `HarmonyStartupOptimizer.dll`
- Internal ID / Harmony owner: `mebiustos.harmonystartupoptimizer`
- Version: `0.2.4`
- Requires: Anchor Chain

## What to expect

The benefit depends on your mod setup. On my PC, Sea Power's measured startup time was about 104 seconds with this mod disabled and 26 seconds with it enabled.

Mods that require Anchor Chain and include a DLL may benefit from faster Harmony type searches.
Mods that add or replace game data may also benefit from faster file-list processing and encyclopedia generation, even if they do not use Harmony themselves.

The optimizations target specific lookup and loading routines, rather than every part of the game's loading process.
They remain active after startup by default and may also help when the same operations run later. They do not optimize rendering.

## Scope

### Harmony type searches

The optimizer targets `HarmonyLib.AccessTools.TypeByName(string)` and `GetTypesFromAssembly(Assembly)` in the shared Harmony assembly loaded by the game.
It applies to all mods that call these functions, without identifying callers by mod name.
It does not optimize custom type lookups that bypass these functions, field/property/method lookups,
or patch installation.

Complete assembly type lists and immutable runtime type names are also reused, reducing repeated work when searching for different type names.
Harmony's search order is preserved, and each returned type array remains independent. Incomplete enumerations are retried; dynamic assemblies and custom type-name getters keep their original behavior.

Successful type lookup results for identical name strings are shared within the current game process.
The first lookup runs the original search, and its result is cached only if a type is found.
Cache keys distinguish case, namespaces, and assembly specifications.
Missing results and exceptions are not cached, allowing types that become available later to be found on subsequent lookups.
When a new assembly is detected, the entire cache is cleared, and results from lookups started before that event are not stored.
The cache is also cleared when patches are added to or removed from the target function and Harmony rebuilds it.
Type lookup results are kept in memory and discarded when caching stops or the game closes.

The relevant caches are cleared when Harmony rebuilds the patched enumeration or comparison methods, and all are released when caching stops.
The first lookup still requires work to populate the caches.

### Game data processing

- **FileListing** uses a set for duplicate checks in the pattern-based `FileManager.GetFiles` overload, including `LayerKind` on Sea Power 0.9.0, preserving file order, case sensitivity and mod priority. Each call still reads the current directories; file lists are not cached between calls.
- **EncyclopediaLookup** indexes repeated reference searches while `LoadUnitsFromMetadata` builds the encyclopedia, preserving the first matching entry. Indices follow collection changes and are released when that operation finishes. This module supports the game assembly shipped with Sea Power `0.9.0-261111`. For an unverified assembly, it logs a warning and stays inactive while the other modules remain available.

All caches are kept in memory. No disk cache is created, and game files and other mods' files are not modified.

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

## Startup status display

Shows the mod version and status at the bottom center during loading, followed by startup time and the Harmony cache hit count at the main menu.
Startup time is measured from game process creation until the overlay first observes the main menu being drawn. If the type lookup optimization is disabled, only startup time is shown at the main menu.

## Settings

To change the settings, create the following file inside your Sea Power game folder:

```text
BepInEx/config/mebiustos.harmonystartupoptimizer.cfg
```

If the configuration file is missing, the mod uses its default settings. Any settings omitted from the file also use their defaults.

### Individual optimizations

Each optimization is enabled by default. Set an option to `false` to disable it:

```ini
[Optimizations]
TypeLookup = true
FileListing = true
EncyclopediaLookup = true
```

`TypeLookup` controls the Harmony result, assembly-type and type-name caches together.

### Startup-only mode

To stop all optimizations after startup:

```ini
[Cache]
StopAfterStartup = true
```

This option defaults to `false` and also works with the overlay disabled.

### Startup overlay

The overlay is enabled by default. To hide it:

```ini
[Overlay]
Enabled = false
```

## Logging

Look for `Harmony Startup Optimizer` entries in `BepInEx/LogOutput.log`.
When active, the mod writes one startup line listing its active modules.
When disabled, it writes one startup line indicating that state.
Startup-only mode writes one line when all optimizations have stopped. Otherwise, initialization, rollback, and shutdown failures are logged as errors.
Original exceptions from the game or other patches continue to propagate to the caller.

The optimizer creates no separate log files or periodic detailed statistics. Startup Diagnostics is optional and logs independently.

## Building and testing

Use .NET SDK 10 on Windows. No NuGet packages are required.
Set `SeaPowerDir` and `AnchorChainDir` in `Directory.Build.local.props`,
or pass `-GameDirectory` and `-AnchorChainDirectory` to `build.ps1`.

```powershell
.\build.ps1 -Test
```

In addition to standalone cache and index behavior tests, the test suite uses Sea Power's bundled Mono runtime and Harmony DLL to patch and exercise the target APIs.
It covers other prefixes and postfixes, argument and result transformations, skipped original calls, original exceptions, missing types,
dynamic assembly loading, recursion, multiple threads, and patch removal by owner.
It also checks file-list ordering, reference index updates, independent module settings, startup timing, and common shutdown with or without the overlay.
The suite verifies that results from before a change are not reused when adding or removing another transpiler changes the lookup implementation.
If Startup Diagnostics is installed, tests use its actual DLL to check both patch installation orders.
If the diagnostic DLL is absent, only the optional compatibility tests are reported as SKIP.
The tests do not launch the game.

The distribution package is generated in `dist/HarmonyStartupOptimizer`.
To build, test, and install it locally, close the game and run:

```powershell
.\build.ps1 -Test -InstallLocal
```

## Source responsibilities

- `TypeLookupCache.cs`: Sharing successful results, invalidation, and concurrency control, without dependencies on the game or Harmony.
- `AssemblyTypeCache.cs` / `TypeNameCache.cs`: Reusing complete type lists and immutable runtime names during first-time searches.
- `LookupHooks.cs`: Connecting to the shared type lookup API while preserving execution order, arguments, results, and exceptions with other patches.
- `FileListingHooks.cs`: Per-call file membership checks while retaining the original directory scans and output list.
- `EncyclopediaLookupHooks.cs` / `ReferenceIndex.cs`: Scoped reference indices for encyclopedia generation and their collection lifecycle.
- `OptimizationSet.cs`: Independent module installation, rollback, and common shutdown.
- `Plugin.cs`: Anchor Chain integration, activation checks, minimal logging, and lifecycle management.
- `StartupOverlay.cs`: Startup status display, resolution scaling, and cleanup of the display component.
- `StartupMonitor.cs` / `StartupLifetime.cs`: Shared startup completion monitoring, display cleanup, and optional shutdown of all optimizations.
- `StartupTiming.cs`: Measuring elapsed time from process creation and fixing the result when the menu is first drawn.
- `StartupSettings.cs`: Reading optional display, lifetime and module settings.
- `StartupScreen.cs`: Reading the game's menu and loading screen state.
- `ModActivation.cs`: Reading Sea Power's mod activation state.
- `PatchRegistry.cs`: Installing, rolling back, and removing only this mod's Harmony patches.

## Source and releases

- [Source code](https://github.com/mebiustos-sea-power/HarmonyStartupOptimizer)
- [Releases](https://github.com/mebiustos-sea-power/HarmonyStartupOptimizer/releases)
