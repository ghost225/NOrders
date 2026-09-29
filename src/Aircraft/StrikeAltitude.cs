using System;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // A missile strike from one of our flights keeps its height.
    //
    // Once the run-in hands a strike to the native combat pilot, its missile
    // attack steers for the target's own position -- a ship at sea level --
    // to bring it into the missile's launch cone, and its height target
    // shrinks 10 m a check with nothing to climb for, and 50 m for each radar
    // lock warning: terrain masking, right for a low strike jet, wrong for a
    // standoff shooter. An EW-25 went from 4 km to 900 m to launch and lost
    // its track on the ship over the horizon.
    //
    // So while the native pilot has one of our strike flights on a missile,
    // and it is not evading a missile of its own, the height of the point it
    // steers for is held at the flight's ordered altitude. It still flies at
    // the target, level. Only where holding height would put the target
    // outside the missile's launch cone -- close in and far below -- is the
    // descent left alone, so the shot can still be taken.
    [HarmonyPatch(typeof(AutopilotPlane), nameof(AutopilotPlane.AutoAim),
        new[] { typeof(GlobalPosition), typeof(bool), typeof(bool), typeof(bool), typeof(float), typeof(float), typeof(bool), typeof(float), typeof(Vector3) })]
    internal static class StrikeAltitudePatch
    {
        private const string Name = "Missile strike altitude";

        private static void Prefix(AutopilotPlane __instance, ref GlobalPosition destination)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = __instance.aircraft;
                Flight flight = aircraft != null ? FlightOrders.Of(aircraft) : null;
                if (flight == null || flight.Mode != FlightMode.Strike || flight.Threat == FlightThreat.Missile) return;
                if (Host.IsFlownByPlayer(flight)) return;
                Pilot pilot = FlightOrders.FirstPilot(aircraft);
                if (pilot == null || !(pilot.currentState is AIPilotCombatModes)) return;
                WeaponInfo info = aircraft.weaponManager?.currentWeaponStation?.WeaponInfo;
                if (info == null || !info.missile || info.bomb) return;

                GlobalPosition here = aircraft.GlobalPosition();
                float ground = here.y - aircraft.radarAlt;
                float hold = ground + Mathf.Max(flight.Altitude, Tuning.MinimumClearance);
                if (destination.y >= hold) return;

                // Close in and well below, level flight would leave the target
                // outside the launch cone: then the dive is needed.
                Unit target = flight.Target;
                if (target != null && !target.disabled)
                {
                    Vector3 to = target.GlobalPosition() - here;
                    float across = new Vector3(to.x, 0f, to.z).magnitude;
                    float below = hold - target.GlobalPosition().y;
                    float cone = info.targetRequirements.minAlignment > 0f ? info.targetRequirements.minAlignment : 30f;
                    if (Mathf.Atan2(below, Mathf.Max(across, 1f)) * Mathf.Rad2Deg > cone * 0.8f) return;
                }
                destination.y = hold;
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
