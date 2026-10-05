using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    public enum EngagementMode { WeaponsFree, WeaponsTight, WeaponsHold }

    // Rules of engagement for automatic fire.
    //
    // Vanilla ships carry no FireControl component -- ShipAI picks the ship's
    // target and each Turret picks its own -- so there is no single native
    // decision to patch. Instead a turret whose current pick is not sanctioned
    // is denied it: cleared and held manual until the mode allows it again.
    public static class EngagementPolicy
    {
        public static EngagementMode GetMode(Ship ship) =>
            ship != null ? ShipEngagement.Ensure(ship).Mode : EngagementMode.WeaponsFree;

        public static bool SetMode(Ship ship, EngagementMode mode, out string reason)
        {
            if (!CommandableShip.CanCommand(ship, out reason)) return false;
            ShipEngagement.Ensure(ship).Mode = mode;
            reason = mode == EngagementMode.WeaponsFree ? "Weapons free · automatic engagement unrestricted"
                : mode == EngagementMode.WeaponsTight ? "Weapons tight · inbound weapons and units that have fired on us only"
                : "Weapons hold · point defence against inbound weapons only";
            return true;
        }

        // A weapon's own rules, over the ship's: null follows the ship.
        public static EngagementMode? GetWeaponMode(Ship ship, string key)
        {
            var state = ship != null ? ship.GetComponent<ShipEngagement>() : null;
            return state != null && key != null && state.PerWeapon.TryGetValue(key, out EngagementMode mode) ? mode : (EngagementMode?)null;
        }

        public static bool SetWeaponMode(Ship ship, string key, EngagementMode? mode, out string reason)
        {
            if (!CommandableShip.CanCommand(ship, out reason)) return false;
            if (key == null) { reason = "No such weapon."; return false; }
            var state = ShipEngagement.Ensure(ship);
            if (mode.HasValue) state.PerWeapon[key] = mode.Value;
            else state.PerWeapon.Remove(key);
            reason = mode.HasValue ? Describe(mode.Value) : "Follows the ship's rules · " + Describe(state.Mode);
            return true;
        }

        // May this shot leave the ship? Anything we did not order, from a ship
        // under command, has to satisfy the rules of engagement -- this
        // weapon's own, if it has them, or the ship's.
        internal static bool Allows(Unit owner, Unit target, Weapon weapon = null, WeaponStation station = null)
        {
            if (ShipWeapons.Firing) return true;              // our own explicit order
            // An aircraft's turret: its flight's rules (TurretRules).
            if (owner is Aircraft aircraft) return TurretRules.Allows(aircraft, target, station);
            if (!(owner is Ship ship)) return true;
            var state = ship.GetComponent<ShipEngagement>();
            if (state == null) return true;
            EngagementMode mode = state.ModeFor(weapon != null ? WeaponOrders.KeyOf(weapon.info) : null);
            if (mode == EngagementMode.WeaponsFree) return true;
            return state.Permits(target, mode);
        }

        // A mount finishing an explicit order returns to whatever the standing
        // rules say, rather than to autonomous fire.
        internal static void HandBack(Ship ship, Turret turret)
        {
            if (turret == null) return;
            // Clear the target it was just given in every mode, or it goes on
            // firing at it the moment it is no longer held: an order for a
            // single round, under weapons free, emptied the launcher at the
            // same target.
            if (NativeBindings.TurretChooseTarget != null && turret.GetTarget() != null)
                NativeBindings.TurretChooseTarget.Invoke(turret, new object[] { true });
            var state = ship != null ? ship.GetComponent<ShipEngagement>() : null;
            if (state == null || state.ModeFor(ShipEngagement.KeyOf(turret)) == EngagementMode.WeaponsFree)
            {
                turret.SetManual(false);
                return;
            }
            state.Hold(turret);
        }

        public static string Describe(EngagementMode mode) =>
            mode == EngagementMode.WeaponsFree ? "Weapons Free"
            : mode == EngagementMode.WeaponsTight ? "Weapons Tight" : "Weapons Hold";
    }

    // Denial at the weapon rather than at the turret.
    //
    // Holding a turret manual stops the base Turret firing on its own, but it
    // is not the only way a shot can leave a ship: a turret subclass or a
    // modded mount can drive its weapon by another path, and then weapons tight
    // and weapons hold are quietly advisory. Every shot goes through
    // Weapon.Fire, so the rules are enforced there instead.
    //
    // Fire is virtual and subclasses override it, so patching the base type
    // alone would miss exactly the weapons that need catching -- each declared
    // override is targeted individually.
    [HarmonyPatch]
    internal static class WeaponReleasePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var signature = new[]
            {
                typeof(Unit), typeof(Unit), typeof(Vector3), typeof(WeaponStation), typeof(GlobalPosition)
            };
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public |
                                       BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = assembly.GetTypes(); }
                catch { continue; }
                foreach (Type type in types)
                {
                    if (!typeof(Weapon).IsAssignableFrom(type)) continue;
                    MethodInfo fire;
                    try { fire = type.GetMethod("Fire", flags, null, signature, null); }
                    catch { continue; }
                    if (fire != null && !fire.IsAbstract) yield return fire;
                }
            }
        }

        // Positional, not by name: overrides rename the parameters -- Gun.Fire
        // calls the first one firingUnit, not owner -- and Harmony binds
        // prefix arguments by name, so naming them fails on exactly the
        // subclasses this patch exists to catch.
        private static bool Prefix(Weapon __instance, Unit __0, Unit __1, WeaponStation __3)
        {
            if (!Guard.Ok(Name)) return true;                 // never hold fire on a broken rule
            try { return NuclearRelease.Allows(__instance, __0) && EngagementPolicy.Allows(__0, __1, __instance, __3); }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }

        private const string Name = "Weapon release rules";
    }

    internal sealed class ShipEngagement : MonoBehaviour
    {
        private Ship ship;
        private float nextSweep, nextThreatScan;
        private readonly HashSet<uint> attackers = new HashSet<uint>();
        private readonly List<Unit> inbound = new List<Unit>();

        // Turrets this policy holds on manual, and only those are released.
        // A turret asleep with nothing to do is woken by any SetManual call:
        // releasing every turret on the ship each second (and four times a
        // second under Tight or Hold) kept every mount of every engaged ship
        // awake and searching -- costly in a big fleet action.
        private readonly HashSet<Turret> held = new HashSet<Turret>();
        private Turret[] turrets;
        private float turretsAt = -100f;

        internal void Hold(Turret turret)
        {
            turret.SetManual(true);
            held.Add(turret);
        }

        private void Free(Turret turret)
        {
            if (held.Remove(turret)) turret.SetManual(false);
        }

        private Turret[] Turrets()
        {
            if (turrets == null || Time.timeSinceLevelLoad - turretsAt > 10f)
            {
                turrets = ship.GetComponentsInChildren<Turret>(true);
                turretsAt = Time.timeSinceLevelLoad;
            }
            return turrets;
        }

        internal EngagementMode Mode = EngagementMode.WeaponsFree;
        internal readonly Dictionary<string, EngagementMode> PerWeapon = new Dictionary<string, EngagementMode>();

        internal EngagementMode ModeFor(string key) =>
            key != null && PerWeapon.TryGetValue(key, out EngagementMode mode) ? mode : Mode;

        internal static string KeyOf(Turret turret) => WeaponOrders.KeyOf(turret?.GetWeapon()?.info);

        // Anything held back at all -- the ship, or any one weapon.
        private bool Restricted()
        {
            if (Mode != EngagementMode.WeaponsFree) return true;
            foreach (EngagementMode mode in PerWeapon.Values) if (mode != EngagementMode.WeaponsFree) return true;
            return false;
        }

        internal static ShipEngagement Ensure(Ship ship)
        {
            Ownership.Claim(ship);
            var state = ship.GetComponent<ShipEngagement>();
            if (state == null)
            {
                state = ship.gameObject.AddComponent<ShipEngagement>();
                state.ship = ship;
            }
            return state;
        }

        private void Update()
        {
            if (ship == null || ship.disabled || !ship.IsServer || !ship.LocalSim) return;
            if (!Restricted())
            {
                // Nothing to deny; make sure nothing stays held from a stricter mode.
                if (Time.timeSinceLevelLoad >= nextSweep) { nextSweep = Time.timeSinceLevelLoad + 1f; ReleaseAll(); }
                return;
            }
            if (Time.timeSinceLevelLoad >= nextThreatScan) { nextThreatScan = Time.timeSinceLevelLoad + .5f; ScanThreats(); }
            if (Time.timeSinceLevelLoad < nextSweep) return;
            nextSweep = Time.timeSinceLevelLoad + .25f;
            Sweep();
        }

        private void ScanThreats()
        {
            inbound.Clear();
            attackers.Clear();
            foreach (Missile missile in MissileIndex.All)
            {
                if (missile == null || missile.disabled) continue;
                if (missile.NetworkHQ == null || missile.NetworkHQ == ship.NetworkHQ) continue;
                if (missile.targetID != ship.persistentID) continue;
                inbound.Add(missile);
                if (missile.owner != null) attackers.Add(missile.owner.persistentID.Id);
            }
        }

        // The same rules the turret sweep applies, asked of a single shot.
        internal bool Permits(Unit target, EngagementMode mode)
        {
            if (mode == EngagementMode.WeaponsFree) return true;
            if (target == null) return false;
            if (target is Missile) return inbound.Contains(target);
            if (mode == EngagementMode.WeaponsHold) return false;
            return attackers.Contains(target.persistentID.Id);
        }

        private bool Sanctioned(Turret turret, Unit target)
        {
            EngagementMode mode = ModeFor(KeyOf(turret));
            if (mode == EngagementMode.WeaponsFree) return true;     // this weapon is free
            if (target == null) return true;                         // Idle mounts are fine.
            if (target is Missile) return inbound.Contains(target);  // Only actual inbounds.
            if (mode == EngagementMode.WeaponsHold) return false;    // Hold permits nothing else.
            return attackers.Contains(target.persistentID.Id);       // Tight: only those who shot at us.
        }

        private void Sweep()
        {
            foreach (Turret turret in Turrets())
            {
                if (turret == null || turret.GetAttachedUnit() != ship) continue;
                // A mount carrying one of our explicit orders is not the policy's business.
                if (ShipWeapons.Holds(ship, turret)) continue;
                Unit target = turret.GetTarget();
                if (Sanctioned(turret, target)) { Free(turret); continue; }
                if (held.Contains(turret)) continue;           // already held; no need to wake it again
                if (NativeBindings.TurretChooseTarget != null)
                    NativeBindings.TurretChooseTarget.Invoke(turret, new object[] { true });
                Hold(turret);
            }
        }

        private void ReleaseAll()
        {
            if (held.Count == 0) return;
            foreach (Turret turret in new List<Turret>(held))
            {
                if (turret == null) { held.Remove(turret); continue; }
                if (ShipWeapons.Holds(ship, turret)) continue;
                Free(turret);
            }
        }
    }
}
