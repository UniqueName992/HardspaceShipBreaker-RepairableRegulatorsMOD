# Repairable Regulators - Technical Reference

This is the deep-dive: how every mechanism actually works and why, what's been tried and discarded, every known issue, and the full diagnostics/configuration reference. For a quick overview, installation, and the feature list, see the [top-level README](../README.md) instead - this document assumes you've already read that.

A [BepInEx](https://github.com/BepInEx/BepInEx) plugin for *Hardspace: Shipbreaker* that paints every atmosphere regulator by status, and lets you repair a Damaged one into a real, working regulator - button, animation, and all.

(This project was originally a single-color "highlight the regulator" mod, then a four-color status plugin named `PurpleRegulators` internally, before the repair feature was added. It's since been fully renamed to `RepairableRegulators` throughout - assembly, namespace, plugin GUID, config file, and deployed plugin folder. If you have an old `BepInEx/plugins/PurpleRegulators/` folder or a `shipbreaker.purpleregulators.cfg` config file lying around from an earlier version, delete them - otherwise BepInEx will happily load both the old and new copies side by side.)

## The four states

| Color | State | Meaning |
|---|---|---|
| 🟢 Green | Pressurized | Working regulator, room is pressurized. |
| 🔵 Blue | Unpressurized | Working regulator, room is not (fully) pressurized. |
| 🟡 Yellow | Damaged | Spawned in already non-functional/damaged, bolted to a wall as-is - not something that happened during play. **Repairable** - see [Repairing Damaged regulators](#repairing-damaged-regulators). |
| 🔴 Red | Broken | Was a working regulator; damaged or torn off during play until its marker was destroyed. |

Priority when more than one could apply: **Broken beats Damaged beats Pressurized/Unpressurized.** In practice Damaged and Broken never overlap on the same regulator (see below), but Broken always wins over the room-pressure colors if a regulator is destroyed mid-scan.

## How the coloring works

**Off by default** (`Appearance.EnableColoring`, see [Configuration](#configuration)) as of the release build - this was the plugin's original, first-built feature, but the repair mechanic turned out to need all the same underlying state-tracking regardless of whether anything actually gets painted, so the tracking always runs; only the renderer tint itself is now gated behind this toggle. Turn it back on for the full original experience, or to help diagnose a regulator that isn't repairing as expected - Damaged (yellow) is often the fastest way to *see* which one needs fixing.

The game's own decompiled source (`BBI.Unity.Game.RoomProbe`) exposes a public `IsAtmosphereRegulator` flag on the component that marks a room's atmosphere regulator. At runtime, the plugin:

1. Scans the scene every couple of seconds for `RoomProbe` components where `IsAtmosphereRegulator` is true.
2. For each one, walks up to its **parent** GameObject (see "Why the parent?" below) and grabs every `Renderer` underneath it. Renderers (and the mesh root's own `EntityBlueprintComponent`, used for the damaged check below) are cached per regulator the first time it's seen, so rescans don't need to walk the hierarchy again.
3. Works out the regulator's state (pressurized / unpressurized / damaged / broken - see below) and, only when that state has changed since the last scan, re-tints every cached renderer whose material exposes `_BaseColor` (HDRP/Lit, which this game uses) or `_Color` (fallback) via a `MaterialPropertyBlock` - never touching the shared `Material` asset directly, so nothing leaks or affects unrelated objects using the same material.

### Why the parent?

`RoomProbe` turned out to be a pure logic marker sitting on its own empty child transform - it has zero renderers of its own. The actual mesh (e.g. `SM_AtmosphereRegulator_Mini_1Bx1xA1_Box`) is one level up, as the **parent** of the `RoomProbe` GameObject. This was found by dumping the scene hierarchy from a running game via the F9 diagnostic (see [Diagnostics](#diagnostics)):

```
HierarchyParent(Clone)/SM_AtmosphereRegulator_Mini_1Bx1xA1_Box/RoomProbe
```

Going up one level lands exactly on the mesh object (3 renderers); going up two levels lands on the whole ship (1500+ renderers) - so one level up is the correct, tightly-scoped target.

### Why `_BaseColor`?

The game ships with HDRP (High Definition Render Pipeline) materials, whose main tint property is `_BaseColor`, not the legacy `_Color`. This was confirmed directly in the game's own code - `BBI.Unity.Game.SpaceTruck3DHABController` recolors a truck's HDRP material at runtime using the exact same `MaterialPropertyBlock.SetColor("_BaseColor", ...)` pattern this plugin uses.

## How the four states are determined

### Pressurized / unpressurized (green / blue)

Each `RoomProbe` has a `RoomEntity` property that resolves (through the game's ECS world) to the `Entity` for the room it belongs to. The game's own `ECUController.ToggleAllAtmosphereRegulators()` reads a room's pressurization exactly this way:

```csharp
EntityManager entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
entityManager.TryGetComponent<RoomComponent>(mProbe.RoomEntity, out var room);
bool isPressurized = room.AirPressureState == RoomPressurization.Pressurized;
```

This plugin uses the identical pattern. `RoomPressurization` actually has eight values (`Decompressed`, `Decompressing`, `Pressurizing`, `Pressurized`, `Breaching`, `Breached`, `ControlledPressurizing`, `ControlledDecompressing`); everything except `Pressurized` is treated as "unpressurized" (blue), matching the simple two-state distinction that was asked for. If the room entity can't be resolved (e.g. very early in load, or - see [Known Issues](#known-issues) - a repaired regulator that never got wired into a room), the regulator defaults to blue rather than guessing.

### Damaged (yellow) - spawned in already non-functional

The F9 diagnostics turned up something neither the "stand-in mesh" nor the "integrity" theories predicted: on a test ship with 6 atmosphere regulator meshes, exactly 3 had a working `RoomProbe` under them and 3 had **none at all** - not inactive, not destroyed, just never wired up with one, ever. Those 3 were the ones that looked pre-damaged in game. Their `StructureGroup` still reported full integrity (`30/30`), even after deliberately damaging one during play - so the lower salvage value on this variant isn't live damage data, it's just that variant's own fixed, pre-configured worth. Atmosphere regulators turn out not to have a graduated "wounded but still working" state at all: they're either a fully working prefab, or this separate always-non-functional prefab, or destroyed.

There's no ECS state to key off for "is this one of the non-functional spawned-in variants," so the plugin falls back to name-matching: any transform whose name contains `AtmosphereRegulator` (excluding the `..._Button` interaction prop and the `GRP_...` StructureGroup, both of which share the substring) that has no `RoomProbe` anywhere under it, and never had one, is treated as Damaged and painted yellow directly.

This is also what keeps "spawned in non-functional" (yellow, permanent) distinct from "was working, then destroyed" (red): the plugin remembers whether a given regulator was ever seen with a live `RoomProbe`. If yes and it later has none, that's Broken. If no and it never gets one, that's Damaged. An `ObjectIntegrityComponent.Value < ValueMax` check (reached via the mesh entity's `StructureGroupMember.GroupEntity`, since integrity lives on the StructureGroup sibling, not the mesh's own entity) still runs as a second, independent way into the Damaged/yellow color, in case some other regulator variant does have graduated damage - but for atmosphere regulators specifically, the name-match path above is the one that actually fires.

Gotchas along the way, all now fixed:

- `FindObjectsOfType<RoomProbe>()` with no arguments **skips inactive GameObjects**, so the scan explicitly passes `includeInactive: true`.
- The regulator mesh's `EntityBlueprintComponent` isn't necessarily present the very first scan a `RoomProbe` is seen (ECS initialization can lag a frame or two behind Unity's object graph). The plugin used to resolve it once and cache the miss forever; it now keeps retrying every scan until it resolves.
- **A regression**: the first version of the name-matching pass marked every matched transform as "already handled" before checking whether it used to be a working regulator - so a regulator that went from working to destroyed got wrongly absorbed here instead of being colored Broken/red. Fixed by only marking it handled once it's confirmed to be the *never-had-a-probe* case.

### Broken (red)

This supersedes the old README's "stand-in mesh" theory, which decompiling further ruled out. Here's what's actually going on:

- `RoomProbe.RoomEntity` calls `EntityBlueprintComponent.TryGetOrAddEntityBlueprint(base.gameObject, ...)` **on the `RoomProbe`'s own (empty marker) GameObject** - meaning the marker has its own ECS entity, separate from the mesh's entity.
- `RoomProbe.DestroyProbeObject()` adds a `PendingDestruction` component to **that same marker entity** - not the mesh's entity.
- `DestroyObjectsSystem` (which processes `PendingDestruction` every frame) then calls `Object.Destroy()` on the GameObject tied to that entity - i.e. it destroys **only the empty marker child**, not the parent mesh.

There is no wreckage/debris mesh-swap system anywhere in `BBI.Unity.Game.dll` tied to `RoomProbe` or `DestroyProbeObject` (confirmed by grepping the full decompiled source for callers - there are none in compiled code, so it's wired up as a Unity Editor `UnityEvent`, most plausibly from `RoomProbe`'s own `RoomStateChange` trigger firing on `RoomPressurization.Breached`, i.e. a catastrophic hull breach destroys the regulator). The regulator's mesh simply stays right where it was, fully intact and renderable - it just no longer has a `RoomProbe` child.

Routine harvesting (cutting the regulator free as salvage) goes through a different method, `RoomProbe.DisconnectProbe()`, which does **not** destroy anything - it just posts a `RoomProbeDisconnectedEvent`. So a regulator that's simply been cut loose and is floating as salvage is not treated as "broken"; only a regulator whose marker has actually been destroyed is.

Given that, the plugin tracks regulators by their **mesh root transform** (not by `RoomProbe` instance) across scans:

1. Every regulator with a live `RoomProbe` is colored green/blue as above.
2. Any previously-tracked mesh root that no longer has a live `RoomProbe` child, but whose GameObject still exists, is treated as **broken** and painted red - permanently, since a destroyed marker never comes back.
3. Any previously-tracked mesh root whose GameObject has *also* been destroyed (e.g. the whole ship was scrapped/despawned) is simply forgotten, so the tracking dictionary doesn't grow unbounded over a play session.

This was a reasoned hypothesis built from static analysis of the decompiled ECS pipeline before it was tested live - it's since been confirmed: damaging a working regulator enough to destroy it, and tearing one off the wall and wrecking it, both correctly turn it red.

## Repairing Damaged regulators

Look directly at a Damaged (yellow) regulator from short range (1.5m by default, matching the game's own interaction distance) and **hold F6** (configurable, see [Configuration](#configuration)) for 3 seconds by default to repair it into a working one. F6 is the default only because it's unbound in the base game - F itself is already the flashlight toggle.

While you're looking at one, a prompt appears on screen styled after the game's own interaction prompts - a circled key icon beside a dark panel reading **Repair** / **Atmosphere Regulator** / **Restore Functionality** (that last line picked out in amber, the same way the game highlights the actual effect of an action - e.g. "Toggle Pressurization" - under the action/object name). The verb switches to **Repairing** and an amber progress bar grows underneath the panel while the key is held down. See `Behavior.ShowRepairPrompt` in [Configuration](#configuration) to turn the prompt off. It only shows up while a regulator is genuinely both in range *and* Damaged, using the exact same look-at-it check that drives the hold timer itself - the prompt and the timer can never disagree about what you're looking at, since both come from one shared lookup.

Progress resets to zero - instantly, no partial credit kept - the moment you look away, move out of range, switch to looking at a *different* Damaged regulator, or release the key early. There's no way to "bank" partial progress across separate attempts; each hold has to run the full `Behavior.RepairDurationSeconds` uninterrupted.

Only Damaged regulators are repairable - Broken (red, destroyed) ones are left alone, since that represents an actually-destroyed marker rather than one that was simply never wired up (see [Broken (red)](#broken-red) above).

### Finding what you're looking at

There's no per-part collider to raycast against here - individual salvage parts (regulators included) turned out not to carry their own collider at all; a raycast from the camera lands on the ship's single combined hull collision mesh instead, so the hit transform is always some ship-wide root object, never anything specific to the regulator. Instead of raycasting for an exact hit, the plugin raycasts for a hit *point*, then finds whichever tracked regulator's own renderer bounds that point is closest to (see `RepairMatchRadiusMeters` below) - i.e. "closest to where you're looking," rather than "directly hit."

### Why the prompt isn't drawn through the game's own UI

The game's own interaction prompts (`ContextualButtonPrompt`) are tied to a specific, closed set of predefined actions (`GameplayActions.GameplayActionSet` and friends) - each with its own pre-authored icon/sprite per platform, looked up through the Doozy UI theme system. There's no "repair" entry in that enum to hook into, and no way to add one from a BepInEx plugin without either patching the compiled enum (fragile, version-specific) or building out a full parallel binding/sprite/theme entry for a key that isn't part of the base game to begin with.

Rather than fight that system, this hand-draws a lookalike instead, entirely through `OnGUI`: a hollow amber ring with the bound key inside it (a small procedurally-generated texture - `MakeRingTexture` in the source, no image asset needed), beside a dark panel with the verb, the object name, and the effect picked out in amber underneath, sized to the game's own reference screenshot layout - circle on the left, panel growing to fit the text on the right, roughly centered on screen. It won't track the actual regulator's screen position the way the real prompt tracks its target (this is a fixed screen-space overlay, not a world-space one), and the font is a system monospace fallback (`Consolas`) rather than the game's own, but it reproduces the shape and color language closely enough to read as "one of these" at a glance - without needing any new asset references, a UI prefab, or carrying any risk of breaking if the game's own binding/theme data changes shape in a future update.

### How the repair works

A Damaged regulator has no `RoomProbe` anywhere under it at all (that's what makes it Damaged in the first place - see above). An early version of this repair just bolted on a bare, empty `RoomProbe` - which restored pressurization, but nothing else: no button to press, no animation, none of the door-toggle triggers a real regulator has. Those all turned out to be wired through serialized Unity `UnityEvent` listeners baked into the working prefab, invisible to decompiled code and not something reflection can reconstruct. So the plugin instead **borrows the internals of a real, currently-working regulator from elsewhere on the same ship** and grafts them onto the Damaged one:

1. Finds any other regulator on the ship the plugin is tracking as currently Pressurized or Unpressurized, to use as a donor.
2. Temporarily moves every direct child of the donor's mesh root (`RoomProbe`, its button - an object named "Interaction Volume" - its animator - `Anim_AirlockLights_Root` - and whatever supporting FX/lighting objects sit alongside them, all confirmed via the F9 diagnostics to be direct children of the mesh root) onto a bare, empty, throwaway GameObject.
3. Clones *that* throwaway object (not the donor's real mesh root - see the cloning hazard below for why that distinction matters) and immediately moves the donor's real children back to exactly where they were, so the donor itself is never left in a disturbed state.
4. Moves every child of the clone onto the Damaged regulator's own mesh root, preserving local offsets, then discards the now-empty clone shell.
5. Wires the moved probe into a room (see below) and registers it with the ship's ECU (see below).

Because `Object.Instantiate` remaps references between objects within the one hierarchy it's given, `RoomProbe.InteractableObject`/`.Animator` on the result correctly point at its *own* button/animator rather than the donor's, and any `UnityEvent` triggers wired between them in the editor (button press animation, door toggles, etc.) come along intact - all without needing to know or reconstruct any of that serialized wiring by hand.

If no other regulator on the ship is currently working at all, there's nothing to clone from - the plugin falls back to the original bare-`RoomProbe` approach (pressurization only, no button or animation), logging clearly that it did so.

**The `EntityBlueprintComponent` cloning hazard, and why a throwaway parent is used instead of cloning the donor's mesh root directly:** `EntityBlueprintComponent.Initialize()` has two very different paths - if its serialized `mLastKnownEntityIndex`/`mLastKnownEntityVersion` are non-zero, it treats itself as *reconnecting* and literally clones whichever ECS entity that index pointed to, carrying over all of that entity's current data. Since `Object.Instantiate` faithfully copies serialized fields, cloning anything already initialized this session (any working regulator's components) sends every `EntityBlueprintComponent` in the cloned subtree down that wrong path unless those two fields are reflectively zeroed immediately before `Instantiate` and restored immediately after (`Awake()` runs synchronously *during* `Instantiate`, so by the time it returns the clone has already taken the correct "fresh entity" path). That part alone was always necessary and still is.

What changed is *what* gets cloned. An earlier version applied that same zero/restore trick directly to the donor's whole mesh root and cloned it wholesale, harvesting the useful children out of the clone and discarding the rest. That worked for the repaired regulator itself, but it introduced a new, nastier bug: the mesh root also carries a `StructurePart`/blueprint-backed ECS entity tied into the ship's real structure graph - a shared `StructureGroup`, physics joints, salvage value (see the `StructureGroupMember` info in the F9 diagnostics). Destroying even a *temporary* clone of that entity had side effects that ended up turning a *different, unrelated* regulator Broken/red. A bare, empty, never-initialized GameObject has none of that structural baggage, so borrowing the donor's children onto one of those instead - rather than ever touching a copy of the donor's actual structural entity - avoids the problem entirely.

### Wiring the probe into a room

The game's own `RoomProbeInitializeSystem` is what would normally do this: scan for any `RoomProbe` entity missing a `RoomProbeComponent`, work out which room's bounding volume its position falls inside, and wire it up. It turns out to only ever run once, during the ship's *initial spawn sequence* - `SingletonShipSpawning.CanInitializeProbes` requires `!ProbesInitialized`, and `ProbesInitialized` gets set `true` right after that one pass and never reset (confirmed by decompiling `SingletonShipSpawning`). A probe added mid-shift - like a repaired one - is invisible to it forever. And because `RoomProbe.RoomEntity` treats "no room could be resolved" exactly the same as "room is unpressurized," a probe that never got wired at all *still turns blue* right on schedule - it just never actually does anything. That's a functionally dead regulator that looks, at a glance, like a working one.

So the plugin replicates that system's room lookup itself (`TryWireProbeIntoRoom` in the source): the exact same `RoomVolumeIncludeData`/`RoomVolumeExcludeData` oriented-bounding-box test, done directly against the probe's world position, adding and filling in the same `RoomProbeComponent`/`AirPressureState`/`PreviousAirPressureState`/etc. the original system would have - synchronously, the instant the repair key is pressed, rather than waiting on a system that will never run again.

### The ship's manual air-pressure toggle control

Individually, a regulator's own button only toggles *that* regulator's own room. Ships also have a separate, ship-wide `ECUController` "machine" elsewhere that can toggle every regulator at once. It builds its internal list of every `RoomProbe` whose `IsAtmosphereRegulator` is set, exactly once, a few fixed-updates after the ship finishes spawning (`ECUController.RegisterRoomProbes()`, found by decompiling the game's own code) - by walking the ship's rigidbody hierarchy for whatever probes exist *at that moment*. A probe added later has no way to get into that list on its own; it pressurizes/depressurizes its room just fine, and its own button works fine too, but the ship-wide ECU toggle silently skips it forever, since it never learns the probe exists.

So repairing a regulator also reaches into that same private list (via reflection, since there's no public API to add to it after the fact) and adds the new probe - after first confirming the `ECUController` found really is this ship's, by checking the new probe's transform is inside that ECU's own `StructurePart.VirtualRigidbody.UnityRigidbody` hierarchy (in case more than one ship/ECU exists in the scene at once, e.g. a newly claimed ship waiting alongside the current one).

### Known limitation: the mesh doesn't change

Repairing a Damaged regulator grafts on a real, fully working `RoomProbe`/button/animator - it doesn't touch the Damaged regulator's own mesh/`StructurePart`/salvage entity at all (deliberately - see the cloning hazard above). The non-functional variant is a genuinely separate prefab from the working one (see [Damaged (yellow)](#damaged-yellow---spawned-in-already-non-functional) above), so after a repair the regulator is fully functional but still *looks* like the pre-damaged mesh, just recolored blue/green instead of yellow. Purely cosmetic - doesn't affect pressurization, the button, the animation, or the ECU toggle - but worth knowing going in.

## Known Issues

### Shift-abandon revert and Free Play/Cutter's Race unlimited kits (Implemented, not yet tested live)

Two Air Reg Kit behavior changes - described in full under [Abandoning a shift reverts it; Free Play/Cutter's Race ignore it entirely](#abandoning-a-shift-reverts-it-free-playcutters-race-ignore-it-entirely) - were implemented straight from reading `GameSession`/`SceneLoader`/`PauseMenuController` in the decompiled source, without a live test pass yet (unlike almost everything else in this document, which was fixed only after live reproduction). Specifically unverified:

- That abandoning a shift (via the pause menu's "Abandon Shift" button, mid-shift, before reaching the Hab) actually reverts `_repairPackCount.Value` to its pre-shift baseline, and that a normal Hab-then-continue or Hab-then-quit run never triggers a revert it shouldn't.
- That `GameSession.CurrentSessionType` really does read as `FreeMode` in Free Play and `Challenge` in Cutter's Race specifically (inferred from how `GameSuccessMessageController`/`SceneLoader` branch on `SessionType.Challenge`, not from having played either mode with this plugin active) - if either is wrong, that mode would keep using the real saved count instead of the flat 100.

`[Shop] Shift ended without reaching the Hab - reverting Air Reg Kits from X back to Y.` in `BepInEx/LogOutput.log` confirms a revert actually fired; its absence after an intentional abandon would mean the session-type/game-state read is off and needs a different signal.

### The Air Reg Kit counter disappears while the shop is open (Still open - one theory disproven)

Reported: the persistent `AIR REG KITS[N]` HUD readout was only ever drawn during `GameSession.GameState.Gameplay` - but opening the ship's vending machine apparently changes `CurrentGameState` away from `Gameplay`, hiding the counter exactly when it'd be most useful (deciding whether to buy more while already looking at the shop).

**First theory, now disproven:** guessed the shop reuses `GameState.Paused`, since `VendingMachineUIController`'s own state-change handler already specifically reacts to `Paused` (by hiding its own tab display) - and broadened `IsHudVisibleGameState` from a strict `== Gameplay` check to an exclusion list (hiding only for loading/front-end/NIS/game-over-or-complete) to allow for that. Confirmed live this was wrong on *both* counts: the counter still didn't show in the shop, and `Paused` turned out to be the real ESC-pause-menu state after all - broadening to include it just made the counter incorrectly show there too, a regression on top of not fixing the original report.

**Reverted** back to the original strict `== Gameplay` check, so the counter at least stops showing over the real pause menu again. The shop case is still unsolved, and evidently isn't explained by any reasonably-guessable `GameState` value - rather than guess a third time, `LogGameStateChanges` now logs `GameSession.CurrentGameState` every time it actually changes, so opening the shop next time will show its real value directly in `BepInEx/LogOutput.log` instead of needing another guess.

### A repaired regulator's own light never changes color like a real one's does (Reported, root cause not yet confirmed)

Reported: unlike this plugin's own overlay tint (the green/blue/yellow/red recoloring documented above, which does work correctly on repaired regulators), the regulator's *own* native indicator light - presumably part of `Unlock_01`, animated via `Anim_AirlockLights_Root` - never seems to change color on a repaired one the way it does on a real, never-broken one.

**The pipeline that's supposed to drive this, traced through the decompiled source:** `RoomStateTransferSystem` runs every single frame (not gated behind any one-shot "already initialized" singleton, unlike the systems behind earlier bugs this project hit) and keeps a probe's own `AirPressureState`/`PreviousAirPressureState` components in sync with its room's actual state. Whenever that sync changes the value, `AirPressureChangeNotificationQueueSystem` notices the mismatch and queues a notification; `AirPressureChangeNotificationProcessSystem` then calls `RoomProbe.OnProbeStateChanged(current, previous)`, which fires the probe's own `m_Triggers` - private, editor-wired `UnityEvent`s with no trace in decompiled code, presumably what actually drives the Animator/light.

In principle, none of this should be broken for a repaired regulator: `TryWireProbeIntoRoom` already adds `RoomProbeComponent`/`AirPressureState`/`PreviousAirPressureState` to the grafted probe's entity, pointing at a real, valid room, and the cloned `RoomProbe`'s own `m_Triggers` (and the Animator they presumably reference) were cloned together in the same `Object.Instantiate` call that correctly preserved the button and animator reference wiring - so this *should* just work the same way it does for a real regulator. Since it apparently doesn't, something in this chain is breaking specifically for a grafted probe, and it isn't obvious which link from reading the code alone.

**Next step, not yet done:** F9's `[Diag] Connectivity for '<name>'` line now also dumps the probe's own `AirPressureState`/`PreviousAirPressureState` values and whether it has `RoomProbeComponent`/`NotifyOnAirPressureStateChange` at all. Toggling a repaired regulator's room pressure and comparing two F9 dumps (before/after) will show whether `RoomStateTransferSystem` is even updating those values for a grafted probe - if it is, the break is somewhere later (the notification chain, or the triggers/Animator themselves); if it isn't, the break is in whatever makes this plugin's probe not match that system's query, which would need comparing against a real regulator's same dump to find.

### A never-repaired Damaged regulator torn loose by the environment stays falsely "repairable" (Fixed)

Narrowed down via a precise live test: repairing a regulator and *then* having an explosion tear it off the wall works correctly - it turns Broken/red and is correctly refused as not repairable, exactly as the fix below describes. The bug is actually a *different*, previously-unidentified case: a regulator that's **never been repaired** (still Damaged/yellow, no `RoomProbe` at all) torn loose by the environment (an explosion, not the player deliberately cutting it free) - it stays lit up as repairable, and attempting the repair fails.

**Root cause:** a never-repaired Damaged regulator has no `RoomProbe`, so `CheckGraftedProbesStillConnected` never looks at it at all (that check only ever runs for regulators this plugin has grafted something onto). Nothing else in this plugin ever changes a Damaged regulator's color state except *gaining* a probe - so once torn loose, it just goes on reading as "Damaged" (and therefore "repairable") forever, regardless of whether it's still actually attached to anything. The repair itself then fails because the regulator's current position, now wherever the explosion sent it, is no longer inside any room's bounding volume for `TryWireProbeIntoRoom` to find.

**Fix, confirmed live:** every tracked regulator - not just repaired ones - now records the `Rigidbody` it had the very first time it was ever seen (`RegulatorEntry.InitialRigidbody`, set once in `GetOrCreateEntry`). `TryFindRepairableRegulatorLookedAt` (the same check driving both the hold timer and the prompt) now also requires the regulator's *current* rigidbody to still match that baseline before offering it as repairable at all - the same rigidbody-identity signal the already-working post-repair zombie fix uses, just applied as a precondition for starting a repair instead of a trigger for tearing one down afterward.

### Repaired regulators kept working forever, even cut loose off the wall (Fixed)

A regulator repaired via this plugin didn't respect being torn loose the way a real, never-broken regulator does - it could be cut/pulled clear off the wall and would keep right on working (still pressurizing its room, still responding to its button) as if nothing happened.

**Root cause:** nothing in the base game ever clears or disconnects a probe's room link on its own - `RoomProbe.DisconnectProbe()` (called when a real regulator is cut loose as salvage) only ever posts a `RoomProbeDisconnectedEvent`, and the *only* handler of that event anywhere in the game is `ECUController`, which just stops including it in the ship-wide toggle. What's specific to a **grafted** probe is that it also has no tie whatsoever to its host `StructurePart`'s own connectivity - a real regulator's probe lives on the same structural entity that gets cut, a grafted one is just an unrelated child object sitting on top of it, so there was never anything that *could* notice the host being cut free in the first place.

**The fix, and how it was actually found:** rather than guess, F9 was extended to dump every connectivity-ish signal available (see [Diagnostics](#diagnostics)), and two live before/after tests nailed it down precisely:

1. A **real, never-repaired** regulator pulled off the wall: its `StructureGroupMember`/`GroupEntity` stayed *exactly the same* the whole time. Its `RoomProbe` and animator were destroyed outright and its button swapped to a different "Unpowered Interaction Volume" object - but through some mechanism with no trace in decompiled code (almost certainly another editor-wired `UnityEvent`, same as the cases documented elsewhere in this README).
2. A **repaired** regulator pulled off the wall: `StructurePart.IsPartOfGroup` likewise stayed `true` throughout, but `StructurePart.VirtualRigidbody.UnityRigidbody` changed to a different instance the moment it was cut free - confirmed by comparing Unity instance IDs logged before and after.

So `IsPartOfGroup` is not a usable disconnect signal at all - it simply doesn't change. The Rigidbody identity is: cutting a part free gives it its own new physics body, distinct from whatever it shared with the rest of the ship. An earlier version of this check required **both** signals to agree before tearing anything down (out of caution, since destroying a clone of a `StructurePart`'s own entity had separately proven capable of disturbing an unrelated sibling's `StructureGroup` state - see the cloning hazard notes under [How the repair works](#how-the-repair-works)). That caution is exactly what let the zombie survive its first fix attempt: the rigidbody demonstrably changed, but `IsPartOfGroup`'s stuck-`true` reading vetoed the teardown every time.

`CheckGraftedProbesStillConnected` (in the source) now decides purely on the `VirtualRigidbody` check, run every scan for every repaired regulator: if its current `Rigidbody` no longer matches the one recorded at repair time, the regulator correctly falls back to Broken/red. `IsPartOfGroup` is still logged alongside it (as `[Repair] Connectivity check for '<name>': IsPartOfGroup=..., sameRigidbodyAsWhenRepaired=...`) purely for visibility, in case it's ever needed again.

Confirmed live: it does correctly turn Broken/red a couple of seconds after being pulled off the wall. One thing the first version of this fix missed - only the `RoomProbe` itself was being destroyed on disconnect, but the button ("Interaction Volume") is a *separate sibling* object, not a child of `RoomProbe`, so it survived fully interactable (just doing nothing) even after the probe was gone. Fixed by having the repair record *every* top-level object it grafted on (probe, button, animator, FX/audio siblings - see `RegulatorEntry.GraftedObjects` in the source) and destroying all of them together on disconnect, rather than just the probe.

## Shop: "Air Reg Kit"

[ROADMAP.md](ROADMAP.md) item #2 (purchasable repair packs): the plugin adds an "Air Reg Kit" item - 200,000 credits by default (`Shop.RepairPackCostCredits`) - to the ship's vending machine (the shop out with the ship during a shift). Buying one increments a count; repairing a regulator now actually requires owning at least one, and consumes exactly one on success.

Confirmed live, working, listed right alongside the real items (Oxygen, Suit Patch Kit, Thruster Fuel, Medkit, Tethers, Demo Charges, Tool Repair Kit). The name started out as "Air Regulator Repair Pack" - functionally fine, but visibly too long: the shop list auto-shrinks an item's name to fit its row, so it rendered noticeably smaller than every other entry instead of matching their size. Shortened to "Air Reg Kit" to match the list's existing naming convention (short, often abbreviated - "Suit Patch Kit," "Tool Repair Kit") and sit at the same size as everything else.

### How a new item gets into a UI that was never built to take one

`VendingMachineUIController` builds its entire item list exactly once, in `Awake()`, from a `m_StoreTabs` array of `(tab name, container, VendingMachineItemAsset[])` configured per-machine at design time - there's no public method to add an item afterward, and `VendingMachineItemAsset`/`VendingMachineItemData` are only ever authored as Unity asset files, never constructed at runtime in the game's own code.

Since none of that is fundamentally closed off, just private, this reaches past it the same way everything else in this plugin does:

1. Finds the live `VendingMachineUIController` and borrows the `CurrencyAsset` reference off whatever real item it finds first in any tab - so the new item charges in the same currency as everything else without this plugin needing to locate or construct a `CurrencyAsset` of its own.
2. Builds a `VendingMachineItemAsset` via `ScriptableObject.CreateInstance` (no asset file needed - this is a real engine type, just normally only ever instantiated from a saved asset; creating one fresh at runtime works identically) and fills in its privately-backed `Name`/`Description`/`Currency`/`Cost` fields via reflection, the same pattern used throughout this plugin for every other getter-only data class.
3. Points its `OnPurchaseAction` at `PurchaseRepairPack` - a new, minimal class this plugin defines itself, deriving from the game's own `VendingMachinePurchaseBase`. The game's existing purchase-action subclasses (`PurchaseOxygen`, `PurchasePatch`, etc.) all point at other pre-authored assets this plugin has no way to construct, so rather than fight that, `PurchaseRepairPack` is a from-scratch implementation of the same (tiny) abstract contract.
4. Calls the controller's own private `AddItem` method directly - the exact same method the controller uses internally for every item it ships with, just invoked by hand, once, after the fact, onto whichever tab's container the borrowed currency came from.

Re-checked every scan (not just once) since a new shift spawns a brand new `VendingMachineUIController` instance - the previous shift's is destroyed along with its scene - so the plugin tracks which *specific* instance it's already patched, not just whether this has ever happened at all.

### What's solid and what's still untested here

The purchase flow itself (currency-sufficiency check, deduction, `TryPurchase` callback, button state) is **entirely the game's own code**, completely unmodified - this plugin only ever constructs the data describing a new item and routes it through that same unmodified flow, rather than reimplementing any part of purchasing itself. Confirmed live: it lists correctly alongside the real items, at the same size and style once the name was shortened (see above).

**The name and purchasing are both confirmed fully working live** - the name displays at full size matching the rest of the list, and clicking it correctly deducts the expected cost.

**The description not showing was real.** First fix attempt: `LynxButtonController` (the base class `VendingMachineUIController` builds on) caches every `LynxButton` under itself exactly once, in its own `Awake()` - well before this plugin ever adds its item - into a private `mButtons` array, and every frame force-reselects the first button in that array if nothing in it is currently selected. Our late-added item is invisible to that array, so calling the controller's own public `Initialize(trySelectFirstButton: false)` right after adding the item (to repopulate that cache) seemed like the right fix. **It wasn't enough - confirmed live, description still didn't show after that fix.**

The real culprit, found by reading `SetPreviewAndDescription` line by line: it calls `MaterialPropertyBlock.SetTexture(propertyName, texture)` - which **throws if `texture` is null** - on the line *immediately before* the one that actually updates the description text. Since this plugin never set a preview texture (there's no real 3D item behind "Air Reg Kit" to generate one from), that call was throwing every time the item was selected, silently aborting the rest of the method before the description line ever ran - while everything *before* that point in the call chain (the highlight, `UpdateState()`) had already completed, making it look like a selection problem rather than an exception. **Fix, confirmed live:** stop passing a null texture at all.

Rather than a bare placeholder, the fix borrows the Suit Patch Kit's own `PreviewMesh`/`PreviewTexture`/`PreviewScale`/`PreviewOrientation` - close enough in spirit (another small repair-ish consumable) to look at home in the list, and it's a real rotating 3D preview instead of a blank fallback. Identified by its actual purchase-action type, `existingItem.Data.ScriptingPrefab is PurchasePatch`, rather than by name - a real item's `Name` is a localization ID, not literal display text, so there's no `"Suit Patch Kit"` string anywhere to match against. The 1x1 placeholder texture is kept as a fallback only, in case that item can't be found at all (logged as a warning if so), purely to guarantee the crash stays fixed either way.

What's still unconfirmed:

- The borrowed Suit Patch Kit preview mesh/texture/scale/orientation - not yet seen rendering live.
- Only the *first* `VendingMachineUIController` found is patched; if more than one is ever active at once (e.g. a next-shift ship pre-spawning before the current one is cleaned up), only one would get the item.

### Owning and spending a kit

Buying an Air Reg Kit increments a `ConfigEntry<int>` (`Shop.OwnedAirRegKits`, `Plugin._repairPackCount` - not a real inventory item, see [What's still unconfirmed](#whats-solid-and-whats-still-untested-here) above) rather than a plain in-memory field, so it rides on BepInEx's own config save/load and survives a restart. A persistent `AIR REG KITS[N]` readout is drawn any time the game is actually being played, independent of whether a regulator is in view, so owning zero is exactly as visible as owning several - amber at one-or-more, dimmed gray at zero.

Styled to match the game's own bottom-left HUD elements (`THRUSTERS`, `TETHERS[1000]`) rather than the repair prompt's dark panel - bold caps, bracketed count, no background box, sitting directly over the 3D world the way the rest of the HUD does. IMGUI has no built-in text outline the way the game's own shader-driven HUD text does, so `DrawOutlinedLabel` fakes one the old way: stamping the label several times in a dark color at small pixel offsets before drawing the real color on top, so it stays readable without a panel behind it. Positioned a bit down the left edge (a fraction of `Screen.height`, not a fixed pixel offset, so it stays roughly in place across resolutions) rather than the literal top-left corner it started in - that corner turned out to be exactly where some players' own debug/performance overlays (FPS, VRAM, GPU%) already live, which render on top of anything else anchored there.

Repairing now actually spends one: `UpdateRepair`'s hold timer simply won't start accumulating progress without owning at least one (going through `EffectiveRepairPackCount`, not the raw saved count - see below), and the on-screen repair prompt reflects this directly rather than just silently refusing - looking at a Damaged regulator with zero kits still shows the prompt (so it's clear the regulator itself *is* repairable), but the key circle and the bottom line both render in the same dimmed gray as the counter, and the bottom line itself changes from "Restore Functionality" to "Requires Air Reg Kit". Completing a hold (which, by definition, could only have started with a kit in hand) consumes exactly one, logged as `[Shop] Consumed an Air Reg Kit repairing '<name>' - N left.`

### Abandoning a shift reverts it; Free Play/Cutter's Race ignore it entirely

**Not yet confirmed live - implemented from reading `GameSession`/`SceneLoader`/`PauseMenuController`, needs an actual abandon/Free-Play/Cutter's-Race test pass.**

Two deliberate departures from "the saved count is just whatever it last was":

- **Abandoning a shift reverts any kits bought (or spent) during it.** `UpdateRepairPackPersistenceTracking` snapshots `_repairPackCount.Value` into `_repairPackBaselineBeforeShift` whenever a Career or WeeklyShip session starts, and moves that snapshot forward every frame the player is actually in the Hab (`GameSession.GameState.Hab`) - mirroring `SceneLoader.PreTearDown`, which only saves the player's profile when leaving a Career session *from* the Hab, never mid-shift. If `GameSession.CurrentSessionType` drops back to `None` (session torn down to the front end) without the baseline ever having moved past its last Hab-visit value - i.e. the current shift never reached the Hab - `_repairPackCount.Value` is reset back to that baseline, undoing whatever happened during the abandoned shift. A clean run (reach the Hab, however many times, before eventually quitting) never triggers this; the baseline keeps catching up to reality every time the Hab is reached.
- **Free Play and Cutter's Race (`GameSession.SessionType.FreeMode`/`Challenge`) don't use the saved count at all.** `EffectiveRepairPackCount` - what the repair gate and the on-screen counter actually read - returns a flat `100` in those two session types instead of `_repairPackCount.Value`, and the repair-consume step skips decrementing the real value there too (so grinding repairs in Free Play can't silently drain, or go negative on, your real Career stock). Buying a kit *while* in one of these modes still increments the real saved count, though - the credits are already spent by the game's own unmodified purchase flow regardless of what this plugin does with the result, so letting that purchase still bank a kit for a later Career session beats letting it go to waste. WeeklyShip is treated the same as Career (real economy, abandon-reverts) rather than lumped in with Free Play/Cutter's Race, since it wasn't named as one of the "just give me plenty" modes.

## Diagnostics

**Off by default** (`Behavior.EnableDiagnostics`, see [Configuration](#configuration)) for a clean release build - turn it on first if any of the below doesn't seem to do anything.

Press **F9** in-game to dump every object in the scene whose name contains "Regulator" to `BepInEx/LogOutput.log`, regardless of whether it has a `RoomProbe`. For each match it logs the full hierarchy path, whether a `RoomProbe` exists under it (and its `IsAtmosphereRegulator`/active state, and - if so - where its `InteractableObject`/`Animator` sit relative to the mesh root), whether it has a resolvable `EntityBlueprintComponent` and `ObjectIntegrityComponent` (with current/max values), and the names of every renderer underneath it. If it has a `RoomProbe`, it also dumps a `[Diag] Connectivity for '<name>'` line: its `StructurePart`/`VirtualRigidbody` state, the room it's wired to (and that room's own `AirPressureState`/`AllowAirPressureToggle`/`ExternallyExposed`/`Sealed`/`Breached`), and its button's `IsInteractable`/`InteractableLockable` state. This is the tool that found the group-entity mismatch, the no-RoomProbe-at-all variants, the button/animator hierarchy relationship the repair feature relies on, and - by comparing two dumps of the same regulator before and after being cut off the wall - the actual signal (`VirtualRigidbody` changing) that the "regulators never die" fix in [Known Issues](#known-issues) is built on. Reach for it first whenever a particular regulator isn't coloring (or repairing, or dying) as expected.

Repairing also logs heavily under an `[Repair]` tag at every step - key press, raycast hit point, which tracked regulator matched, whether a donor was found to clone from, the room lookup result, and the ECU registration result - all synchronously, in the same frame the key is pressed. A repaired regulator is also re-checked every scan afterward, logging `[Repair] Connectivity check for '<name>': IsPartOfGroup=..., sameRigidbodyAsWhenRepaired=...` whenever either value changes, so its connectivity can be watched over time without needing to press F9 again. Worth checking `BepInEx/LogOutput.log` immediately after any repair (or disconnect) that doesn't seem to have worked.

## Project layout

- [Plugin.cs](../Plugin.cs) - the plugin.
- [RepairableRegulators.csproj](../RepairableRegulators.csproj) - builds against `net472`, referencing `BepInEx.dll`, several `UnityEngine.*Module.dll`s, `Unity.Entities`/`Unity.Collections`/`Unity.Mathematics`, and `BBI.Unity.Game.dll` pulled straight from the installed game. Also auto-copies the built DLL into the game's `BepInEx/plugins/RepairableRegulators/` folder after every build.
- [ROADMAP.md](ROADMAP.md) - future feature ideas (repair timers, paid repair packs, a skill unlock, a remote-control tool, a compartment-matching tool) that haven't been started yet.

## Building

```powershell
dotnet build -c Release
```

If your game isn't installed at the default Steam path, override it:

```powershell
dotnet build -c Release -p:GameDir="D:\Games\Hardspace Shipbreaker"
```

## Configuration

After the first run, a config file is generated at `BepInEx/config/shipbreaker.repairableregulators.cfg`:

| Setting | Default | Description |
|---|---|---|
| `Appearance.PressurizedColor` | `#2ECC71` (green) | Hex color for regulators whose room is pressurized. |
| `Appearance.UnpressurizedColor` | `#3498DB` (blue) | Hex color for regulators whose room isn't (fully) pressurized. |
| `Appearance.DamagedColor` | `#F1C40F` (yellow) | Hex color for regulators that spawn in already non-functional (or, in principle, have object integrity below max). Overrides the pressurized/unpressurized colors. |
| `Appearance.BrokenColor` | `#E74C3C` (red) | Hex color for regulators that have been destroyed. Overrides every other color. |
| `Appearance.EnableColoring` | `false` | Tint regulators by status at all. Off by default for a clean release build - the underlying state tracking (and repairing) keeps working correctly either way regardless of this setting; it only ever affects the renderer tint itself. |
| `Behavior.EnableDiagnostics` | `false` | Enables the F9 diagnostics dump and other `[Diag]`-tagged debug logging (connectivity dumps, the GameState change log). Off by default for a clean release build - turn on when troubleshooting. |
| `Behavior.RescanIntervalSeconds` | `2` | How often (seconds) to rescan regulators for new/changed/broken state. |
| `Behavior.RepairKey` | `F6` | Key to hold to repair a Damaged regulator you're looking directly at, from short range. |
| `Behavior.RepairDurationSeconds` | `3` | How long `RepairKey` must be held down, continuously, to repair the regulator. Resets to zero on any interruption - see [Repairing Damaged regulators](#repairing-damaged-regulators). |
| `Behavior.RepairRangeMeters` | `1.5` | Max distance (meters) to the regulator for `RepairKey` to work - matches the game's own default interaction range. |
| `Behavior.RepairMatchRadiusMeters` | `0.75` | How close the raycast hit point must be to a regulator's own mesh to count as "looking at" it (see [Finding what you're looking at](#finding-what-youre-looking-at)). |
| `Behavior.ShowRepairPrompt` | `true` | Show a "Press \<key\> to Repair" prompt on screen while looking directly at a repairable (Damaged) regulator in range. |
| `Shop.RepairPackCostCredits` | `200000` | Price of the "Air Reg Kit" item added to the ship's vending machine (see [Shop](#shop-air-reg-kit)). One is required, and consumed, per repair. |

Edit and reload/relaunch to pick different colors.

## Installation note

This game's `BepInEx/plugins` folder is managed by Vortex on this machine (there's a `__folder_managed_by_vortex` marker). A manually-copied plugin folder can be wiped out by a future Vortex deploy/purge - if that happens, just rebuild (`dotnet build -c Release`) to redeploy it, or package it as a proper Vortex mod.

## Verifying it's working

Check `BepInEx/LogOutput.log` after loading into a shift - you should see lines like:

```
[Info   :Repairable Regulators] Repairable Regulators loaded. Pressurized=#2ECC71, Unpressurized=#3498DB, Damaged=#F1C40F, Broken=#E74C3C.
[Info   :Repairable Regulators] Regulator 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box' is now Unpressurized -> painted 3498DB.
```

Pressurize the room and you should see a follow-up line for the same regulator switching to `Pressurized -> painted 2ECC71`. A regulator that spawns in already non-functional logs `Damaged -> painted F1C40F` on the very first scan. Destroying a working regulator (via damage or a room breach), or tearing one off the wall and wrecking it, logs `Broken -> painted E74C3C` - all four states confirmed working live.

Look directly at a yellow regulator from up close and hold F6 (the default `RepairKey`) for the full `RepairDurationSeconds` (3s by default), and you should see something like:

```
[Info   :Repairable Regulators] [Repair] Started repairing 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box' - hold F6 for 3.0s.
[Info   :Repairable Regulators] [Repair] Closest tracked regulator to the hit point is 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box' (0.02m away), state=Damaged.
[Info   :Repairable Regulators] [Repair] Cloned 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box''s internals to graft onto 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box' - probe='RoomProbe', button=Interaction Volume, animator=Anim_AirlockLights_Root.
[Info   :Repairable Regulators] [Repair] Moved 7 child object(s) from the clone onto 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box'.
[Info   :Repairable Regulators] [Repair] Wired the probe into room entity Entity(15:1) (checked 6 room(s)) - AirPressureState=Unpressurized, AllowAirPressureToggle=True.
[Info   :Repairable Regulators] [Repair] Registered the repaired probe with the ECUController on 'ECU' (4 atmosphere regulator(s) it now tracks) - the ship's air-pressure toggle control will include it.
[Info   :Repairable Regulators] Repaired regulator 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box' - reattached a working, wired-in RoomProbe.
[Info   :Repairable Regulators] Regulator 'SM_AtmosphereRegulator_Mini_1Bx1xA1_Box' is now Unpressurized -> painted 3498DB.
```

The "Started repairing..." line appears the instant you start holding; everything else only once the full hold duration has elapsed (so the "Cloned.../Wired.../Registered..." block appears once, right at the moment of completion, not throughout the hold). If you let go early instead, you'll see `[Repair] F6 released for '<name>' at 1.3s/3.0s - progress reset.` instead - no partial repair, no partial credit kept for next time. If "Cloned..." is missing and it went straight to "Added bare RoomProbe..." instead, no other regulator on the ship was currently working to clone from (see the fallback in [How the repair works](#how-the-repair-works)). If "Wired the probe into room entity..." comes back as a warning instead, no room's bounding volume contains the probe's position; if "Couldn't find this ship's ECUController" shows up, that's the other failure mode - either is worth reporting back with logs for. And see [Known Issues](#known-issues) above for the one confirmed-but-unfixed bug so far.
