using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // The player's auxiliary axis, customAxis1, means something different on
    // each airframe. On jets whose engines carry a parasitic thrust loss
    // (several mod jets) it gates the afterburner, and no AI pilot ever moves
    // it, so we push it at full power. On a swivel-duct VTOL type (the EW-25
    // Medusa, the FS-20 Vortex) it is where the ducts point: 1 aft, 0 straight
    // down. An AI-flown one is in manual vectoring below 139 m/s, so the ducts
    // follow the axis directly -- and pushing it to 0 at cruise power put them
    // in hover on station. A heavy Medusa ordered to 600 m climbed on its jets
    // past 2,000 m whatever its nose did, and came apart coming back down.
    //
    // So: ducts held aft for wing-borne flight; the afterburner gate only where
    // an engine has the loss that needs it; anything else left alone (swing
    // wings, compound helicopters and tiltwings run the axis themselves).
    internal static class AuxAxis
    {
        private enum Use { Leave, Reheat, Ducts }

        private static readonly FieldInfo FanLoss = AccessTools.Field(typeof(Turbofan), "parasiticThrustLoss");
        private static readonly FieldInfo JetLoss = AccessTools.Field(typeof(Turbojet), "parasiticThrustLoss");
        private static readonly Dictionary<Aircraft, Use> uses = new Dictionary<Aircraft, Use>();
        private static readonly Dictionary<Aircraft, float> checkedAt = new Dictionary<Aircraft, float>();

        // For a fixed-wing aircraft flown by AI, at the throttle it has now.
        internal static void Apply(Aircraft aircraft, ControlInputs inputs)
        {
            if (aircraft == null || inputs == null) return;
            switch (UseOf(aircraft))
            {
                case Use.Ducts: inputs.customAxis1 = 1f; break;
                case Use.Reheat: inputs.customAxis1 = inputs.throttle >= 0.98f ? 1f : 0f; break;
            }
        }

        private static Use UseOf(Aircraft aircraft)
        {
            // Read again now and then: a mod may set the loss after spawn.
            float now = Time.timeSinceLevelLoad;
            if (uses.TryGetValue(aircraft, out Use use) && checkedAt.TryGetValue(aircraft, out float at) && now - at < 10f) return use;
            if (uses.Count > 300) { uses.Clear(); checkedAt.Clear(); }
            Use was = uses.TryGetValue(aircraft, out Use known) ? known : Use.Leave;
            use = Read(aircraft);
            if (use != Use.Leave && (!uses.ContainsKey(aircraft) || use != was))
                Host.LogInfo("[flight] " + (aircraft.definition?.unitName ?? aircraft.name) + " · aux axis " +
                    (use == Use.Ducts ? "points swivel ducts: held aft" : "gates the afterburner: pushed at full power"));
            uses[aircraft] = use;
            checkedAt[aircraft] = now;
            return use;
        }

        private static Use Read(Aircraft aircraft)
        {
            if (aircraft.GetComponentInChildren<SwivelDuctSystem>(true) != null) return Use.Ducts;
            if (aircraft.GetComponentInChildren<SwingWingController>(true) != null ||
                aircraft.GetComponentInChildren<CompoundHeloController>(true) != null ||
                aircraft.GetComponentInChildren<TiltWingController>(true) != null) return Use.Leave;
            foreach (Turbofan fan in aircraft.GetComponentsInChildren<Turbofan>(true))
                if (FanLoss?.GetValue(fan) is float loss && loss > 0f) return Use.Reheat;
            foreach (Turbojet jet in aircraft.GetComponentsInChildren<Turbojet>(true))
                if (JetLoss?.GetValue(jet) is float loss && loss > 0f) return Use.Reheat;
            return Use.Leave;
        }
    }
}
