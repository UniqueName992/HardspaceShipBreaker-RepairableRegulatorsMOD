# Changelog

All notable changes to this project are documented here.

## [1.1.0] - 2026-10-03

### Added

- Air Reg Kits now persist across a game restart - the owned count is saved to the config file instead of being a plain in-memory counter.
- Free Play and Cutter's Race now get a flat 100 Air Reg Kits, independent of your real Career stock - repairing there no longer drains (or inflates) the count you'll see back in Career.
- Abandoning a Career/WeeklyShip shift (quitting to the front end before reaching the Hab) now reverts any Air Reg Kits bought or spent during that shift, rather than letting them stick.

### Changed

- The BepInEx plugin GUID (and therefore the generated config filename) no longer contains a personal identifier.
- Compiled build output no longer embeds the local build machine's disk path in its debug info.

## [1.0.0] - 2026-10-02

First release.

### Added

- Repair a Damaged atmosphere regulator back to full working order by holding **F6** while looking at it - button, animation, room pressurization, and the ship's manual toggle control all included, not just a cosmetic fix.
- Hold-to-repair progress bar and on-screen interaction prompt, styled to match the game's own HUD.
- "Air Reg Kit" vendor item (200,000 credits by default) - repairing requires owning one and consumes it on success, with a persistent on-screen counter.
- Optional regulator status coloring (green/blue/yellow/red, by pressurization/damage/destruction), off by default.
- Full documentation: a frontpage README plus a technical deep-dive and roadmap under `docs/`.
