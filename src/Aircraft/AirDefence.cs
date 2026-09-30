using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // What the faction knows of enemy air defence and forces along a leg.
    //
    // A unit's anti-air reach is the longest reach among its weapons the game
    // rates as anti-air; the leg is threatened when any known enemy unit that
    // is not itself an aircraft or a missile has that reach, plus a margin,
    // over any point of it -- or when the leg passes close over enemy ground
    // or naval forces at all, armed with anti-air or not.
    internal static class AirDefence
    {
        private static readonly Dictionary<UnitDefinition, float> reachOf = new Dictionary<UnitDefinition, float>();
        private const float Margin = 1000f, OverForces = 2000f;

        internal static float Reach(Unit unit)
        {
            if (unit == null) return 0f;
            UnitDefinition key = unit.definition;
            if (key != null && reachOf.TryGetValue(key, out float known)) return known;
            float reach = 0f;
            if (unit.weaponStations != null)
                foreach (WeaponStation station in unit.weaponStations)
                    if (station?.WeaponInfo != null && station.WeaponInfo.effectiveness.antiAir > 0.05f)
                        reach = Mathf.Max(reach, station.WeaponInfo.targetRequirements.maxRange);
            if (key != null) reachOf[key] = reach;
            return reach;
        }

        internal static bool Threatens(Aircraft aircraft, GlobalPosition from, GlobalPosition to, out string what)
        {
            what = null;
            FactionHQ hq = aircraft != null ? aircraft.NetworkHQ : null;
            if (hq == null) return false;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (unit == null || unit.disabled || unit.NetworkHQ == null || unit.NetworkHQ == hq) continue;
                if (unit is Aircraft || unit is Missile) continue;
                if (!hq.TryGetKnownPosition(unit, out GlobalPosition at)) continue;
                float off = OffLeg(at, from, to);
                float reach = Reach(unit);
                if (reach > 0f && off < reach + Margin)
                {
                    what = (unit.definition?.unitName ?? unit.name) + " air defence";
                    return true;
                }
                if ((unit is GroundVehicle || unit is Ship) && off < OverForces)
                {
                    what = "enemy forces under the way in";
                    return true;
                }
            }
            return false;
        }

        // Horizontal distance from a point to the leg.
        private static float OffLeg(GlobalPosition p, GlobalPosition a, GlobalPosition b)
        {
            Vector3 ab = b - a; ab.y = 0f;
            Vector3 ap = p - a; ap.y = 0f;
            float t = ab.sqrMagnitude > 1f ? Mathf.Clamp01(Vector3.Dot(ap, ab) / ab.sqrMagnitude) : 0f;
            return (ap - ab * t).magnitude;
        }
    }
}
