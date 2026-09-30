using NuclearOption.Networking;
using UnityEngine;

namespace NOrders
{
    // The capability gate. Naval Power commands any ship the native game drives
    // with a ShipAI and a UnitCommand; there is no hull whitelist. ShipAI
    // subclasses (AssaultCarrierAI, LandingCraftAI, third-party ship AI) match
    // here polymorphically, which is why orders are issued through UnitCommand
    // rather than through a patch on a specific Steer body.
    internal static class CommandableShip
    {
        internal static bool Is(Unit unit) =>
            unit is Ship ship && ship.GetComponent<ShipAI>() != null && ship.UnitCommand != null &&
            ship.GetComponent<LandingCraftAI>() == null && !Ownership.Theirs(ship);

        // Our claim on a ship lasts as long as it is under our control.
        internal static void ReleaseIfIdle(Ship ship)
        {
            if (ship != null && !Controlled(ship)) Ownership.Release(ship);
        }

        // Commandable but for another mod holding it: one the player can ask
        // for (Ownership.RequestHandover).
        internal static bool Held(Unit unit) =>
            unit != null && Ownership.Theirs(unit) && CanCommand(unit, out _, ignoreOwnership: true);

        internal static bool CanCommand(Unit unit, out string reason) => CanCommand(unit, out reason, false);

        internal static bool CanCommand(Unit unit, out string reason, bool ignoreOwnership)
        {
            reason = null;
            var ship = unit as Ship;
            if (ship == null) { reason = "Only ships can be commanded."; return false; }
            if (ship.GetComponent<ShipAI>() == null || ship.UnitCommand == null)
            { reason = "This hull has no native navigation controller."; return false; }
            // A landing craft runs its own landing -- beach, unload, home --
            // and taking the helm mid-run leaves it unable to finish. It is
            // sent and recalled from its carrier's Amphibious window.
            if (ship.GetComponent<LandingCraftAI>() != null)
            { reason = "Landing craft are run from their carrier's Amphibious window."; return false; }
            if (!ignoreOwnership && Ownership.Theirs(ship))
            { reason = "Another mod (" + Ownership.OwnerOf(ship) + ") is commanding this ship."; return false; }
            if (!MissionManager.IsRunning || ship.disabled || !ship.gameObject.activeInHierarchy)
            { reason = "This ship is not available in a running mission."; return false; }
            bool factionCommand = false;
            try { factionCommand = ship.NetworkHQ != null && Host.CommandsFaction(ship.NetworkHQ); } catch { }
            if (!factionCommand)
            {
                if (!GameManager.GetLocalPlayer<Player>(out var player) || player == null)
                { reason = "A local player is required."; return false; }
                if (!HasPermission(ship, player))
                { reason = player.HQ != null ? "You can command only your own faction's ships." : "Command authority is required."; return false; }
            }
            // Orders mutate simulation state, so they belong to whoever owns it.
            if (!ship.IsServer || !ship.LocalSim)
            { reason = "Ship commands require the mission host."; return false; }
            return true;
        }

        // Under Naval Power's control: the ship being commanded, a ship in one
        // of our task forces, or one we are steering or firing for. Behaviour
        // changes to the game's own AI apply to these only, so another mod
        // running the same faction's ships is left alone.
        internal static bool Controlled(Ship ship)
        {
            if (ship == null) return false;
            if (ship == Host.CommandedShip() || TaskForces.Of(ship) != null) return true;
            ShipRoute route = ship.GetComponent<ShipRoute>();
            if (route != null && (route.OwnsNavigation || route.HasSpeedOrder)) return true;
            return ship.GetComponent<ShipWeapons>() != null || ship.GetComponent<ShipEngagement>() != null;
        }

        internal static bool HasPermission(Ship ship, Player player)
        {
            if (ship == null || player == null) return false;
            return player.HQ != null ? player.HQ == ship.NetworkHQ : player.HasAuthority;
        }

        // Ship definitions record top speed in km/h; every order in this mod is
        // in knots, matching the native naval UI.
        internal const float MetresPerSecondPerKnot = 1852f / 3600f;

        internal static float MaximumSpeedKnots(Ship ship)
        {
            float top = (ship != null ? ship.definition as ShipDefinition : null)?.shipInfo?.topSpeed ?? 0f;
            // Definition top speed is a nominal figure; hulls are observed doing
            // a little better, and clamping to it would make "ahead flank"
            // quietly slow the ship down.
            return Finite(top) && top > 0f ? top / 1.852f * 1.05f : 30f;
        }

        internal static bool Finite(float value) => !float.IsNaN(value) && !float.IsInfinity(value);
        internal static bool Finite(GlobalPosition p) => Finite(p.x) && Finite(p.y) && Finite(p.z);
    }
}
