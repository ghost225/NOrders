using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Home to park, or home to rearm.
    //
    // The game's AI lands, taxis to a service point, and its crew get out,
    // which returns the airframe to the faction's reserve. That is what a
    // flight of ours does by default, and always on a ship's deck: a small
    // deck has nowhere to turn an aircraft round, and one that relaunched
    // from it rarely got off again. A flight set to rearm (Flight.RearmAtHome)
    // that lands at a land airfield is kept instead: parked where it stopped,
    // rearmed station by station (the faction pays per round, as for a rearm
    // truck) and refuelled, and then it taxis out again as the same flight to
    // the task it had when it was sent home -- or to a new one given while it
    // was on the ground.
    internal static class Turnaround
    {
        private sealed class Parked
        {
            internal Flight Flight;
            internal float Since;
            internal bool Serviced;
            internal string Where;
        }

        private static readonly Dictionary<Aircraft, Parked> parked = new Dictionary<Aircraft, Parked>();

        internal static bool IsParked(Flight flight) => flight?.Aircraft != null && parked.ContainsKey(flight.Aircraft);

        // Whether this aircraft, down and stopping, is to be kept: one of ours,
        // back from a flight home, set to rearm, and on a land field of its own
        // side.
        internal static bool Keeps(Aircraft aircraft, Pilot pilot, out Flight flight, out Airbase field)
        {
            flight = null; field = null;
            if (aircraft == null || pilot == null || pilot.playerControlled || aircraft.Player != null || !aircraft.IsServer) return false;
            if (!pilot.flightInfo.HasTakenOff) return false;
            flight = FlightOrders.Of(aircraft);
            if (flight == null || !flight.RearmAtHome || flight.Mode != FlightMode.ReturnToBase) return false;
            FactionHQ hq = aircraft.NetworkHQ;
            if (hq == null || !hq.TryGetNearestAirbase(aircraft.transform.position, 3000f, out field) || field == null || field.disabled) return false;
            if (field.AttachedAirbase) return false;                // decks always park
            return FastMath.InRange(aircraft.GlobalPosition(), Airfields.PositionOf(field), Mathf.Max(field.GetRadius() + 500f, 1500f));
        }

        internal static void Park(Aircraft aircraft, Flight flight, Pilot pilot, Airbase field)
        {
            if (parked.ContainsKey(aircraft)) return;
            ControlInputs inputs = aircraft.GetInputs();
            if (inputs != null) { inputs.brake = 1f; inputs.throttle = 0f; }
            pilot.SwitchState(pilot.parkedState);
            string where = field != null ? Airfields.NameOf(field) : "the field";
            parked[aircraft] = new Parked { Flight = flight, Since = Time.timeSinceLevelLoad, Where = where };
            flight.Doing("TURNING ROUND · " + where);
            Host.LogInfo("[flight] " + flight.Name + " · down at " + where + ", turning round");
            Host.Say(flight.Name + " · down at " + where + ", rearming");
        }

        internal static void Tick()
        {
            if (parked.Count == 0) return;
            float now = Time.timeSinceLevelLoad;
            var gone = new List<Aircraft>();
            var standDown = new List<Aircraft>();
            foreach (KeyValuePair<Aircraft, Parked> entry in parked)
            {
                Aircraft aircraft = entry.Key;
                Parked p = entry.Value;
                if (aircraft == null || aircraft.disabled || p.Flight == null || p.Flight.Aircraft != aircraft) { gone.Add(aircraft); continue; }
                Pilot pilot = FlightOrders.FirstPilot(aircraft);
                // Someone else has it now -- a player took the seat, or the
                // game moved it on: forget it.
                if (pilot == null || pilot.playerControlled || !(pilot.currentState is PilotParkedState)) { gone.Add(aircraft); continue; }
                // Told to park after all, while it sat there.
                if (!p.Flight.RearmAtHome) { standDown.Add(aircraft); continue; }
                if (!p.Serviced)
                {
                    float left = Tuning.TurnaroundSeconds - (now - p.Since);
                    p.Flight.Doing("TURNING ROUND · " + p.Where + " · " + Mathf.CeilToInt(Mathf.Max(left, 0f)) + " s");
                    if (left > 0f) continue;
                    Service(aircraft, p);
                    p.Serviced = true;
                }
                Relaunch(p.Flight, pilot, p.Where);
                gone.Add(aircraft);
            }
            foreach (Aircraft aircraft in standDown)
            {
                Parked p = parked[aircraft];
                Host.LogInfo("[flight] " + p.Flight.Name + " · parked at " + p.Where + ", stood down to the reserve");
                aircraft.StartEjectionSequence();
                gone.Add(aircraft);
            }
            foreach (Aircraft aircraft in gone) parked.Remove(aircraft);
        }

        private static void Service(Aircraft aircraft, Parked p)
        {
            FactionHQ hq = aircraft.NetworkHQ;
            int rounds = 0; float cost = 0f;
            foreach (WeaponStation station in aircraft.weaponStations)
            {
                if (station?.WeaponInfo == null || station.WeaponInfo.cargo || station.WeaponInfo.troops) continue;
                int missing = station.FullAmmo - station.Ammo;
                if (missing <= 0) continue;
                float perRound = Mathf.Max(station.WeaponInfo.costPerRound, 0f);
                float funds = hq != null ? hq.factionFunds : 0f;
                int affordable = perRound > 0f ? Mathf.Min(missing, Mathf.FloorToInt(Mathf.Max(funds - cost, 0f) / perRound)) : missing;
                if (affordable <= 0) continue;
                station.Rearm(affordable);
                rounds += affordable; cost += affordable * perRound;
            }
            if (cost > 0f && hq != null) hq.AddFunds(-cost);
            aircraft.Refuel(null);
            p.Flight.RefreshStores();
            Host.LogInfo("[flight] " + p.Flight.Name + " · serviced at " + p.Where + " · " + rounds + " rounds for " + cost.ToString("0") + " · fuelled");
        }

        // Out again as the same flight: a fresh taxi to the runway (or a
        // helicopter's lift-off), then FlightOrders adopts it once it is
        // flying, as after any launch, with the task restored.
        private static void Relaunch(Flight flight, Pilot pilot, string where)
        {
            string task = FlightOrders.Resume(flight);
            pilot.flightInfo.HasTakenOff = false;           // to the runway, not to the service point again
            ControlInputs inputs = flight.Aircraft.GetInputs();
            if (inputs != null) inputs.brake = 0f;
            if (FlightOrders.IsRotary(pilot))
            {
                if (pilot.AIHeloTakeoffState == null) pilot.AIHeloTakeoffState = new AIHeloTakeoffState();
                pilot.SwitchState(pilot.AIHeloTakeoffState);
            }
            else
            {
                pilot.AITaxiState = new AIPilotTaxiState();
                pilot.SwitchState(pilot.AITaxiState);
            }
            flight.Adopted = false;
            flight.Interrupted = false;
            Host.LogInfo("[flight] " + flight.Name + " · rearmed, taxiing out from " + where + " · " + task);
            Host.Say(flight.Name + " · rearmed, going back out · " + task);
        }

        internal static void Forget(Aircraft aircraft) => parked.Remove(aircraft);
    }

    // Every way the game ends a landing -- the taxi state at the service
    // point, a vertical landing sat on the spot, a helicopter down on its pad
    // -- gets the crew out, which returns the airframe. For a flight to be
    // turned round, down and stopped on the field, it is parked instead.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.StartEjectionSequence))]
    internal static class TurnaroundPatch
    {
        private const string Name = "Turnaround";

        private static bool Prefix(Aircraft __instance)
        {
            if (!Guard.Ok(Name)) return true;
            try
            {
                Aircraft aircraft = __instance;
                Pilot pilot = FlightOrders.FirstPilot(aircraft);
                PilotBaseState state = pilot?.currentState;
                if (!(state is AIPilotTaxiState || state is AIPilotLandingState || state is AIHeloLandingState)) return true;
                if (aircraft.rb == null || aircraft.speed > 1.5f || aircraft.radarAlt > 2f) return true;
                if (!Turnaround.Keeps(aircraft, pilot, out Flight flight, out Airbase field)) return true;
                Turnaround.Park(aircraft, flight, pilot, field);
                return false;
            }
            catch (Exception ex) { Guard.Failed(Name, ex); return true; }
        }
    }
}
