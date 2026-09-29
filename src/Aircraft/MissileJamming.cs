using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Aims every jamming pod on our flights: the jamming task's targets, and
    // radar-guided missiles fired at the aircraft.
    //
    // A pod jams whatever unit it is aimed at, missiles included, and both
    // active and semi-active radar seekers build up jamming from a jammer
    // inside their field of view -- which the aircraft they are chasing always
    // is -- until they lose their lock. Neither the game's AI nor the jamming
    // task ever aimed a pod at a missile, so an EW aircraft kept its jammers on
    // a SAM's radar while that SAM's missiles came in unjammed.
    //
    // Allocation, several times a second: missiles take pods from the last
    // pod backwards -- the one kept spare first (a jamming flight's task is
    // one pod short of its pods, see FlightOrders.JamCapacity), then pods
    // borrowed from the lowest-priority task targets -- and every other pod
    // jams the task's targets in order, spare ones doubling up. When the
    // missiles are gone the borrowed pods go straight back. A pod we aim
    // keeps its aim: the game's own pilot, evading, would otherwise turn it.
    internal static class MissileJamming
    {
        private static readonly Dictionary<Weapon, Unit> assigned = new Dictionary<Weapon, Unit>();
        private static readonly Dictionary<Weapon, WeaponStation> stations = new Dictionary<Weapon, WeaponStation>();
        private static readonly Dictionary<Weapon, Aircraft> owners = new Dictionary<Weapon, Aircraft>();
        private static readonly HashSet<Missile> announced = new HashSet<Missile>();
        private static readonly List<Missile> inbound = new List<Missile>();
        private static readonly List<Unit> tasks = new List<Unit>();
        private static float nextChoice;
        [ThreadStatic] internal static bool Aiming;

        internal struct Pod
        {
            internal Weapon Weapon;
            internal WeaponStation Station;
        }

        // Every jammer on the aircraft, whichever station it is on: anything
        // the game flags as a jammer -- any airframe, any mod's jamming
        // weapon, a jammer built into a fixed hardpoint -- as well as the
        // game's own JammingPod however it is listed. Aimed through the base
        // Weapon calls, so a mod's own jammer class works too.
        internal static List<Pod> Pods(Aircraft aircraft)
        {
            var pods = new List<Pod>();
            if (aircraft?.weaponStations == null) return pods;
            foreach (WeaponStation station in aircraft.weaponStations)
            {
                if (station?.Weapons == null) continue;
                bool flagged = station.WeaponInfo != null && station.WeaponInfo.jammer;
                foreach (Weapon weapon in station.Weapons)
                    if (weapon != null && (flagged || weapon is JammingPod)) pods.Add(new Pod { Weapon = weapon, Station = station });
            }
            return pods;
        }

        private static readonly System.Reflection.FieldInfo Falloff = AccessTools.Field(typeof(JammingPod), "rangeFalloff");
        private const float StillEffective = 0.4f;   // of the pod's best, at the edge of its reach

        // How far the aircraft's pods still jam well: the furthest distance at
        // which the weakest pod's range falloff is still 40% of its best.
        // Zero when no pod says.
        internal static float Reach(Aircraft aircraft)
        {
            float reach = float.MaxValue;
            foreach (Pod pod in Pods(aircraft))
            {
                float here = 0f;
                if (pod.Weapon is JammingPod && Falloff?.GetValue(pod.Weapon) is AnimationCurve curve && curve.length > 0)
                {
                    float end = curve[curve.length - 1].time, best = 0f;
                    for (float d = 0f; d <= end; d += end / 64f) best = Mathf.Max(best, curve.Evaluate(d));
                    for (float d = end; d > 0f && best > 0f; d -= end / 64f)
                        if (curve.Evaluate(d) >= best * StillEffective) { here = d; break; }
                }
                float listed = pod.Station?.WeaponInfo != null ? pod.Station.WeaponInfo.targetRequirements.maxRange : 0f;
                if (listed > 0f) here = here > 0f ? Mathf.Min(here, listed) : listed;
                if (here > 0f) reach = Mathf.Min(reach, here);
            }
            return reach == float.MaxValue ? 0f : reach;
        }

        internal static bool Aimed(Weapon pod) =>
            pod != null && assigned.TryGetValue(pod, out Unit target) && target != null && !target.disabled;

        internal static void Tick()
        {
            bool choose = Time.timeSinceLevelLoad >= nextChoice;
            if (choose)
            {
                nextChoice = Time.timeSinceLevelLoad + 0.25f;
                Choose();
            }
            SelfProtection(choose);

            // A pod switches itself off unless fired again each frame.
            foreach (KeyValuePair<Weapon, Unit> entry in assigned)
            {
                Weapon pod = entry.Key;
                Unit target = entry.Value;
                if (pod == null || target == null || target.disabled) continue;
                if (!owners.TryGetValue(pod, out Aircraft aircraft) || aircraft == null || aircraft.disabled) continue;
                if (!stations.TryGetValue(pod, out WeaponStation station) || station == null) continue;
                Aiming = true;
                try
                {
                    pod.SetTarget(target);
                    pod.Fire(aircraft, target, aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero, station, default(GlobalPosition));
                }
                finally { Aiming = false; }
            }
        }

        // Self-protection jammers (RadarJammer, a countermeasure many airframes
        // carry built in) raise the aircraft's own ECM, which breaks a radar
        // seeker's lock when it is close. The native pilot runs them while it
        // evades; under our own state nothing did. Run them while a radar
        // missile is coming at one of our flights we are flying ourselves.
        private static readonly Dictionary<Aircraft, RadarJammer[]> ecm = new Dictionary<Aircraft, RadarJammer[]>();
        private static readonly HashSet<Aircraft> underThreat = new HashSet<Aircraft>();
        private const float EcmFrom = 15000f;

        private static void SelfProtection(bool choose)
        {
            if (choose)
            {
                underThreat.Clear();
                foreach (Flight flight in FlightOrders.All())
                {
                    Aircraft aircraft = flight.Aircraft;
                    if (aircraft == null || aircraft.disabled || flight.Interrupted || Host.IsFlownByPlayer(flight)) continue;
                    if (!ecm.TryGetValue(aircraft, out RadarJammer[] jammers)) ecm[aircraft] = jammers = aircraft.GetComponentsInChildren<RadarJammer>(true);
                    if (jammers.Length == 0) continue;
                    foreach (Unit unit in UnitRegistry.allUnits)
                    {
                        if (!(unit is Missile missile) || missile.disabled || missile.targetID != aircraft.persistentID) continue;
                        string seeker = missile.GetSeekerType();
                        if (seeker != "ARH" && seeker != "SARH") continue;
                        if (Vector3.Distance(missile.transform.position, aircraft.transform.position) > EcmFrom) continue;
                        underThreat.Add(aircraft);
                        break;
                    }
                }
                var gone = new List<Aircraft>();
                foreach (Aircraft aircraft in ecm.Keys) if (aircraft == null || aircraft.disabled) gone.Add(aircraft);
                foreach (Aircraft aircraft in gone) ecm.Remove(aircraft);
            }
            // Like a pod, it switches off unless fired again (within 0.1 s).
            foreach (Aircraft aircraft in underThreat)
            {
                if (aircraft == null || aircraft.disabled || !ecm.TryGetValue(aircraft, out RadarJammer[] jammers)) continue;
                foreach (RadarJammer jammer in jammers) if (jammer != null) jammer.Fire();
            }
        }

        private static void Choose()
        {
            assigned.Clear();
            stations.Clear();
            owners.Clear();
            foreach (Flight flight in FlightOrders.All())
            {
                Aircraft aircraft = flight.Aircraft;
                if (aircraft == null || aircraft.disabled || Host.IsFlownByPlayer(flight)) continue;
                List<Pod> pods = Pods(aircraft);
                if (pods.Count == 0) continue;

                Inbound(aircraft, pods[0].Station, inbound);
                tasks.Clear();
                if (flight.Mode == FlightMode.Jam)
                    foreach (Unit unit in flight.JamTargets) if (unit != null && !unit.disabled) tasks.Add(unit);

                // Missiles from the last pod back; the rest on the task.
                int onMissiles = Mathf.Min(inbound.Count, pods.Count);
                int onTask = pods.Count - onMissiles;
                for (int i = 0; i < pods.Count; i++)
                {
                    Unit target = null;
                    if (i >= onTask) target = inbound[pods.Count - 1 - i];
                    else if (tasks.Count > 0) target = tasks[i % tasks.Count];
                    if (target == null) continue;
                    assigned[pods[i].Weapon] = target;
                    stations[pods[i].Weapon] = pods[i].Station;
                    owners[pods[i].Weapon] = aircraft;
                }
                for (int m = 0; m < onMissiles; m++)
                    if (announced.Add(inbound[m]))
                    {
                        int dropped = Mathf.Max(0, tasks.Count - onTask);
                        Tracing.Flight("[flight] " + flight.Name + " · jamming an inbound " + inbound[m].GetSeekerType() +
                            " missile at " + (Vector3.Distance(aircraft.transform.position, inbound[m].transform.position) / 1000f).ToString("0.0") + " km" +
                            (dropped > 0 ? " · " + dropped + " task target(s) paused" : ""));
                    }
            }
            announced.RemoveWhere(m => m == null || m.disabled);
        }

        // Radar-guided shots at this aircraft that the pod can jam: its side
        // has to be tracking the missile (the pod will not jam an unseen
        // target) and it has to be within the pod's reach. Nearest first.
        private static void Inbound(Aircraft aircraft, WeaponStation station, List<Missile> into)
        {
            into.Clear();
            float reach = station?.WeaponInfo != null && station.WeaponInfo.targetRequirements.maxRange > 0f
                ? station.WeaponInfo.targetRequirements.maxRange : 20000f;
            FactionHQ hq = aircraft.NetworkHQ;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile missile) || missile.disabled || missile.targetID != aircraft.persistentID) continue;
                if (missile.NetworkHQ == null || missile.NetworkHQ == hq) continue;
                string seeker = missile.GetSeekerType();
                if (seeker != "ARH" && seeker != "SARH") continue;
                if (hq != null && !hq.IsTargetBeingTracked(missile)) continue;
                if (Vector3.Distance(missile.transform.position, aircraft.transform.position) > reach) continue;
                into.Add(missile);
            }
            Vector3 here = aircraft.transform.position;
            into.Sort((a, b) => (a.transform.position - here).sqrMagnitude.CompareTo((b.transform.position - here).sqrMagnitude));
        }

    }

    // A pod we aim stays on its target until we move it.
    [HarmonyPatch(typeof(JammingPod), nameof(JammingPod.SetTarget))]
    internal static class KeepPodOnMissilePatch
    {
        private const string Name = "Missile jamming";

        private static bool Prefix(JammingPod __instance, Unit target)
        {
            if (MissileJamming.Aiming || !Guard.Ok(Name)) return true;
            try { return !MissileJamming.Aimed(__instance); }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
