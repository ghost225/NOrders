using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // A G limit on the final pitch command, for every fixed-wing of ours flown
    // by AI -- our own state or the game's combat pilot. The game has a G
    // limiter class but nothing calls it; the only limit that runs is the
    // fly-by-wire's pitch-rate cap, and only on airframes that have it on.
    // Four FS-41s lost their cockpits in one sortie at 420-580 m/s, one in a
    // steady orbit porpoising a few hundred metres either way.
    //
    // The same scheme as the game's own limiter: normal load from the
    // cockpit's acceleration, looked ahead a little at its rate of change,
    // and the pitch command scaled back while over. The limit is the
    // fly-by-wire's own where it has one, never above SafeG.
    [HarmonyPatch(typeof(Aircraft), nameof(Aircraft.FilterInputs))]
    internal static class GLimitPatch
    {
        private const string Name = "G limit";
        internal const float SafeG = 7f;
        private const float Prediction = 0.3f;     // seconds looked ahead

        private sealed class State
        {
            internal float Prev, Rate, RateVel, Strength;
            internal bool Started;
            internal float Peak, PeakAt;
            internal float Limit = -1f;
            internal float Cut, At = -1f;
        }

        private static readonly Dictionary<Aircraft, State> states = new Dictionary<Aircraft, State>();
        private static readonly FieldInfo FbwEnabled = AccessTools.Field(typeof(ControlsFilter.FlyByWire), "Enabled");
        private static readonly FieldInfo FbwLimit = AccessTools.Field(typeof(ControlsFilter.FlyByWire), "gLimitPositive");

        // Before the controls filter: the stick itself is scaled, so a
        // fly-by-wire asks for a gentler pitch rate rather than winding its
        // own loop up against a cut made after it -- after it, as first
        // written, the load still ran to 9-14 g in a soak. A proportional
        // term bites the moment the predicted load passes 90% of the limit;
        // the integral holds it there through a sustained turn.
        private static void Prefix(Aircraft __instance)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                Aircraft aircraft = __instance;
                if (aircraft == null || !(aircraft.autopilot is AutopilotPlane) || !aircraft.LocalSim) return;
                Flight flight = FlightOrders.Of(aircraft);
                if (flight == null || Host.IsFlownByPlayer(flight)) return;
                ControlInputs inputs = aircraft.GetInputs();
                if (inputs == null) return;
                if (!states.TryGetValue(aircraft, out State state))
                {
                    if (states.Count > 200) states.Clear();
                    states[aircraft] = state = new State();
                }
                if (state.Limit < 0f) state.Limit = LimitFor(aircraft);

                float g = Vector3.Dot(aircraft.accel + Vector3.up, aircraft.transform.up);
                float now = Time.timeSinceLevelLoad;
                if (Mathf.Abs(g) > state.Peak || now - state.PeakAt > 5f) { state.Peak = Mathf.Abs(g); state.PeakAt = now; }
                if (!state.Started) { state.Started = true; state.Prev = g; return; }
                state.Rate = FastMath.SmoothDamp(state.Rate, (g - state.Prev) / Time.fixedDeltaTime, ref state.RateVel, 0.05f);
                state.Prev = g;

                float predicted = Mathf.Max(Mathf.Abs(g), Mathf.Abs(g + state.Rate * Prediction));
                float over = predicted - state.Limit * 0.9f;
                if (over > 0f) state.Strength += over * 1.5f * Time.fixedDeltaTime;
                else state.Strength -= 0.4f * Time.fixedDeltaTime;
                state.Strength = Mathf.Clamp01(state.Strength);
                float cut = Mathf.Clamp01(state.Strength + Mathf.Max(over, 0f) / Mathf.Max(state.Limit * 0.3f, 0.5f));
                if (cut > 0f) inputs.pitch *= 1f - cut;
                state.Cut = cut;
                state.At = Time.fixedTime;
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        // And a backstop after it: well over the limit, whatever the filter
        // made of the stick, the elevator is eased as well.
        private static void Postfix(Aircraft __instance)
        {
            if (!Guard.Ok(Name)) return;
            try
            {
                if (__instance == null || !states.TryGetValue(__instance, out State state) || state.Limit <= 0f) return;
                if (state.At != Time.fixedTime || Mathf.Abs(state.Prev) < state.Limit) return;
                ControlInputs inputs = __instance.GetInputs();
                if (inputs != null) inputs.pitch *= 1f - Mathf.Clamp01((Mathf.Abs(state.Prev) - state.Limit) / 2f);
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }

        private static float LimitFor(Aircraft aircraft)
        {
            float limit = SafeG;
            ControlsFilter filter = aircraft.GetControlsFilter();
            ControlsFilter.FlyByWire fbw = filter != null ? filter.GetFlyByWire() : null;
            bool on = fbw != null && FbwEnabled?.GetValue(fbw) is bool enabled && enabled;
            if (on && FbwLimit?.GetValue(fbw) is float fbwLimit && fbwLimit > 1f) limit = Mathf.Min(limit, fbwLimit);
            Host.LogInfo("[flight] " + (aircraft.definition?.unitName ?? aircraft.name) + " · G limit " + limit.ToString("0.0") +
                " · fly-by-wire " + (on ? "on, its limit " + (FbwLimit?.GetValue(fbw) is float f ? f.ToString("0.0") : "?") : "off or absent"));
            return limit;
        }

        // The worst load over the last few seconds, for the trace.
        internal static float Peak(Aircraft aircraft) =>
            aircraft != null && states.TryGetValue(aircraft, out State state) ? state.Peak : 0f;
    }
}
