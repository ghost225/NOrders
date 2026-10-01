using System;
using System.Collections;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace NOrders
{
    // A radar that is off stops turning.
    //
    // The game spins a radar's antennas every frame in Radar.Update whether
    // the radar is radiating or not, so a ship gone EMCON silent still had
    // every array turning. On a ship, a radar switched off (EMCON, or one
    // sensor turned off) now holds its antennas where they are; switched back
    // on, they turn again. The frame's turn is taken back after the game's
    // own update, which leaves the rest of it -- the jamming decay -- alone.
    // A fix to the game's behaviour, so Ownership.Acts on the ship.
    [HarmonyPatch(typeof(Radar), "Update")]
    internal static class RadarSpinPatch
    {
        private const string Name = "Radar spin";
        private static readonly FieldInfo Rotators = AccessTools.Field(typeof(TargetDetector), "rotators");
        private static readonly FieldInfo Attached = AccessTools.Field(typeof(TargetDetector), "attachedUnit");
        private static FieldInfo rotTransform, rotAxis;

        private static bool Prepare() => Rotators != null && Attached != null;

        private static void Postfix(Radar __instance)
        {
            if (__instance == null || __instance.activated || !Guard.Ok(Name)) return;
            try
            {
                if (!(Attached.GetValue(__instance) is Ship ship) || !Ownership.Acts(ship)) return;
                if (!(Rotators.GetValue(__instance) is IList rotators) || rotators.Count == 0) return;
                foreach (object rotator in rotators)
                {
                    if (rotator == null) continue;
                    if (rotTransform == null)
                    {
                        rotTransform = AccessTools.Field(rotator.GetType(), "transform");
                        rotAxis = AccessTools.Field(rotator.GetType(), "axis");
                        if (rotTransform == null || rotAxis == null) return;
                    }
                    if (!(rotTransform.GetValue(rotator) is Transform spun)) continue;
                    Vector3 axis = (Vector3)rotAxis.GetValue(rotator);
                    spun.localEulerAngles -= axis * Time.deltaTime;
                }
            }
            catch (Exception ex) { Guard.Failed(Name, ex); }
        }
    }
}
