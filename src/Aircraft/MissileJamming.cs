using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // A flight carrying jamming pods turns them on radar-guided missiles fired
    // at it. A pod jams whatever unit it is aimed at, missiles included, and
    // both active and semi-active radar seekers build up jamming from a
    // jammer inside their field of view -- which the aircraft they are
    // chasing always is -- until they lose their lock. Neither the game's AI
    // nor the jamming task ever aimed a pod at a missile, only at radars, so
    // an EW aircraft kept its jammers on a SAM's radar while that SAM's
    // missiles came in unjammed and shot it down.
    //
    // Nearest missile first, one pod each. Pods left over stay on the flight's
    // own jamming task, so a two-pod aircraft keeps jamming the radar with one
    // while it jams the missile with the other. A pod given a missile keeps
    // it: the game's own pilot, evading, would otherwise turn it back.
    internal static class MissileJamming
    {
        private static readonly Dictionary<JammingPod, Missile> assigned = new Dictionary<JammingPod, Missile>();
        private static readonly HashSet<Missile> announced = new HashSet<Missile>();
        private static readonly List<Missile> inbound = new List<Missile>();
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

        internal static bool OnMissile(JammingPod pod) =>
            pod != null && assigned.TryGetValue(pod, out Missile missile) && missile != null && !missile.disabled;

        internal static void Tick()
        {
            bool choose = Time.timeSinceLevelLoad >= nextChoice;
            if (choose)
            {
                nextChoice = Time.timeSinceLevelLoad + 0.25f;
                assigned.Clear();
                foreach (Flight flight in FlightOrders.All())
                {
                    Aircraft aircraft = flight.Aircraft;
                    if (aircraft == null || aircraft.disabled || Host.IsFlownByPlayer(flight)) continue;
                    List<Pod> pods = Pods(aircraft);
                    if (pods.Count == 0) continue;
                    Inbound(aircraft, pods[0].Station, inbound);
                    for (int i = 0; i < pods.Count && i < inbound.Count; i++)
                    {
                        assigned[pods[i].Weapon] = inbound[i];
                        if (announced.Add(inbound[i]))
                            Tracing.Flight("[flight] " + flight.Name + " · jamming an inbound " + inbound[i].GetSeekerType() +
                                " missile at " + (Vector3.Distance(aircraft.transform.position, inbound[i].transform.position) / 1000f).ToString("0.0") + " km");
                    }
                }
                announced.RemoveWhere(m => m == null || m.disabled);
            }

            // A pod switches itself off unless fired again each frame.
            foreach (KeyValuePair<JammingPod, Missile> entry in assigned)
            {
                JammingPod pod = entry.Key;
                Missile missile = entry.Value;
                if (pod == null || missile == null || missile.disabled) continue;
                Aircraft aircraft = pod.GetComponentInParent<Aircraft>();
                WeaponStation station = StationOf(aircraft, pod);
                if (aircraft == null || station == null) continue;
                Aiming = true;
                try
                {
                    pod.SetTarget(missile);
                    pod.Fire(aircraft, missile, aircraft.rb != null ? aircraft.rb.velocity : Vector3.zero, station, default(GlobalPosition));
                }
                finally { Aiming = false; }
            }
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

        private static WeaponStation StationOf(Aircraft aircraft, JammingPod pod)
        {
            if (aircraft?.weaponStations == null) return null;
            foreach (WeaponStation station in aircraft.weaponStations)
                if (station?.Weapons != null && station.Weapons.Contains(pod)) return station;
            return null;
        }
    }

    // A pod on a missile stays on it until the missile is gone or passed.
    [HarmonyPatch(typeof(JammingPod), nameof(JammingPod.SetTarget))]
    internal static class KeepPodOnMissilePatch
    {
        private const string Name = "Missile jamming";

        private static bool Prefix(JammingPod __instance, Unit target)
        {
            if (MissileJamming.Aiming || !Guard.Ok(Name)) return true;
            try { return !MissileJamming.OnMissile(__instance); }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
