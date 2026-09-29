using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Everything out in one pass.
    //
    // The game's helicopter transport state fires its cargo station once --
    // one vehicle -- and then, on an airdrop, hands the aircraft straight to
    // the combat state; on a landing it sits seven seconds and takes off. A
    // helicopter carrying two or four vehicles had to come round for each,
    // and on the way round the game's own logic was free to go elsewhere: one
    // landed on a frigate's helipad and put its second vehicle into the sea.
    //
    // So once one of ours has started unloading on a delivery we ordered,
    // the rest follow: on an airdrop a stick, a moment apart, along the run;
    // on a landing each as the ramp clears the last, with the aircraft held
    // on the ground until it is empty. And none of our deliveries unloads on
    // a ship's deck -- a supply run to a ship is flown by its own path and is
    // not affected.
    internal static class CargoBurst
    {
        private sealed class Burst { internal float Started, Next, Gap; internal bool Airdrop; }

        private static readonly Dictionary<Flight, Burst> bursts = new Dictionary<Flight, Burst>();
        private const float AirdropGap = 0.5f, LandedGap = 1.2f, AirdropFor = 10f, LandedFor = 40f;

        internal static bool Active(Flight flight) => flight != null && bursts.ContainsKey(flight);

        internal static void Begin(Flight flight, bool airdrop)
        {
            if (flight == null || bursts.ContainsKey(flight)) return;
            float gap = Gap(flight, airdrop);
            bursts[flight] = new Burst { Started = Time.timeSinceLevelLoad, Next = Time.timeSinceLevelLoad + gap, Airdrop = airdrop, Gap = gap };
        }

        // Along a line: spaced to cover this aircraft's stretch at its speed.
        private static float Gap(Flight flight, bool airdrop)
        {
            if (!airdrop) return LandedGap;
            if (!flight.HasDropLine || flight.DropSpacing <= 0f || flight.Aircraft == null) return AirdropGap;
            return Mathf.Clamp(flight.DropSpacing / Mathf.Max(flight.Aircraft.speed, 5f), 0.3f, 6f);
        }

        internal static void Tick()
        {
            if (bursts.Count == 0) return;
            var done = new List<Flight>();
            foreach (KeyValuePair<Flight, Burst> entry in bursts)
            {
                Flight flight = entry.Key;
                Burst burst = entry.Value;
                Aircraft aircraft = flight?.Aircraft;
                float now = Time.timeSinceLevelLoad;
                if (aircraft == null || aircraft.disabled || FlightOrders.CargoAboard(aircraft) <= 0 ||
                    now - burst.Started > (burst.Airdrop ? AirdropFor + burst.Gap * 6f : LandedFor)) { done.Add(flight); continue; }
                // Landed: only while still on the ground. Lifted off with some
                // aboard, the rest stays aboard rather than falling unslowed.
                if (!burst.Airdrop && (aircraft.radarAlt > 2f || aircraft.speed > 10f)) { done.Add(flight); continue; }
                if (now < burst.Next) continue;
                Pilot pilot = FlightOrders.FirstPilot(aircraft);
                WeaponStation station = CargoStation(aircraft);
                if (pilot == null || station == null) { done.Add(flight); continue; }
                if (!station.Ready() || station.SalvoInProgress) continue;
                aircraft.weaponManager.currentWeaponStation = station;
                int before = station.Ammo;
                pilot.Fire();
                burst.Next = now + burst.Gap;
                if (station.Ammo < before)
                    Tracing.Flight("[flight] " + flight.Name + " · " + (burst.Airdrop ? "dropped" : "unloaded") + " " +
                        (station.WeaponInfo?.weaponName ?? "cargo") + " · " + FlightOrders.CargoAboard(aircraft) + " left aboard");
            }
            foreach (Flight flight in done) bursts.Remove(flight);
        }

        internal static WeaponStation CargoStation(Aircraft aircraft)
        {
            if (aircraft?.weaponStations == null) return null;
            foreach (WeaponStation station in aircraft.weaponStations)
                if (station?.WeaponInfo != null && station.WeaponInfo.cargo && station.Ammo > 0) return station;
            return null;
        }

        // Standing on a ship: the ground under the skids belongs to a ship.
        internal static bool OnShip(Aircraft aircraft, out Ship ship)
        {
            ship = null;
            if (aircraft == null || aircraft.radarAlt > 6f) return false;
            Vector3 at = aircraft.transform.position;
            foreach (RaycastHit hit in Physics.RaycastAll(at + Vector3.up * 2f, Vector3.down, 20f))
            {
                if (hit.collider == null) continue;
                Unit unit = hit.collider.GetComponentInParent<Unit>();
                if (unit == aircraft) continue;
                if (unit is Ship found) { ship = found; return true; }
            }
            return false;
        }
    }

    // The game's DeployCargo, for one of ours on a delivery we ordered: never
    // onto a ship's deck, and the first item starts the rest.
    [HarmonyPatch(typeof(AIHeloTransportState), "DeployCargo")]
    internal static class CargoBurstPatch
    {
        private const string Name = "Cargo in one pass";
        private static readonly FieldInfo StateAircraft = AccessTools.Field(typeof(PilotBaseState), "aircraft");
        private static readonly FieldInfo Airdrop = AccessTools.Field(typeof(AIHeloTransportState), "airdrop");
        private static readonly FieldInfo Deployed = AccessTools.Field(typeof(AIHeloTransportState), "deployedCargo");
        private static readonly Dictionary<Flight, float> refusedAt = new Dictionary<Flight, float>();

        private static bool Prefix(AIHeloTransportState __instance)
        {
            if (!Guard.Ok(Name)) return true;
            try
            {
                Flight flight = Ours(__instance);
                if (flight == null) return true;
                bool airdrop = Airdrop != null && (bool)Airdrop.GetValue(__instance);
                // Unless that ship is where it was sent: cargo taken home to its deck.
                if (!airdrop && CargoBurst.OnShip(StateAircraft.GetValue(__instance) as Aircraft, out Ship ship) &&
                    FastMath.Distance(ship.GlobalPosition(), flight.CargoPoint) > ship.maxRadius + 300f)
                {
                    if (!refusedAt.TryGetValue(flight, out float at) || Time.timeSinceLevelLoad - at > 30f)
                    {
                        refusedAt[flight] = Time.timeSinceLevelLoad;
                        Host.LogWarning("[flight] " + flight.Name + " · on " + ShipNames.Of(ship) + "'s deck, not unloading there");
                        Host.Say(flight.Name + " · on a ship's deck, holding the cargo");
                    }
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }

        private static void Postfix(AIHeloTransportState __instance)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Flight flight = Ours(__instance);
                if (flight == null || Deployed == null || !(bool)Deployed.GetValue(__instance)) return;
                bool airdrop = Airdrop != null && (bool)Airdrop.GetValue(__instance);
                if (FlightOrders.CargoAboard(flight.Aircraft) > 0) CargoBurst.Begin(flight, airdrop);
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        private static Flight Ours(AIHeloTransportState state)
        {
            if (!(StateAircraft?.GetValue(state) is Aircraft aircraft)) return null;
            Flight flight = FlightOrders.Of(aircraft);
            return flight != null && flight.Mode == FlightMode.Cargo && flight.SupplyShip == null ? flight : null;
        }
    }

    // Held on the ground while the rest comes off: the state takes off seven
    // seconds after its one item, whatever is still aboard.
    [HarmonyPatch(typeof(AIHeloTransportState), "FixedUpdateState")]
    internal static class CargoHoldOnGroundPatch
    {
        private const string Name = "Cargo in one pass";
        private static readonly FieldInfo StateAircraft = AccessTools.Field(typeof(PilotBaseState), "aircraft");
        private static readonly FieldInfo TouchedDown = AccessTools.Field(typeof(AIHeloTransportState), "touchedDownTime");

        private static void Prefix(AIHeloTransportState __instance)
        {
            if (!Guard.Ok(Name) || TouchedDown == null) return;
            try
            {
                if (!(StateAircraft?.GetValue(__instance) is Aircraft aircraft)) return;
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || !CargoBurst.Active(flight) || FlightOrders.CargoAboard(aircraft) <= 0) return;
                if ((float)TouchedDown.GetValue(__instance) > 5f) TouchedDown.SetValue(__instance, 5f);
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
