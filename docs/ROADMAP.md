# Roadmap

Future ideas for this plugin, captured so they don't get lost - not commitments, not started, no particular order beyond the list below. Each one is a genuinely separate feature with its own open design questions; none of them should be tackled as a single combined effort.

## 1. Repair takes time (Done)

Repairing now requires holding `RepairKey` for `Behavior.RepairDurationSeconds` (3s by default), with progress resetting instantly on any interruption - looking away, moving out of range, switching target, or releasing the key early. The on-screen prompt's own real estate ended up being exactly where the progress feedback lives: the verb switches to "Repairing" and an amber bar grows underneath the panel while held. See [Repairing Damaged regulators](TECHNICAL.md#repairing-damaged-regulators) for the details.

## 2. Repair packs, purchased from a vendor (expensive) (Done, aside from persistence)

Repairing should consume a consumable item - a "repair pack" - bought from an in-game vendor at a meaningful cost, rather than being free and unlimited.

**Done:** the whole loop. `VendingMachineUIController`/`VendingMachineItemAsset`/`VendingMachinePurchaseBase` are decompiled and understood well enough that the plugin adds a real "Air Reg Kit" item (200,000 credits by default) to the ship's one shop, going through the game's own unmodified purchase flow - confirmed live, listed correctly alongside the real items at the same size and style, with a working description and a borrowed 3D preview. Buying one increments a counter (shown on screen via a persistent "Air Reg Kits: N" readout); `RepairKey`'s hold-to-repair timer now refuses to even start without owning at least one (the prompt shows "Requires Air Reg Kit" in a dimmed/disabled style instead of the normal green-lit prompt), and successfully completing a repair consumes exactly one.

**Still open:**

- The count is a plain in-memory counter (`Plugin._repairPackCount`) - nothing persists it across sessions/shifts yet. There's no real inventory item behind this (deliberately, to keep this step scoped), so there's currently nothing durable to hang that state off if persistence across restarts ever matters.

## 2a. Merge with #4/#5 as tool ammo

Original framing for #4 (remote control) and #5 (compartment match) was separate cheap/expensive gadgets; the idea since discussed is a single "Regulator Control Tool" with two ammo tiers instead - cheap ammo for remote toggling, expensive ammo for the compartment-match effect. If pursued, the repair pack work above (and whatever "does the player have N of this consumable" mechanism it lands on) would likely want to be shared with however that tool's ammo is tracked, rather than building two separate consumable systems.

## 3. Repair skill, unlockable and upgradeable via Lynx tokens

Gate repairing behind a skill unlock in the game's own progression system (paid for with "Lynx" tokens, matching the game's existing skill-tree currency), with further paid upgrades on top (faster repairs? cheaper packs? no pack needed at higher ranks? - undecided). Needs:

- Decompiling however the game's own skill tree / progression system is structured, and whether a plugin can register a new skill node into it at all, or whether this has to be simulated some other way (e.g. a plugin-only "unlocked" flag gated behind spending tokens via some other mechanism).
- Deciding the upgrade path/tiers once the mechanism is understood.

Likely the most involved item on this list - probably depends on figuring out #2's economy hooks first anyway.

## 4. Remote control for regulators (cheap)

A cheap tool/gadget that lets the player toggle a regulator's air pressure remotely, without walking up and pressing its button directly - basically a personal, portable version of what the ship's ECU already does for the whole ship, but usable on a single regulator from a distance. Smaller in scope than #2/#3 - mainly needs a targeting/selection mechanic (which regulator does "remote control" currently point at?) and reuses `RoomProbe.TryToggleAirPressure()`, already used elsewhere in this codebase.

## 5. "Match the neighboring compartment" tool (super expensive)

A tool that, when used on a wall, forces that compartment to match whatever state a neighboring compartment is in. Least well-specified idea on this list - needs nailing down before design even starts:

- "Match" in what sense - pressurization state specifically, or something broader (sealed/breached status, room type)?
- What counts as "the wall" and "the neighboring compartment" - is this raycast-based like the repair prompt, or does it need real adjacency/room-graph data (`RoomComponent`/`RoomGroup` structures already touched by this plugin)?
- Positioned as the most expensive/powerful item on the list, implying it should feel like a significant, rare tool rather than routine equipment - pricing/gating aside from the mechanic itself.

## 6. Proper controller support for RepairKey

Right now `Behavior.RepairKey` is a raw `KeyCode` read via Unity's legacy `Input.GetKey` - works fine for keyboard, and should technically work for a gamepad too if bound to one of Unity's generic `KeyCode.JoystickButtonX` codes (that polling is independent of the game's own controller input library, InControl, so the two don't conflict). The catch: those codes aren't labeled by controller type - Unity doesn't know "JoystickButton0 means Xbox A" vs PlayStation's Cross, so a player has to experiment to find the right one for their specific pad, and it only covers digital buttons, not triggers/sticks.

Doing this properly would mean integrating with the game's own InControl-based action system instead (the same one `GameplayActions`/`FrontEndActions`/`ContextualButtonPrompt` all use) - so Repair shows up as a real rebindable control with the correct icon per platform, like every other action in the game. Not investigated yet; likely its own decompiling pass into however InControl action sets get registered/extended.

## Suggested order, if asked

~~1 (timer) is nearly free to build on top of what exists.~~ Done. 4 (remote control) is next smallest and mostly self-contained. 2 (repair packs) and 3 (skill unlock) both need the same kind of economy/progression-system investigation, so probably tackled together once that investigation happens. 5 (compartment match) needs its own design pass before any code, regardless of order.
