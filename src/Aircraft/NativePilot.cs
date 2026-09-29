using System;
using System.Reflection;
using HarmonyLib;

namespace NOrders
{
    // Handing an aircraft back to the game's combat pilot. AIPilotCombatModes
    // subscribes to the missile warning system only in its constructor and
    // unsubscribes in LeaveState, so once our state has had the aircraft and
    // given it back, the native pilot no longer hears missile warnings: no
    // reaction time is set, no flares go out, no evasion starts. Every flight
    // that had been through our hands died to the first heat-seeker. This
    // re-subscribes on every hand-over.
    public static class NativePilot
    {
        private static readonly MethodInfo Alert = AccessTools.Method(typeof(AIPilotCombatModes), "AICombat_OnMissileAlert");

        public static void Wake(PilotBaseState state, Aircraft aircraft)
        {
            if (!(state is AIPilotCombatModes combat) || aircraft == null || Alert == null) return;
            MissileWarning warning = aircraft.GetMissileWarningSystem();
            if (warning == null) return;
            try
            {
                var handler = (Action<MissileWarning.OnMissileWarning>)Delegate.CreateDelegate(
                    typeof(Action<MissileWarning.OnMissileWarning>), combat, Alert);
                warning.onMissileWarning -= handler;                 // never twice
                warning.onMissileWarning += handler;
            }
            catch (Exception ex) { Host.LogWarning("[flight] could not wake the native pilot's missile warning: " + ex.Message); }
        }
    }
}
