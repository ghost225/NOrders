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
        private static readonly Dictionary<JammingPod, Unit> assigned = new Dictionary<JammingPod, Unit>();
        private static readonly Dictionary<JammingPod, WeaponStation> stations = new Dictionary<JammingPod, WeaponStation>();
        private static readonly Dictionary<JammingPod, Aircraft> owners = new Dictionary<JammingPod, Aircraft>();
        private static readonly HashSet<Missile> announced = new HashSet<Missile>();
        private static readonly List<Missile> inbound = new List<Missile>();
        private static readonly List<Unit> tasks = new List<Unit>();
        private static float nextChoice;
        [ThreadStatic] internal static bool Aiming;

        internal struct Pod
        {
            internal JammingPod Weapon;
            internal WeaponStation Station;
        }

        // Every jamming pod on the aircraft, whichever station it hangs on.
        internal static List<Pod> Pods(Aircraft aircraft)
        {
            var pods = new List<Pod>();
            if (aircraft?.weaponStations == null) return pods;
            foreach (WeaponStation station in aircraft.weaponStations)
            {
                if (station?.Weapons == null) continue;
                foreach (Weapon weapon in station.Weapons)
                    if (weapon is JammingPod pod && pod != null) pods.Add(new Pod { Weapon = pod, Station = station });
            }
            return pods;
        }

        internal static bool Aimed(JammingPod pod) =>
            pod != null && assigned.TryGetValue(pod, out Unit target) && target != null && !target.disabled;

        internal static void Tick()
        {
            if (Time.timeSinceLevelLoad >= nextChoice)
            {
                nextChoice = Time.timeSinceLevelLoad + 0.25f;
                Choose();
            }

            // A pod switches itself off unless fired again each frame.
            foreach (KeyValuePair<JammingPod, Unit> entry in assigned)
            {
                JammingPod pod = entry.Key;
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
