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
    //
    // And a warning already sounding at the hand-over is replayed to it: the
    // alert handlers start an evasion only from the warning event, which
    // fires once, when the missile is first detected. A shot detected while
    // our state had the aircraft, and handed over for that very shot, was
    // never evaded -- an attack helicopter flew on at its target, straight at
    // the missile. The helicopter pilot (AIHeloCombatState) re-subscribes
    // itself on every entry, so it needs only the replay.
    public static class NativePilot
    {
        private static readonly MethodInfo Alert = AccessTools.Method(typeof(AIPilotCombatModes), "AICombat_OnMissileAlert");
        private static readonly MethodInfo HeloAlert = AccessTools.Method(typeof(AIHeloCombatState), "AIHeloCombatState_OnMissileAlert");

        public static void Wake(PilotBaseState state, Aircraft aircraft)
        {
            if (aircraft == null) return;
            MissileWarning warning = aircraft.GetMissileWarningSystem();
            if (warning == null) return;
            try
            {
                MethodInfo method = state is AIPilotCombatModes ? Alert : state is AIHeloCombatState ? HeloAlert : null;
                if (method == null) return;
                var handler = (Action<MissileWarning.OnMissileWarning>)Delegate.CreateDelegate(
                    typeof(Action<MissileWarning.OnMissileWarning>), state, method);
                if (state is AIPilotCombatModes)
                {
                    warning.onMissileWarning -= handler;                 // never twice
                    warning.onMissileWarning += handler;
                }
                if (warning.IsWarning() && warning.TryGetNearestIncoming(out Missile incoming) && incoming != null)
                    handler(new MissileWarning.OnMissileWarning { missile = incoming });
            }
            catch (Exception ex) { Host.LogWarning("[flight] could not wake the native pilot's missile warning: " + ex.Message); }
        }
    }
}
