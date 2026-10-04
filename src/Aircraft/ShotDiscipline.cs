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
                // Every launched store -- missiles and unguided rockets alike; not
                // guns, bombs, cargo or pods. Rockets are not flagged "missile"
                // and slipped past the cone (a Sledge put rockets out well off
                // its nose).
                if (info == null || info.gun || info.bomb || info.glideBomb || info.sling || info.cargo || info.troops || info.jammer || info.rearmGround || info.rearmShip) return true;
                List<Unit> targets = __instance.GetTargetList();
                Unit target = targets != null && targets.Count > 0 ? targets[0] : null;
                if (target == null) return true;
                if (Host.Dead(target)) return false;
                // Off the nose: the launch waits until the aircraft has come
                // round. The cone is the seeker's -- an optical or laser
                // seeker must see the target at launch, a heat-seeker nearly
                // so, a radar or anti-radiation seeker is steered onto it --
                // and the weapon's own alignment limit is kept when tighter.
                string guidance = Guidance(info);
                float cone = guidance == "Optical" || guidance == "Laser" ? Tuning.MaxLaunchAngleOptical
                    : guidance.Length == 0 ? Tuning.MaxLaunchAngleRocket        // no seeker: an unguided rocket
                    : Tuning.MaxLaunchAngle;
                float limit = Mathf.Min(cone, info.targetRequirements.minAlignment > 0f ? info.targetRequirements.minAlignment : 180f);
                Vector3 toTarget = target.transform.position - aircraft.transform.position;
                float angle = toTarget.sqrMagnitude > 1f ? Vector3.Angle(aircraft.transform.forward, toTarget) : 0f;
                // An unguided rocket: the cockpit's own SHOOT cue where the
                // game has a solution (RocketSight), the plain cone where not.
                if (guidance.Length == 0 && RocketSight(aircraft, station, target, out float off, out float tolerance, out bool inRange))
                {
                    if (inRange && off <= tolerance) { angle = 0f; limit = 1f; }
                    else
                    {
                        float at = Time.timeSinceLevelLoad;
                        if (!said.TryGetValue((aircraft, target), out float lastSight) || at - lastSight > 20f)
                        {
                            said[(aircraft, target)] = at;
                            Tracing.Flight("[flight] " + (flight?.Name ?? aircraft.definition?.unitName ?? aircraft.name) + " · holding rockets · " + ShipNames.Of(target) +
                                (inRange ? " · aim " + off.ToString("0.0") + "° off the solution (limit " + tolerance.ToString("0.0") + "°)" : " · beyond 75% of range"));
                        }
                        OffNose++;
                        return false;
                    }
                }
                if (angle > limit)
                {
                    float at = Time.timeSinceLevelLoad;
                    if (!said.TryGetValue((aircraft, target), out float lastAngle) || at - lastAngle > 20f)
                    {
                        said[(aircraft, target)] = at;
                        Tracing.Flight("[flight] " + (flight?.Name ?? aircraft.definition?.unitName ?? aircraft.name) + " · holding fire · " + ShipNames.Of(target) + " is " + angle.ToString("0") + "° off the nose (" + (info.weaponName ?? "missile") + ", " + (guidance.Length > 0 ? guidance : "unknown") + " seeker, limit " + limit.ToString("0") + "°)");
                    }
                    OffNose++;
                    return false;
                }
                if (!info.missile) return true;                          // a rocket: the cone was the whole question
                // Overkill: an over-the-horizon (anti-ship) missile, or one that
                // costs several times what the target is worth, is not spent on
                // a vehicle when another armed anti-surface station in range
                // will do -- an Alkyon put anti-ship cruise missiles into a few
                // tanks. The cheaper station is selected and the shot proceeds
                // with it; with nothing else in range the shot is held.
                if (!(target is Aircraft) && !(target is Ship) && Overkill(info, target) && !StrikePlans.Saturating(flight, target, info))
                {
                    WeaponStation cheaper = CheaperStation(aircraft, __instance, target, info);
                    float at = Time.timeSinceLevelLoad;
                    if (!said.TryGetValue((aircraft, target), out float lastOver) || at - lastOver > 20f)
                    {
                        said[(aircraft, target)] = at;
                        Tracing.Flight("[flight] " + (flight?.Name ?? aircraft.definition?.unitName ?? aircraft.name) + " · " + (info.weaponName ?? "missile") + " is overkill for " + ShipNames.Of(target) +
                            (cheaper != null ? " · using " + (cheaper.WeaponInfo.weaponName ?? "another station") + " instead" : " · holding fire"));
                    }
                    Overkills++;
                    if (cheaper == null) return false;
                    __instance.currentWeaponStation = cheaper;
                    station = cheaper; info = cheaper.WeaponInfo;
                }
                int allowed = AllowedOn(flight, target, info);
                int live = Closing(aircraft.NetworkHQ, target);
                if (live < allowed)
                {
                    Fired(aircraft.NetworkHQ, target);              // the next ask counts this shot
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

        // How many of our missiles may be closing on this target at once: the
        // flight's own choice, else the default for its kind of target.
        internal static int AllowedOn(Flight flight, Unit target, WeaponInfo info)
        {
            if (StrikePlans.Saturating(flight, target, info)) return 99;     // everything chosen, at once
            if (flight != null && flight.MissilesPerTarget > 0) return flight.MissilesPerTarget;
            if (target is Aircraft) return Mathf.Max(1, Tuning.MissilesPerAirTarget);
            if (Tuning.MissilesPerSurfaceTarget > 0) return Tuning.MissilesPerSurfaceTarget;
            return Mathf.Clamp(Mathf.CeilToInt(info.CalcAttacksNeeded(target)), 1, 4);
        }

        internal static int Overkills;

        private static bool Overkill(WeaponInfo info, Unit target)
        {
            if (info.overHorizon || info.strategic) return true;
            float worth = target?.definition != null ? Mathf.Max(target.definition.value, 1f) : 1f;
            return info.costPerRound > worth * 3f;
        }

        // Another armed anti-surface station aboard whose weapon reaches the
        // target and is not itself overkill; the cheapest per round first.
        private static WeaponStation CheaperStation(Aircraft aircraft, WeaponManager manager, Unit target, WeaponInfo current)
        {
            if (aircraft.weaponStations == null) return null;
            float range = Vector3.Distance(aircraft.transform.position, target.transform.position);
            WeaponStation best = null;
            foreach (WeaponStation s in aircraft.weaponStations)
            {
                WeaponInfo i = s?.WeaponInfo;
                if (i == null || i == current || s.Ammo <= 0 || i.gun || i.bomb || i.cargo || i.troops || i.sling || i.jammer) continue;
                if (i.effectiveness.antiSurface < 0.3f || Overkill(i, target)) continue;
                if (range > i.targetRequirements.maxRange || range < i.targetRequirements.minRange) continue;
                if (best == null || i.costPerRound < best.WeaponInfo.costPerRound) best = s;
            }
            return best;
        }

        // How a weapon's missile finds its target, from the seeker on its
        // prefab, as the game names it: "IR", "ARH" (active radar), "ARAD"
        // (anti-radiation), "Optical", "Laser", or empty when it has none.
        private static readonly Dictionary<WeaponInfo, string> guidanceOf = new Dictionary<WeaponInfo, string>();
        // The cockpit's rocket sight (HUDBoresightState): SHOOT when the
        // target is inside 75% of the weapon's range and the aim point --
        // the game's own solution (ControlsFilter.CalcAim: lead from the
        // closing speed, gravity drop, corrected by a simulated trajectory
        // of the round) -- sits on the boresight, the pods' own direction.
        // The HUD's "on" is 20 pixels; this takes the game AI's strafing
        // tolerance instead, by the target's size at range (50 x radius /
        // range, 0.5-3 degrees), loosened by half again for a ripple of
        // rockets. False when the game has no solution (no muzzle speed,
        // out of range, or the pods more than 40 degrees off): the plain
        // cone decides then.
        internal static bool RocketSight(Aircraft aircraft, WeaponStation station, Unit target, out float off, out float tolerance, out bool inRange)
        {
            off = 0f; tolerance = 0f; inRange = false;
            try
            {
                if (aircraft == null || station?.WeaponInfo == null || target == null) return false;
                if (aircraft.weaponManager == null || aircraft.weaponManager.currentWeaponStation != station) return false;
                ControlsFilter filter = aircraft.GetControlsFilter();
                if (filter == null) return false;
                filter.GetAim(target, out GlobalPosition? aimPoint, out GlobalPosition? _);
                if (!aimPoint.HasValue) return false;
                Vector3 bore = Vector3.zero;
                GlobalPosition from = aircraft.GlobalPosition();
                foreach (Weapon weapon in station.Weapons)
                    if (weapon != null) bore += weapon.transform.forward;
                if (bore.sqrMagnitude < 0.01f) bore = aircraft.transform.forward;
                off = Vector3.Angle(bore, aimPoint.Value - from);
                float range = FastMath.Distance(target.GlobalPosition(), from);
                float maxRange = station.WeaponInfo.targetRequirements.maxRange;
                inRange = maxRange <= 0f || range < maxRange * 0.75f;
                tolerance = Mathf.Clamp(50f * Mathf.Max(target.maxRadius, 1f) / Mathf.Max(range, 10f), 0.5f, 3f) * 1.5f;
                return true;
            }
            catch { return false; }
        }

        public static string Guidance(WeaponInfo info)
        {
            if (info == null) return "";
            if (guidanceOf.TryGetValue(info, out string known)) return known;
            string type = "";
            try
            {
                MissileSeeker seeker = info.weaponPrefab != null ? info.weaponPrefab.GetComponent<MissileSeeker>() ?? info.weaponPrefab.GetComponentInChildren<MissileSeeker>(true) : null;
                if (seeker != null) type = seeker.GetSeekerType() ?? "";
            }
            catch { }
            guidanceOf[info] = type;
            // Once per weapon type: how the game classifies it, so a cone that
            // is wrong for a weapon can be read off the log rather than guessed.
            Tracing.Flight("[flight] weapon · " + (info.weaponName ?? "?") + " · seeker " + (type.Length > 0 ? type : "none") + (info.missile ? " · missile" : "") + (info.laserGuided ? " · laser-guided" : "") + (info.gun ? " · gun" : "") + (info.bomb ? " · bomb" : "") +
                " · alignment " + info.targetRequirements.minAlignment.ToString("0") + "° · reach " + (info.targetRequirements.maxRange / 1000f).ToString("0.0") + " km");
            return type;
        }

        // Our side's missiles still on their way to this target: any live
        // missile of ours with the target's id that is not flying away from
        // it. A shot launched at 35 km is a shot spent; counting only the
        // last 15 km let nine native pilots empty their racks in one run.
        private static readonly Dictionary<Missile, float> firstSeen = new Dictionary<Missile, float>();
        private const float CountsFor = 120f;    // a missile two minutes out is not arriving

        // Asked per fire attempt, and a held shot is attempted again every
        // physics step: the count is kept a fifth of a second per target
        // rather than walking every unit each time.
        private static readonly Dictionary<(FactionHQ, Unit), (int count, float at)> closingCache = new Dictionary<(FactionHQ, Unit), (int, float)>();

        internal static int Closing(FactionHQ hq, Unit target)
        {
            if (hq == null || target == null) return 0;
            float at = Time.timeSinceLevelLoad;
            if (closingCache.TryGetValue((hq, target), out var cached) && at - cached.at < 0.2f && at >= cached.at) return cached.count;
            int count = Count(hq, target);
            if (closingCache.Count > 200) closingCache.Clear();
            closingCache[(hq, target)] = (count, at);
            return count;
        }

        // A shot just fired counts at once, not a fifth of a second later.
        internal static void Fired(FactionHQ hq, Unit target) { if (hq != null && target != null) closingCache.Remove((hq, target)); }

        private static int Count(FactionHQ hq, Unit target)
        {
            int live = 0;
            float now = Time.timeSinceLevelLoad;
            if (firstSeen.Count > 500) firstSeen.Clear();
            foreach (Missile missile in MissileIndex.At(target))
            {
                if (missile == null || missile.disabled || missile.NetworkHQ != hq || missile.targetID != target.persistentID) continue;
                // Only what aircraft have fired: a ship's SAM or a ground
                // launcher's shot is a separate layer, often decoyed or shot
                // down, and counting it grounded a wing -- one Scythe and one
                // sea-launched missile filled an aircraft's allowance of two.
                if (!(missile.owner is Aircraft)) continue;
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
