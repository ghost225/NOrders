using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // One target, so many missiles: the native combat pilot fires whenever
    // its weapon is in range and its target is held, with no regard for the
    // missiles already on their way. A wing emptied eight racks at one
    // helicopter; a Vagrant and an F-16M went "out of ammo" on one target
    // each. So a missile launch by an AI aircraft under our orders is
    // withheld while the target already has as many of our side's missiles
    // closing on it as the weapon says it needs (two at most for an
    // aircraft), and never goes at a wreck. Guns, slings and bombs are left
    // alone; the player's own aircraft is never touched.
    [HarmonyPatch(typeof(WeaponManager), nameof(WeaponManager.Fire))]
    internal static class ShotDisciplinePatch
    {
        private const string Name = "Shot discipline";
        private static readonly Dictionary<(Aircraft, Unit), float> said = new Dictionary<(Aircraft, Unit), float>();
        private static readonly System.Reflection.FieldInfo AircraftOf = AccessTools.Field(typeof(WeaponManager), "aircraft");
        internal static int OffNose;

        private static bool Prefix(WeaponManager __instance)
        {
            if (!Guard.Ok(Name)) return true;
            try
            {
                if (!(AircraftOf?.GetValue(__instance) is Aircraft aircraft)) return true;
                if (aircraft.Player != null || !aircraft.IsServer) return true;
                // Every AI aircraft of a commanded faction, ours or the game's
                // own: a native Revoker put eight Scimitars into one Cricket.
                Flight flight = FlightOrders.Of(aircraft);
                if (flight != null && Host.IsFlownByPlayer(flight)) return true;
                if (flight == null && !Host.CommandsFaction(aircraft.NetworkHQ)) return true;
                WeaponStation station = __instance.currentWeaponStation;
                WeaponInfo info = station?.WeaponInfo;
                if (info == null || !info.missile || info.gun || info.sling || info.cargo || info.troops) return true;
                List<Unit> targets = __instance.GetTargetList();
                Unit target = targets != null && targets.Count > 0 ? targets[0] : null;
                if (target == null) return true;
                if (Host.Dead(target)) return false;
                // Off the nose: the launch waits until the aircraft has come
                // round. The weapon's own alignment limit is kept when tighter.
                float limit = Mathf.Min(Tuning.MaxLaunchAngle, info.targetRequirements.minAlignment > 0f ? info.targetRequirements.minAlignment : 180f);
                Vector3 toTarget = target.transform.position - aircraft.transform.position;
                float angle = toTarget.sqrMagnitude > 1f ? Vector3.Angle(aircraft.transform.forward, toTarget) : 0f;
                if (angle > limit)
                {
                    float at = Time.timeSinceLevelLoad;
                    if (!said.TryGetValue((aircraft, target), out float lastAngle) || at - lastAngle > 20f)
                    {
                        said[(aircraft, target)] = at;
                        Tracing.Flight("[flight] " + (flight?.Name ?? aircraft.definition?.unitName ?? aircraft.name) + " · holding fire · " + ShipNames.Of(target) + " is " + angle.ToString("0") + "° off the nose (limit " + limit.ToString("0") + "°)");
                    }
                    OffNose++;
                    return false;
                }
                int allowed = target is Aircraft ? 2 : Mathf.Clamp(Mathf.CeilToInt(info.CalcAttacksNeeded(target)), 1, 4);
                int live = Closing(aircraft.NetworkHQ, target);
                if (live < allowed)
                {
                    // A ripple weapon fires the whole list in one salvo, and
                    // the combat AI lists the target once per attack it wants:
                    // a Scimitar rack went at one Cricket. The list is cut to
                    // what is still allowed.
                    int room = allowed - live, kept = 0;
                    for (int i = 0; i < targets.Count;)
                    {
                        if (targets[i] == target && ++kept > room) targets.RemoveAt(i);
                        else i++;
                    }
                    return true;
                }
                float now = Time.timeSinceLevelLoad;
                if (!said.TryGetValue((aircraft, target), out float last) || now - last > 20f)
                {
                    said[(aircraft, target)] = now;
                    Tracing.Flight("[flight] " + (flight?.Name ?? aircraft.definition?.unitName ?? aircraft.name) + " · holding fire · " + live + " missile(s) already closing on " + ShipNames.Of(target));
                }
                return false;
            }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }

        // Our side's missiles still on their way to this target: any live
        // missile of ours with the target's id that is not flying away from
        // it. A shot launched at 35 km is a shot spent; counting only the
        // last 15 km let nine native pilots empty their racks in one run.
        private static readonly Dictionary<Missile, float> firstSeen = new Dictionary<Missile, float>();
        private const float CountsFor = 120f;    // a missile two minutes out is not arriving

        internal static int Closing(FactionHQ hq, Unit target)
        {
            int live = 0;
            if (hq == null || target == null) return 0;
            float now = Time.timeSinceLevelLoad;
            if (firstSeen.Count > 500) firstSeen.Clear();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile missile) || missile.disabled || missile.NetworkHQ != hq || missile.targetID != target.persistentID) continue;
                if (!firstSeen.TryGetValue(missile, out float seen)) firstSeen[missile] = seen = now;
                if (now - seen > CountsFor) continue;
                Vector3 toTarget = target.GlobalPosition() - missile.GlobalPosition();
                float range = toTarget.magnitude;
                float closing = missile.rb != null ? Vector3.Dot(missile.rb.velocity - (target.rb != null ? target.rb.velocity : Vector3.zero), toTarget / Mathf.Max(range, 1f)) : 1f;
                if (closing < 0f) continue;                                  // past it or lost it
                live++;
            }
            return live;
        }
    }
}
