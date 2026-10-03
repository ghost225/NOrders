using System;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // A compound helicopter's engines feed the main rotor, the pusher fan and
    // the anti-torque fan through one transmission, and when they ask for more
    // than the engines make, the transmission cuts every one of them by the
    // same fraction -- the rotor gets no priority. Full pusher at speed
    // starves the rotor; it sags below governed speed, and the game's
    // autopilot answers that by taking the collective away (one point of
    // collective per rotor RPM short, so ten RPM low is no collective at all)
    // and only then setting the pusher neutral. Low over the sea that was
    // fatal for a string of Ibises.
    //
    // So for our AI helicopters the rotor comes first: as it drops from 99%
    // toward the 97.5% the autopilot starts cutting collective at, forward
    // pusher eases off toward neutral, before the autopilot has to.
    // After the input filter, so whatever flew this step (our state, the
    // game's transport or combat state) has had its say.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.FilterInputs))]
    internal static class RotorFirstPatch
    {
        private const string Name = "Rotor first";

        internal static float RotorRatio(Aircraft aircraft)
        {
            if (aircraft?.engines == null) return -1f;
            float sum = 0f; int n = 0;
            foreach (IEngine engine in aircraft.engines)
                if (engine is RotorShaft shaft) { sum += shaft.GetRPMRatio(); n++; }
            return n > 0 ? sum / n : -1f;
        }

        private static void Postfix(Aircraft __instance)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = __instance;
                if (aircraft == null || !(aircraft.autopilot is AutopilotHelo) || !aircraft.LocalSim || aircraft.radarAlt < 3f) return;
                ControlInputs inputs = aircraft.GetInputs();
                if (inputs == null || inputs.customAxis1 <= 0.5f) return;
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || Host.IsFlownByPlayer(flight)) return;
                float ratio = RotorRatio(aircraft);
                if (ratio < 0f) return;
                float starve = Mathf.InverseLerp(0.99f, 0.975f, ratio);
                if (starve > 0f) inputs.customAxis1 = Mathf.Lerp(inputs.customAxis1, 0.5f, starve);
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
