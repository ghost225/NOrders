using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // An orderly recovery: one aircraft on the approach at a time.
    //
    // The game hands every aircraft coming home straight to its own landing,
    // and that only notices traffic on final: the second of two on the same
    // deck was waved off at the last moment, inside a few hundred metres of
    // the one ahead. So our fixed-wing flights coming home queue for their
    // field. Within MarshalRange of it each joins a marshal stack overhead in
    // the order it arrived -- the first at MarshalBase, each after it
    // MarshalStep higher; astern of a ship, overhead a land field -- and only
    // the head of the queue is cleared to the game's approach. The next is cleared once the one ahead has landed and
    // had a few seconds to clear the deck or runway, or has gone round. A
    // ship turning holds the queue (DeckWaveOff); any other aircraft on the
    // approach to that field, ours or not, holds it too. One low on fuel goes
    // to the head of the queue, and one nearly dry is cleared regardless.
    // Helicopters land on their own pads and are left out.
    internal static class RecoveryQueue
    {
        internal const float MarshalRange = 14000f, MarshalBase = 600f, MarshalStep = 150f, MarshalRadius = 2500f;
        // At sea the stack holds astern of the ship: released from overhead,
        // high and close, the approach had no room -- jets dived short into
        // the stern or arrived fast and long. From astern, the one cleared
        // goes straight to the game's approach.
        internal const float MarshalAstern = 7000f;
        private const float DeckClearSeconds = 8f, ClearedAtMost = 300f, LowFuel = 0.1f, Dry = 0.04f;

        private sealed class Field
        {
            internal readonly List<Flight> Waiting = new List<Flight>();
            internal Flight Cleared;
            internal float ClearedAt, FreeAt;
        }

        private static readonly Dictionary<Airbase, Field> fields = new Dictionary<Airbase, Field>();

        // Asked each frame by a flight within marshal range of its field. True
        // when it may begin its approach; otherwise its place in the stack
        // (1 is next) and why it is waiting.
        internal static bool Cleared(Flight flight, Airbase airbase, out int place, out string why)
        {
            place = 0; why = null;
            if (!fields.TryGetValue(airbase, out Field field)) fields[airbase] = field = new Field();
            Ship ship = Airfields.ShipOf(airbase);
            bool turning = ship != null && !ship.disabled && Ownership.Acts(ship) && !DeckWaveOff.Steady(ship);
            float fuel = flight.Aircraft.GetFuelLevel();
            if (fuel < Dry) { Clear(field, flight); return true; }

            if (field.Cleared == flight)
            {
                if (!turning) return true;
                why = ShipNames.Of(ship) + " turning";
                return false;
            }
            if (!field.Waiting.Contains(flight))
            {
                if (fuel < LowFuel) field.Waiting.Insert(0, flight); else field.Waiting.Add(flight);
                Host.LogInfo("[recovery] " + flight.Name + " · in the marshal for " + Airfields.NameOf(airbase) + " · #" + (field.Waiting.IndexOf(flight) + 1));
            }
            else if (fuel < LowFuel && field.Waiting[0] != flight)
            {
                field.Waiting.Remove(flight);
                field.Waiting.Insert(0, flight);
            }

            int index = field.Waiting.IndexOf(flight);
            string busy = Busy(field, airbase, turning, ship);
            if (busy == null && index == 0)
            {
                field.Waiting.RemoveAt(0);
                Clear(field, flight);
                return true;
            }
            place = index + 1;
            why = busy ?? (index > 0 ? index + " ahead" : null);
            return false;
        }

        private static void Clear(Field field, Flight flight)
        {
            field.Waiting.Remove(flight);
            field.Cleared = flight;
            field.ClearedAt = Time.timeSinceLevelLoad;
            Host.LogInfo("[recovery] " + flight.Name + " · cleared to land" + (field.Waiting.Count > 0 ? " · " + field.Waiting.Count + " in the marshal" : ""));
        }

        // What holds the approach, or null when it is free.
        private static string Busy(Field field, Airbase airbase, bool turning, Ship ship)
        {
            if (turning) return ShipNames.Of(ship) + " turning";
            if (field.Cleared != null) return field.Cleared.Name + " landing";
            if (Time.timeSinceLevelLoad < field.FreeAt) return "deck clearing";
            Aircraft other = DeckWaveOff.OnApproach(airbase, null);
            if (other != null) return (FlightOrders.Of(other)?.Name ?? other.definition?.unitName ?? "traffic") + " on the approach";
            return null;
        }

        // The place a flight holds in the stack, for its height. 0 when it is
        // not queued.
        internal static int PlaceOf(Flight flight)
        {
            foreach (Field field in fields.Values)
            {
                int i = field.Waiting.IndexOf(flight);
                if (i >= 0) return i + 1;
            }
            return 0;
        }

        // Every frame: drop the gone and the retasked, and release the
        // approach once the cleared aircraft has landed, gone round or gone.
        internal static void Tick()
        {
            if (fields.Count == 0) return;
            float now = Time.timeSinceLevelLoad;
            var stale = new List<Airbase>();
            foreach (KeyValuePair<Airbase, Field> entry in fields)
            {
                Field field = entry.Value;
                if (entry.Key == null || entry.Key.disabled) { stale.Add(entry.Key); continue; }
                field.Waiting.RemoveAll(f => f?.Aircraft == null || f.Aircraft.disabled || f.Mode != FlightMode.ReturnToBase);
                Flight cleared = field.Cleared;
                if (cleared == null) continue;
                string done = null;
                PilotBaseState state = cleared.Aircraft != null && !cleared.Aircraft.disabled ? FlightOrders.FirstPilot(cleared.Aircraft)?.currentState : null;
                if (cleared.Aircraft == null || cleared.Aircraft.disabled) done = "gone";
                else if (cleared.Mode != FlightMode.ReturnToBase) done = "retasked";
                else if (state is AIPilotTaxiState || state is PilotParkedState || state == null) done = "down";
                else if (state is AIPilotLandingState landing && DeckWaveOff.Touchdown(landing)) done = null;   // rolling out: wait for the taxi
                else if (!(state is AIPilotLandingState) && !(state is NavalPilotState) && now - field.ClearedAt > 20f) done = "went round";
                else if (now - field.ClearedAt > ClearedAtMost) done = "timed out";
                if (done == null) continue;
                field.Cleared = null;
                field.FreeAt = done == "down" ? now + DeckClearSeconds : now;
                Host.LogInfo("[recovery] " + cleared.Name + " · " + done + " · " + Airfields.NameOf(entry.Key) + " approach free" +
                    (done == "down" ? " in " + DeckClearSeconds.ToString("0") + " s" : ""));
            }
            foreach (Airbase airbase in stale) fields.Remove(airbase);
        }
    }
}
