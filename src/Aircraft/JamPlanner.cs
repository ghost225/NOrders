using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // Where a jamming flight should work from. Its own task area is used if
    // the pods reach every target from all of its orbit; the ideal area is as
    // far back on our side as still does that. Shared by the pilot, which
    // moves an area that does not work, and the interface, which shows the
    // ideal one and offers to move there.
    public static class JamPlanner
    {
        private const float ReachMargin = 0.95f;

        public static List<Unit> Targets(Flight flight)
        {
            var targets = new List<Unit>();
            if (flight == null) return targets;
            foreach (Unit unit in flight.JamTargets) if (unit != null && !unit.disabled) targets.Add(unit);
            if (targets.Count == 0 && flight.Target != null && !flight.Target.disabled && flight.Mode == FlightMode.Jam)
                targets.Add(flight.Target);
            return targets;
        }

        public static float Reach(Flight flight)
        {
            float reach = MissileJamming.Reach(flight?.Aircraft);
            return reach > 0f ? reach : Mathf.Max(Tuning.JammingStandoff, 1000f);
        }

        // Every target within the pods' reach from every point on the area's orbit.
        public static bool Covers(Vector3 centre, float radius, List<Unit> targets, float reach)
        {
            foreach (Unit unit in targets)
            {
                Vector3 d = unit.transform.position - centre;
                d.y = 0f;
                if (d.magnitude + radius > reach * ReachMargin) return false;
            }
            return true;
        }

        public static bool CurrentCovers(Flight flight) =>
            Covers(flight.OrbitCentre.ToLocalPosition(), flight.OrbitRadius, Targets(flight), Reach(flight));

        // As far back on our side of the targets as keeps every one of them in
        // reach from all of the orbit; the lowest priority dropped from the
        // reckoning if they cannot all be kept. How many it covers comes back.
        public static int Ideal(Flight flight, out Vector3 station, out float radius)
        {
            station = flight?.Aircraft != null ? flight.Aircraft.transform.position : Vector3.zero;
            float reach = Reach(flight);
            radius = Mathf.Clamp(reach * 0.12f, 1500f, 5000f);
            List<Unit> targets = Targets(flight);
            if (targets.Count == 0) return 0;
            for (int keep = targets.Count; keep >= 1; keep--)
            {
                Vector3 centre = Vector3.zero;
                for (int i = 0; i < keep; i++) centre += targets[i].transform.position;
                centre /= keep;
                float spread = 0f;
                for (int i = 0; i < keep; i++)
                {
                    Vector3 d = targets[i].transform.position - centre;
                    d.y = 0f;
                    spread = Mathf.Max(spread, d.magnitude);
                }
                float back = reach * ReachMargin - spread - radius;
                if (back < 1000f && keep > 1) continue;
                Vector3 own = flight.Aircraft != null ? flight.Aircraft.transform.position : centre;
                Vector3 friendly = (flight.Home != null ? flight.HomePosition.ToLocalPosition() : own) - centre;
                friendly.y = 0f;
                if (friendly.sqrMagnitude < 1f) friendly = own - centre;
                friendly.y = 0f;
                if (friendly.sqrMagnitude < 1f) friendly = Vector3.back;
                station = centre + friendly.normalized * Mathf.Max(back, 1000f);
                return keep;
            }
            return 0;
        }

        // The flight's area to the ideal one.
        public static bool MoveToIdeal(Flight flight)
        {
            if (Ideal(flight, out Vector3 station, out float radius) == 0) return false;
            flight.OrbitCentre = station.ToGlobalPosition();
            flight.OrbitRadius = radius;
            return true;
        }
    }
}
