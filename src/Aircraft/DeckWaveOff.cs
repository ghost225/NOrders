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
    //
    // Ahead of that, a flight of ours coming home to a ship that is turning
    // marshals overhead (NavalPilotState) until the deck has held its heading
    // for a while: the game's pattern entry sits three turning circles astern
    // of the deck, so a turning ship swings it sideways faster than the
    // aircraft can follow, and one was seen chasing it round for minutes. One
    // still joining the pattern when the ship starts to turn is taken back
    // into the marshal the same way.
    internal static class DeckWaveOff
    {
        private static readonly FieldInfo Mode = AccessTools.Field(typeof(AIPilotLandingState), "landingMode");
        private static readonly FieldInfo Base = AccessTools.Field(typeof(AIPilotLandingState), "airbase");
        private static readonly MethodInfo Switch = AccessTools.Method(typeof(AIPilotLandingState), "SwitchMode");

        private const int JoiningPattern = 0, TurningToFinal = 1, StabilizedApproach = 2, VerticalTouchdown = 3, TouchedDown = 4, Aborting = 5;
        private const float DeckSwing = 15f;

        // A deck is steady once its heading has stayed within a few degrees
        // for SteadyFor seconds; a swing past SteadyBand starts the clock again.
        internal const float SteadyFor = 15f;
        private const float SteadyBand = 5f;

        private sealed class Deck { internal float Heading, Since, Sampled; }
        private static readonly Dictionary<Ship, Deck> decks = new Dictionary<Ship, Deck>();

        // Seconds the ship has held its present heading. Sampled on demand and
        // from the sweep; a ship not looked at for a while starts afresh.
        internal static float SteadyTime(Ship ship)
        {
            if (ship == null) return 0f;
            float now = Time.timeSinceLevelLoad, heading = ship.transform.eulerAngles.y;
            if (!decks.TryGetValue(ship, out Deck deck) || now - deck.Sampled > 5f || now < deck.Sampled)
            {
                deck = new Deck { Heading = heading, Since = now };
                decks[ship] = deck;
            }
            deck.Sampled = now;
            if (Mathf.Abs(Mathf.DeltaAngle(deck.Heading, heading)) > SteadyBand) { deck.Heading = heading; deck.Since = now; }
            return now - deck.Since;
        }

        internal static bool Steady(Ship ship) => SteadyTime(ship) >= SteadyFor;

        // An aircraft other than `except` on the approach to this field --
        // turning to final, on final, touching down -- or null.
        internal static Aircraft OnApproach(Airbase airbase, Aircraft except)
        {
            if (Mode == null || Base == null || airbase == null) return null;
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Aircraft aircraft) || aircraft == except || aircraft.disabled) continue;
                if (!(FlightOrders.FirstPilot(aircraft)?.currentState is AIPilotLandingState landing)) continue;
                if (!ReferenceEquals(Base.GetValue(landing), airbase)) continue;
                int mode = Convert.ToInt32(Mode.GetValue(landing));
                if (mode == TurningToFinal || mode == StabilizedApproach || mode == VerticalTouchdown || mode == TouchedDown) return aircraft;
            }
            return null;
        }

        // On the ground at the end of a landing, rolling out.
        internal static bool Touchdown(AIPilotLandingState landing)
        {
            if (Mode == null || landing == null) return false;
            int mode = Convert.ToInt32(Mode.GetValue(landing));
            return mode == VerticalTouchdown || mode == TouchedDown;
        }

        // The game's landing phase, in words, for the status.
        internal static string Phase(AIPilotLandingState landing)
        {
            if (Mode == null || landing == null) return null;
            switch (Convert.ToInt32(Mode.GetValue(landing)))
            {
                case JoiningPattern: return "JOINING THE PATTERN";
                case TurningToFinal: return "TURNING FINAL";
                case StabilizedApproach: return "ON FINAL";
                case VerticalTouchdown: return "TOUCHING DOWN";
                case TouchedDown: return "TOUCHED DOWN";
                case Aborting: return "GOING ROUND";
            }
            return null;
        }

        private static readonly Dictionary<Aircraft, float> headingAtTurnIn = new Dictionary<Aircraft, float>();
        private static readonly Dictionary<Flight, float> wavedOff = new Dictionary<Flight, float>();
        private static float nextSweep;

        internal static void Tick()
        {
            if (Mode == null || Base == null || Switch == null || !MissionManager.IsRunning) return;
            if (Time.timeSinceLevelLoad < nextSweep) return;
            nextSweep = Time.timeSinceLevelLoad + 0.25f;

            // Keep the clock running on every deck one of ours is coming home to.
            foreach (Flight flight in FlightOrders.All())
                if (flight.Mode == FlightMode.ReturnToBase && flight.Parent != null && !flight.Parent.disabled) SteadyTime(flight.Parent);
            var stale = new List<Ship>();
            foreach (Ship ship in decks.Keys) if (ship == null || ship.disabled) stale.Add(ship);
            foreach (Ship ship in stale) decks.Remove(ship);

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
                // Still joining when the ship turns: ours goes back to the marshal.
                if (mode == JoiningPattern && SteadyTime(ship) < 1f)
                {
                    Flight ours = FlightOrders.Of(aircraft);
                    if (ours != null && ours.Mode == FlightMode.ReturnToBase && ours.Adopted && NavalPilotState.CanBeFlown(aircraft))
                    {
                        ours.Adopted = false;       // Tick reinstalls our state, which marshals
                        Host.LogInfo("[deck] " + ours.Name + " · " + ShipNames.Of(ship) + " turning · back to the marshal");
                    }
                    continue;
                }
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
