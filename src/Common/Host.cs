using System;
using UnityEngine;

namespace NOrders
{
    // What shared code needs from the mod it is compiled into. Each mod's
    // plugin sets these at startup; the defaults do nothing, so NOrders code
    // never reaches into a particular mod's UI or plugin.
    public static class Host
    {
        public static Action<string> LogInfo = _ => { };
        public static Action<string> LogWarning = _ => { };
        public static Action<string> LogError = _ => { };

        // An on-screen message for the player, where the mod has somewhere to
        // show one.
        public static Action<string> Say = _ => { };

        // The ship or airbase the player is commanding in this mod, if any.
        public static Func<Ship> CommandedShip = () => null;
        public static Func<Airbase> CommandedBase = () => null;

        // Whether the player is at the controls of this flight right now.
        public static Func<Flight, bool> IsFlownByPlayer = _ => false;

        // Stamped on units this mod owns (see Ownership).
        public static string ModId = "NOrders";
    }
}
