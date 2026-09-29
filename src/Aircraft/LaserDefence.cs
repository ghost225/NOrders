using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // A laser aboard one of our flights shoots down missiles coming in at it
    // or at friendlies close by. Nothing fired it before under our own state.
    //
    // A laser can only swing its beam so far off its mount's own axis --
    // front mounted, off the nose -- and fires only once within a degree of
    // its target; every 0.2 s it does blast and fire damage off a
    // damage-at-range curve, scaled by the power it gets, and a missile loses
    // what gets past its armour, divided by its tolerance, from its hit
    // points. So for each missile the time to kill it at its present range
    // is known exactly. The laser takes the most urgent missile it can kill
    // before that missile arrives, inside its cone and in clear line, and
    // stays on it while it still can -- no beam time spent on a shot it
    // cannot finish, and no flicking between targets. It does not turn the
    // aircraft to bring the laser to bear: that is the evasion's business.
    internal static class LaserDefence
    {
        private static readonly FieldInfo MaxAngle = AccessTools.Field(typeof(Laser), "maxAngle");
        private static readonly FieldInfo DamageAtRange = AccessTools.Field(typeof(Laser), "damageAtRange");
        private static readonly FieldInfo BlastDamage = AccessTools.Field(typeof(Laser), "blastDamage");
        private static readonly FieldInfo FireDamage = AccessTools.Field(typeof(Laser), "fireDamage");
        private static readonly FieldInfo Direction = AccessTools.Field(typeof(Laser), "directionTransform");
        private static readonly FieldInfo Hitpoints = AccessTools.Field(typeof(Missile), "hitpoints");

        private const float GuardRadius = 3000f;       // friendlies this close are covered too
        private const float PowerAllowance = 0.85f;    // the pods draw on the same supply
        private const float SlewAllowance = 0.4f;      // seconds to swing on and warm up

        private sealed class Shot
        {
            internal Missile Target;
            internal WeaponStation Station;
            internal Aircraft Owner;
        }

        private static readonly Dictionary<Laser, Shot> shots = new Dictionary<Laser, Shot>();
        private static float nextChoice;
        [ThreadStatic] internal static bool Aiming;

        internal static bool Busy(Laser laser) =>
            laser != null && shots.TryGetValue(laser, out Shot shot) && shot.Target != null && !shot.Target.disabled;

        internal static void Tick()
        {
            if (Time.timeSinceLevelLoad >= nextChoice)
            {
                nextChoice = Time.timeSinceLevelLoad + 0.2f;
                Choose();
            }
            // The beam goes out unless fired again within 0.2 s.
            foreach (KeyValuePair<Laser, Shot> entry in shots)
            {
                Laser laser = entry.Key;
                Shot shot = entry.Value;
                if (laser == null || shot.Target == null || shot.Target.disabled || shot.Owner == null || shot.Owner.disabled) continue;
                Aiming = true;
                try
                {
                    laser.SetTarget(shot.Target);
                    laser.Fire(shot.Owner, shot.Target, shot.Owner.rb != null ? shot.Owner.rb.velocity : Vector3.zero, shot.Station, default(GlobalPosition));
                }
                finally { Aiming = false; }
            }
        }

        private static void Choose()
        {
            var previous = new Dictionary<Laser, Missile>();
            foreach (KeyValuePair<Laser, Shot> entry in shots) previous[entry.Key] = entry.Value.Target;
            shots.Clear();

            foreach (Flight flight in FlightOrders.All())
            {
                Aircraft aircraft = flight.Aircraft;
                if (aircraft == null || aircraft.disabled || Host.IsFlownByPlayer(flight) || aircraft.weaponStations == null) continue;
                var taken = new HashSet<Missile>();
                foreach (WeaponStation station in aircraft.weaponStations)
                {
                    if (station?.Weapons == null) continue;
                    foreach (Weapon weapon in station.Weapons)
                    {
                        if (!(weapon is Laser laser) || laser == null) continue;
                        previous.TryGetValue(laser, out Missile held);
                        Missile best = Pick(aircraft, laser, held, taken);
                        if (best == null) continue;
                        taken.Add(best);
                        shots[laser] = new Shot { Target = best, Station = station, Owner = aircraft };
                        if (best != held)
                            Tracing.Flight("[flight] " + flight.Name + " · laser on an inbound " + best.GetSeekerType() + " missile at " +
                                (Vector3.Distance(laser.transform.position, best.transform.position) / 1000f).ToString("0.0") + " km");
                    }
                }
            }
        }

        // The most urgent missile this laser can kill before it arrives; the
        // one it already has, while that is still true.
        private static Missile Pick(Aircraft aircraft, Laser laser, Missile held, HashSet<Missile> taken)
        {
            FactionHQ hq = aircraft.NetworkHQ;
            Missile best = null;
            float soonest = float.MaxValue;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile missile) || missile.disabled || missile.NetworkHQ == null || missile.NetworkHQ == hq) continue;
                if (taken.Contains(missile) && missile != held) continue;
                if (!UnitRegistry.TryGetUnit(missile.targetID, out Unit aimedAt) || aimedAt == null || aimedAt.NetworkHQ != hq) continue;
                if (aimedAt != aircraft && Vector3.Distance(aimedAt.transform.position, aircraft.transform.position) > GuardRadius) continue;
                if (!KillTime(laser, missile, out float kill)) continue;
                float arrives = TimeToImpact(missile, aimedAt);
                if (kill + SlewAllowance > arrives) continue;
                if (missile == held) return held;                  // still killable: stay on it
                if (arrives < soonest) { soonest = arrives; best = missile; }
            }
            return best;
        }

        // Seconds for this laser to kill this missile from where it is now, if
        // it can: inside the swing cone, in clear line, and doing damage past
        // the missile's armour at this range.
        private static bool KillTime(Laser laser, Missile missile, out float seconds)
        {
            seconds = float.MaxValue;
            Transform mount = Direction?.GetValue(laser) as Transform ?? laser.transform;
            Vector3 to = missile.transform.position - mount.position;
            float cone = MaxAngle?.GetValue(laser) is float angle ? angle : 0f;
            if (Vector3.Angle(laser.transform.forward, to) > cone - 1f) return false;
            if (Physics.Linecast(mount.position, missile.transform.position, PhysicsLayers.StaticsMask)) return false;
            if (!(DamageAtRange?.GetValue(laser) is AnimationCurve curve)) return false;

            float scale = curve.Evaluate(to.magnitude) * PowerAllowance;
            float blast = (BlastDamage?.GetValue(laser) is float b ? b : 0f) * scale * 0.2f;
            float fire = (FireDamage?.GetValue(laser) is float f ? f : 0f) * scale * 0.2f;
            ArmorProperties armour = missile.GetArmorProperties();
            if (armour == null) return false;
            float perTick = 0f;
            if (blast > armour.blastArmor) perTick += (blast - armour.blastArmor) / Mathf.Max(armour.blastTolerance, 0.1f);
            if (fire > armour.fireArmor) perTick += (fire - armour.fireArmor) / Mathf.Max(armour.fireTolerance, 0.1f);
            if (perTick <= 0f) return false;
            float left = Hitpoints?.GetValue(missile) is float hp ? hp : 1f;
            seconds = Mathf.Ceil(Mathf.Max(left, 0.01f) / perTick) * 0.2f;
            return true;
        }

        private static float TimeToImpact(Missile missile, Unit target)
        {
            Vector3 gap = target.transform.position - missile.transform.position;
            Vector3 relative = (missile.rb != null ? missile.rb.velocity : Vector3.zero) - (target.rb != null ? target.rb.velocity : Vector3.zero);
            float closing = Vector3.Dot(relative, gap.normalized);
            return closing > 1f ? gap.magnitude / closing : float.MaxValue;
        }
    }

    // A laser on a missile stays on it until we move it.
    [HarmonyPatch(typeof(Laser), nameof(Laser.SetTarget))]
    internal static class KeepLaserOnMissilePatch
    {
        private const string Name = "Laser defence";

        private static bool Prefix(Laser __instance)
        {
            if (LaserDefence.Aiming || !Guard.Ok(Name)) return true;
            try { return !LaserDefence.Busy(__instance); }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
