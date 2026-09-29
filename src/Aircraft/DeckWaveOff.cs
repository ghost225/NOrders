using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Waving off an approach to a deck that has turned.
    //
    // An aircraft sets up its final approach to the deck's heading as it
    // turns in; if the ship then turns, the approach no longer points down the
    // deck, and aircraft were seen trying to land across it. Real carriers
    // hold course to recover; this leaves the ship free to manoeuvre and
    // makes the aircraft go round instead. On the turn to final or the final
    // approach, if the deck has swung more than 15 degrees since the turn in
    // began, the game's own abort is triggered -- climb out, gear up -- and a
    // flight of ours on its way home is taken back once clear and sent into a
    // fresh approach to the deck as it lies now. Other aircraft go round the
    // game's own way. Ownership.Acts on the ship, as for any fix to the
    // game's behaviour.
    internal static class DeckWaveOff
    {
        private static readonly FieldInfo Mode = AccessTools.Field(typeof(AIPilotLandingState), "landingMode");
        private static readonly FieldInfo Base = AccessTools.Field(typeof(AIPilotLandingState), "airbase");
        private static readonly MethodInfo Switch = AccessTools.Method(typeof(AIPilotLandingState), "SwitchMode");

        private const int TurningToFinal = 1, StabilizedApproach = 2, Aborting = 5;
        private const float DeckSwing = 15f;

        private static readonly Dictionary<Aircraft, float> headingAtTurnIn = new Dictionary<Aircraft, float>();
        private static readonly Dictionary<Flight, float> wavedOff = new Dictionary<Flight, float>();
        private static float nextSweep;

        internal static void Tick()
        {
            if (Mode == null || Base == null || Switch == null || !MissionManager.IsRunning) return;
            if (Time.timeSinceLevelLoad < nextSweep) return;
            nextSweep = Time.timeSinceLevelLoad + 0.25f;

            var seen = new HashSet<Aircraft>();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft aircraft) || aircraft.disabled || aircraft.Player != null || !aircraft.IsServer) continue;
                Pilot pilot = FlightOrders.FirstPilot(aircraft);
                if (!(pilot?.currentState is AIPilotLandingState landing)) continue;
                if (!(Base.GetValue(landing) is Airbase airbase) || !airbase.AttachedAirbase) continue;
                Ship ship = Airfields.ShipOf(airbase);
                if (ship == null || ship.disabled || !Ownership.Acts(ship)) continue;

                int mode = Convert.ToInt32(Mode.GetValue(landing));
                if ((mode != TurningToFinal && mode != StabilizedApproach) || aircraft.radarAlt < 3f) continue;
                seen.Add(aircraft);
                float heading = ship.transform.eulerAngles.y;
                if (!headingAtTurnIn.TryGetValue(aircraft, out float then)) { headingAtTurnIn[aircraft] = heading; continue; }
                if (Mathf.Abs(Mathf.DeltaAngle(then, heading)) <= DeckSwing) continue;

                headingAtTurnIn.Remove(aircraft);
                seen.Remove(aircraft);
                Switch.Invoke(landing, new object[] { Enum.ToObject(Mode.FieldType, Aborting) });
                Flight flight = FlightOrders.Of(aircraft);
                string name = flight?.Name ?? aircraft.definition?.unitName ?? aircraft.name;
                Host.Say(name + " · waved off, " + ShipNames.Of(ship) + " turned · going round");
                Host.LogInfo("[deck] " + name + " waved off · " + ShipNames.Of(ship) + " swung " +
                    Mathf.Abs(Mathf.DeltaAngle(then, heading)).ToString("0") + "° during the approach");
                if (flight != null) wavedOff[flight] = Time.timeSinceLevelLoad;
            }
            var gone = new List<Aircraft>();
            foreach (Aircraft aircraft in headingAtTurnIn.Keys) if (!seen.Contains(aircraft)) gone.Add(aircraft);
            foreach (Aircraft aircraft in gone) headingAtTurnIn.Remove(aircraft);

            // Ours, clear of the abort and handed to the combat pilot: back into
            // our own return, which starts a fresh approach.
            var done = new List<Flight>();
            foreach (KeyValuePair<Flight, float> entry in wavedOff)
            {
                Flight flight = entry.Key;
                if (flight?.Aircraft == null || flight.Aircraft.disabled || Time.timeSinceLevelLoad - entry.Value > 120f) { done.Add(flight); continue; }
                if (flight.Mode != FlightMode.ReturnToBase) { done.Add(flight); continue; }
                Pilot pilot = FlightOrders.FirstPilot(flight.Aircraft);
                if (pilot?.currentState is AIPilotLandingState) continue;
                flight.Adopted = false;
                done.Add(flight);
            }
            foreach (Flight flight in done) wavedOff.Remove(flight);
        }
    }
}
