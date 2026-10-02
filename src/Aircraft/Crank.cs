using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // The crank: the air-to-air answer to egress. After a radar missile is
    // away, the shooter turns until its targets sit at the edge of its own
    // radar's cone and eases down. It keeps them tracked for the missiles
    // still flying on its picture -- a semi-active round all the way in, an
    // active one until its own seeker has the target ("pitbull") -- while it
    // closes far more slowly and leaves the enemy's shot a longer, uphill
    // flight. With nothing left to guide it turns cold (the ordinary egress)
    // and then picks its task up again.
    internal static class Crank
    {
        internal const float MaxSeconds = 90f;       // a missile is in or gone by then
        internal const float ColdSeconds = 30f;       // the egress that follows, against an air target
        internal const float Descent = 3000f;         // down by up to this much
        internal const float FloorAboveGround = 1500f;
        internal const float MaxDiveDegrees = 7f;
        private const float Margin = 8f;              // short of the cone edge, for the turn's wander
        private const float DefaultCone = 60f;

        private static readonly FieldInfo RadarCone = AccessTools.Field(typeof(Radar), "radarCone");

        // This aircraft's missiles still flying on its radar, and what they
        // are after.
        internal static int Supported(Aircraft aircraft, List<Unit> targets)
        {
            targets?.Clear();
            if (aircraft == null) return 0;
            int count = 0;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile missile) || missile.disabled || missile.owner != aircraft) continue;
                string seeker;
                try { seeker = missile.GetSeekerType(); } catch { continue; }
                bool needs = seeker == "SARH" || (seeker == "ARH" && missile.seekerMode != Missile.SeekerMode.activeLock);
                if (!needs) continue;
                if (!UnitRegistry.TryGetUnit(missile.targetID, out Unit target) || target == null || target.disabled) continue;
                count++;
                if (targets != null && !targets.Contains(target)) targets.Add(target);
            }
            return count;
        }

        // A round that flies on the launcher's radar for at least part of
        // the way: semi-active, or active with a datalinked midcourse.
        internal static bool RadarGuided(WeaponInfo info)
        {
            if (info?.weaponPrefab == null) return false;
            MissileSeeker seeker = info.weaponPrefab.GetComponentInChildren<MissileSeeker>(true);
            string kind;
            try { kind = seeker != null ? seeker.GetSeekerType() : null; } catch { return false; }
            return kind == "SARH" || kind == "ARH";
        }

        // Half-angle of the radar's cone, less a margin. Radars without one
        // (or none at all) get a fighter's sixty degrees.
        internal static float Cone(Aircraft aircraft)
        {
            float cone = 0f;
            if (aircraft?.radar != null && RadarCone?.GetValue(aircraft.radar) is float read) cone = read;
            if (cone <= 0f) cone = DefaultCone;
            return Mathf.Clamp(cone, 20f, 80f) - Margin;
        }
    }
}
