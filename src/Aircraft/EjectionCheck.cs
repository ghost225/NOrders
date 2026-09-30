using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // The game's combat pilot ejects when the aircraft is beyond saving:
    // flying backward (tumbling), a good part of it gone, or down and
    // stopped. Our own pilot state never ran that check, so an aircraft
    // under our orders that lost a wing fell all the way in pieces with its
    // crew aboard. The same rule, for every flight of ours flown by its AI,
    // once a second.
    internal static class EjectionCheck
    {
        private static readonly Dictionary<Aircraft, float> nextCheck = new Dictionary<Aircraft, float>();
        internal static int Ejected;

        internal static void Tick()
        {
            float now = Time.timeSinceLevelLoad;
            foreach (Flight flight in FlightOrders.All())
            {
                Aircraft aircraft = flight?.Aircraft;
                if (aircraft == null || aircraft.disabled || !aircraft.IsServer || Host.IsFlownByPlayer(flight)) continue;
                if (nextCheck.TryGetValue(aircraft, out float at) && now < at) continue;
                nextCheck[aircraft] = now + 1f;
                if (!Doomed(aircraft, out string why)) continue;
                Tracing.Flight("[flight] " + flight.Name + " · " + why + " · crew ejecting");
                aircraft.StartEjectionSequence();
                Ejected++;
            }
            if (nextCheck.Count > 500) nextCheck.Clear();
        }

        private static bool Doomed(Aircraft aircraft, out string why)
        {
            why = null;
            try
            {
                if (aircraft.rb == null) return false;
                if (aircraft.cockpit != null && aircraft.cockpit.IsDetached()) { why = "cockpit gone"; return true; }
                if (aircraft.radarAlt > 40f)
                {
                    // Flying backward is tumbling for an aeroplane; a helicopter
                    // backs up and slides sideways as a matter of course, and a
                    // slow aeroplane in a hard turn can read backward for a frame.
                    Vector3 forward = aircraft.cockpit != null && aircraft.cockpit.xform != null ? aircraft.cockpit.xform.forward : aircraft.transform.forward;
                    bool rotary = FlightOrders.IsRotary(FlightOrders.FirstPilot(aircraft));
                    if (!rotary && aircraft.rb.velocity.sqrMagnitude > 900f && Vector3.Dot(forward.normalized, aircraft.rb.velocity.normalized) < -0.3f) { why = "tumbling"; return true; }
                    if (aircraft.partDamageTracker != null && aircraft.partDamageTracker.GetDetachedRatio() > 0.12f) { why = "airframe breaking up"; return true; }
                }
                if (aircraft.transform.position.y < Datum.LocalSeaY) { why = "in the water"; return true; }
            }
            catch { return false; }
            return false;
        }
    }
}
