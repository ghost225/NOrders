using System;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // Take off the way it is lined up.
    //
    // A runway keeps the direction it was last used in for thirty seconds,
    // and the take-off state asks for the direction afresh as it starts. With
    // other traffic landing the other way in that window, one of ours lined
    // up at the threshold was handed the reverse direction, turned round, and
    // ran for the near end with next to no runway ahead: an EW-25 got away
    // with it on its nozzles, a conventional jet would not. Lined up along a
    // runway with most of it ahead, the runway is set to that direction first.
    [HarmonyPatch(typeof(AIPilotTakeoffState), nameof(AIPilotTakeoffState.EnterState))]
    internal static class TakeoffDirectionPatch
    {
        private const string Name = "Take-off direction";

        private static void Prefix(Pilot pilot)
        {
            if (!Guard.Ok(Name)) return;
            try { Hold(pilot); }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        private static void Hold(Pilot pilot)
        {
            Aircraft aircraft = pilot?.aircraft;
            if (aircraft == null || pilot.playerControlled || FlightOrders.Of(aircraft) == null || aircraft.NetworkHQ == null) return;
            Airbase airbase = aircraft.NetworkHQ.GetNearestAirbase(aircraft.transform.position);
            if (airbase == null || airbase.runways == null) return;
            Vector3 nose = aircraft.transform.forward;
            nose.y = 0f;
            if (nose.sqrMagnitude < 0.01f) return;
            nose.Normalize();
            foreach (Airbase.Runway runway in airbase.runways)
            {
                if (runway == null || !runway.Takeoff || runway.Start == null || runway.End == null) continue;
                Vector3 along = runway.End.position - runway.Start.position;
                along.y = 0f;
                float length = along.magnitude;
                if (length < 1f) continue;
                along /= length;
                // On this runway: close to its centreline, between its ends.
                Vector3 fromStart = aircraft.transform.position - runway.Start.position;
                fromStart.y = 0f;
                float at = Vector3.Dot(fromStart, along);
                float off = (fromStart - along * at).magnitude;
                if (at < -60f || at > length + 60f || off > 40f) continue;
                float facing = Vector3.Dot(nose, along);
                if (Mathf.Abs(facing) < Mathf.Cos(30f * Mathf.Deg2Rad)) continue;     // not lined up along it
                bool reverse = facing < 0f;
                if (reverse && !runway.Reversable) return;
                float ahead = reverse ? at : length - at;
                if (ahead < length * 0.6f) return;                                  // not at a threshold
                runway.SetUsageDirection(reverse);
                return;
            }
        }
    }
}
