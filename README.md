# NOrders

Shared execution code for Nuclear Option mods: flight orders and wings, the
pilot state, strikes, escorts, ship navigation and task forces, carrier
launches, supply runs, amphibious operations. Compiled into each mod that
uses it (Naval Power, High Command) as a git submodule at `norders/`; no DLL
of its own, no csproj. An SDK-style project whose folder contains the
submodule already compiles `norders/src/**/*.cs` by default; don't add it
again explicitly, or the files are compiled twice.

Private for now.

## Wiring a mod

At startup, before anything else from NOrders runs:

- **`Host`**: set `ModId`, the three log delegates, `Say` (an on-screen
  message, if the mod has somewhere to show one), `CommandedShip` /
  `CommandedBase` (what the player commands in this mod, or null), and
  `IsFlownByPlayer`.
- **`Tuning`**: plain values with defaults. A mod with a settings file copies
  its entries in at startup and again when one changes.
- **Harmony**: apply the `[HarmonyPatch]` classes in your own assembly; that
  includes every NOrders patch.

## Ownership: two mods at once

Each DLL carries its own copy of every class and patch, so with two mods
installed every shared patch runs twice, and each copy has its own statics.
The rules that make that safe:

1. **Claim before a unit enters a registry.** A flight is claimed when
   adopted, a ship when commanded, routed, given weapon or engagement orders,
   or added to a task force, and a landing craft when launched
   (`Ownership.Claim`, which returns false if another mod owns the unit, and
   then the unit is not taken on). A claim is a disabled marker child on the
   unit named `__NOrders.Owner:<ModId>`, so it is visible across assemblies.
2. **Patches act only on their own registry's units.** `FlightOrders.Of`,
   `ShipRoute`/`ShipWeapons`/`ShipEngagement` components, task force
   membership, `Amphib.Managed`: with rule 1, membership means ownership.
   New patches must keep to this.
3. **Fixes to the game's own behaviour for every unit** (today only the
   carrier approach speed) use `Ownership.Acts(unit)`: the owner acts, or
   the **steward** (the first NOrders mod to ask this session) for units no
   mod owns, so nothing is applied twice.
4. **Release when a unit leaves your control** (`CommandableShip.ReleaseIfIdle`
   for ships). A destroyed unit takes its marker with it.
