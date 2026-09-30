using System;
using UnityEngine;

namespace NOrders
{
    // What shared code needs from the mod it is compiled into. Each mod's
    // plugin sets these at startup; the defaults do nothing, so NOrders code
    // never reaches into a particular mod's UI or plugin.
    public static class Host
    {
        // A unit that is gone for targeting purposes: removed, destroyed and
        // still tumbling, or abandoned by its crew -- an aircraft whose pilot
        // has ejected is out of the fight, and a strike on one never ended.
        // A wreck in the air kept a wing's missiles.
        public static bool Dead(Unit unit) => unit == null || unit.disabled ||
            unit.unitState == Unit.UnitState.Destroyed || unit.unitState == Unit.UnitState.Abandoned || unit.unitState == Unit.UnitState.Returned;

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

        // What the game's unit strip shows as the pilot state of a flight
        // this mod is flying.
        public static string ModName = "Naval Power";

        // Whether kills by flights this mod adopts are credited to the local
        // player. True for a mod that launches flights on the player's behalf
        // (Naval Power); false for one that commands AI factions, whose kills
        // are nobody's. Either way, only a flight of the player's own faction
        // is ever credited.
        public static bool CreditKillsToPlayer = true;

        // Whether the local player directs this mod's units -- Naval Power:
        // yes; an AI commander such as High Command: no. Off, nothing is paid
        // to or charged to the player: no sortie bonus on recovery, and
        // launches are paid for the faction's way, not from the player's
        // allocation. (Kill credit has its own switch above.) Shipped without
        // this, High Command's recoveries paid the player sortie bonuses.
        public static bool PlayerDirected = false;

        // A waypoint to fly through on the way home, or null to go straight
        // there: the host mod knows where the enemy's missiles reach and can
        // route a returning flight round them. Every return goes through it,
        // including the ones this tree orders itself for fuel or ammunition.
        public static Func<Flight, GlobalPosition?> DoglegHome = _ => null;

        // Whether a hostile is one this flight should leave alone: the host
        // mod knows what sits under the enemy's missiles. A weapons-free
        // patrol that chased every hostile in range flew into a fleet's air
        // defences for a run's worth of losses.
        public static Func<Flight, Unit, bool> AvoidEngaging = (_, __) => false;

        // Whether this host commands a whole faction's ships as an AI (High
        // Command), rather than the local player's own. Ships of such a
        // faction can be ordered without the player's permission, and what
        // is bought for them is paid from the faction's funds.
        public static Func<FactionHQ, bool> CommandsFaction = _ => false;

        // A unit this mod owned is being handed to another mod at the player's
        // request (Ownership.ServiceHandovers): drop it from whatever the host
        // plans for it. NOrders' own state -- task force, route, components --
        // is cleared before this is called; ownership passes after it.
        public static Action<Unit> OnYield = _ => { };
    }
}
