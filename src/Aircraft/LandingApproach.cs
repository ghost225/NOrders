using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // A helicopter or tiltwing landing at a field comes straight at its
    // touchdown from wherever it is, and the game's landing state does not
    // look at what stands in the way: Tarantulas coming in from the side of
    // an airbase flew into its hangars and towers on a smooth, slow approach.
    //
    // So before the landing is handed over -- going home, or delivering
    // cargo onto a field -- the final three kilometres of the straight-in
    // approach are swept for structures. If anything stands in it, the
    // aircraft is routed out to two gates on a line that is clear (a
    // runway's axis first, being the one line a field keeps open, then the
    // compass round from the direct bearing) and handed the landing from
    // the inner gate, so its last few kilometres come in along that line.
    internal static class LandingApproach
    {
        private const float FinalLength = 3000f;   // the stretch swept
        private const float InnerOut = 3500f;      // inner gate, from the touchdown
        private const float OuterOut = 7000f;      // outer gate
        private const float SweepRadius = 25f;     // a tiltwing's half-span and a margin
        private const float GateHeight = 250f;     // over the ground on the gate legs
        private const float FieldReach = 2500f;    // a touchdown this near a field is "at" it

        internal static bool Vtol(Aircraft aircraft) =>
            aircraft != null && (aircraft.autopilot is AutopilotHelo || aircraft.autopilot is AutopilotTiltwing);

        // ---- the sweep -----------------------------------------------------------------------
        // Is the final approach coming in from direction `outward` (flat, unit,
        // pointing from the touchdown back toward the approach) clear of
        // anything built? Terrain is the autopilot's own business; aircraft and
        // vehicles move. Anything else static in the way is a structure.
        internal static bool Clear(Vector3 touchdown, Vector3 outward, out string what)
        {
            what = null;
            Vector3 start = touchdown + outward * FinalLength;
            start.y = GroundAt(start) + 200f;
            // Stopped short of the touchdown itself: a pad beside a hangar is
            // reached by the last few hundred metres of the landing state's own
            // descent, and it is the long run-in that hits things.
            Vector3 end = touchdown + outward * 150f + Vector3.up * 30f;
            Vector3 line = end - start;
            float length = line.magnitude;
            if (length < 1f) return true;
            RaycastHit[] hits = Physics.SphereCastAll(start, SweepRadius, line / length, length, PhysicsLayers.StaticsMask, QueryTriggerInteraction.Ignore);
            float nearest = float.MaxValue;
            foreach (RaycastHit hit in hits)
            {
                Collider c = hit.collider;
                if (c == null) continue;
                if (GameAssets.i != null && c.sharedMaterial == GameAssets.i.terrainMaterial) continue;
                Unit unit = c.GetComponentInParent<Unit>();
                if (unit is Aircraft || unit is GroundVehicle) continue;
                if (hit.distance < nearest)
                {
                    nearest = hit.distance;
                    what = unit != null ? (unit.definition?.unitName ?? unit.name) : c.gameObject.name;
                }
            }
            return what == null;
        }

        private static float GroundAt(Vector3 p)
        {
            if (Physics.Raycast(new Vector3(p.x, p.y + 5000f, p.z), Vector3.down, out RaycastHit hit, 10000f, PhysicsLayers.StaticsMask))
                return Mathf.Max(hit.point.y, Datum.LocalSeaY);
            return Datum.LocalSeaY;
        }

        // ---- choosing the line ---------------------------------------------------------------
        // Gates for an approach to `touchdown` at `field` from `from`; false when
        // the direct approach is clear, or the field is a ship.
        internal static bool Gates(Airbase field, GlobalPosition from, GlobalPosition touchdown, out GlobalPosition outer, out GlobalPosition inner, out string why)
        {
            outer = inner = default; why = null;
            if (field == null || Airfields.ShipOf(field) != null) return false;
            Vector3 t = touchdown.ToLocalPosition();
            Vector3 direct = from.ToLocalPosition() - t; direct.y = 0f;
            if (direct.magnitude < InnerOut) return false;                 // already in close: nothing to route round
            direct.Normalize();
            if (Clear(t, direct, out string blocker)) return false;

            var candidates = new List<(Vector3 dir, string name, bool runway)>();
            if (field.runways != null)
                foreach (Airbase.Runway runway in field.runways)
                {
                    if (runway?.Start == null || runway.End == null) continue;
                    Vector3 axis = runway.End.position - runway.Start.position; axis.y = 0f;
                    if (axis.sqrMagnitude < 1f) continue;
                    axis.Normalize();
                    candidates.Add((axis, "along the runway", true));
                    candidates.Add((-axis, "along the runway", true));
                }
            for (int i = 1; i < 12; i++)
            {
                Vector3 d = Quaternion.AngleAxis(i * 30f, Vector3.up) * direct;
                candidates.Add((d, null, false));
            }
            // Runway axes first, then the compass; each group nearest the
            // direct bearing first, so the gates sit on the aircraft's side.
            candidates.Sort((a, b) => a.runway != b.runway ? (a.runway ? -1 : 1) : Vector3.Angle(a.dir, direct).CompareTo(Vector3.Angle(b.dir, direct)));
            Vector3 chosen = Vector3.zero; string how = null;
            foreach ((Vector3 dir, string name, bool _) in candidates)
            {
                if (!Clear(t, dir, out _)) continue;
                chosen = dir; how = name ?? "from " + Bearing(dir).ToString("000") + "°";
                break;
            }
            if (how == null)
            {
                // Nothing clear: the runway axis nearest the aircraft, which is at
                // least the line the field was built to keep open.
                foreach ((Vector3 dir, string name, bool runway) in candidates) if (runway) { chosen = dir; how = "along the runway (nothing found clear)"; break; }
                if (how == null) return false;
            }
            inner = (t + chosen * InnerOut).ToGlobalPosition();
            outer = (t + chosen * OuterOut).ToGlobalPosition();
            why = "straight in blocked by " + blocker + "; in " + how;
            return true;
        }

        private static float Bearing(Vector3 dir) => (Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg + 360f) % 360f;   // the heading flown inbound

        // The field a touchdown is at, if any (any side's: a captured field is still a field).
        private static Airbase[] fields = new Airbase[0];
        private static float fieldsAt = -1000f;
        internal static Airbase FieldAt(GlobalPosition at)
        {
            if (Time.timeSinceLevelLoad - fieldsAt > 60f || fieldsAt > Time.timeSinceLevelLoad) { fieldsAt = Time.timeSinceLevelLoad; fields = Object.FindObjectsOfType<Airbase>(); }
            Airbase best = null; float bd = float.MaxValue;
            foreach (Airbase a in fields)
            {
                if (a == null || a.disabled || Airfields.ShipOf(a) != null) continue;
                float d = FastMath.Distance(Airfields.PositionOf(a), at);
                if (d < Mathf.Max(a.GetRadius() + 1000f, FieldReach) && d < bd) { bd = d; best = a; }
            }
            return best;
        }

        // Where a VTOL landing at its home field will put down: the vertical
        // landing point nearest it, else the field's centre.
        private static GlobalPosition HomeTouchdown(Airbase field, Aircraft aircraft)
        {
            GlobalPosition best = Airfields.PositionOf(field); float bd = float.MaxValue;
            if (field.verticalLandingPoints != null)
                foreach (Airbase.VerticalLandingPoint p in field.verticalLandingPoints)
                {
                    if (p?.point == null) continue;
                    float d = Vector3.Distance(p.point.position, aircraft.transform.position);
                    if (d < bd) { bd = d; best = p.point.position.ToGlobalPosition(); }
                }
            return best;
        }

        // ---- going home --------------------------------------------------------------------
        internal static bool GateHome(Flight flight)
        {
            Aircraft aircraft = flight?.Aircraft;
            if (!Vtol(aircraft) || flight.Home == null || Host.IsFlownByPlayer(flight)) return false;
            GlobalPosition touchdown = HomeTouchdown(flight.Home, aircraft);
            if (!Gates(flight.Home, aircraft.GlobalPosition(), touchdown, out GlobalPosition outer, out GlobalPosition inner, out string why)) return false;
            FlightOrders.SetRoute(flight, outer, false);
            FlightOrders.SetRoute(flight, inner, true);
            flight.Altitude = GateHeight;
            flight.HomingVia = inner;
            flight.HomingGated = true;
            Host.LogInfo("[flight] " + flight.Name + " · home to " + Airfields.NameOf(flight.Home) + " by gates · " + why);
            return true;
        }

        // ---- delivering onto a field -----------------------------------------------------------
        internal static bool GateCargo(Flight flight, GlobalPosition where, bool airdrop)
        {
            Aircraft aircraft = flight?.Aircraft;
            if (!Vtol(aircraft) || Host.IsFlownByPlayer(flight)) return false;
            Airbase field = FieldAt(where);
            if (field == null) return false;
            if (!Gates(field, aircraft.GlobalPosition(), where, out GlobalPosition outer, out GlobalPosition inner, out string why)) return false;
            FlightOrders.SetRoute(flight, outer, false);
            FlightOrders.SetRoute(flight, inner, true);
            flight.Altitude = GateHeight;
            flight.CargoGate = inner;
            flight.CargoGateWhere = where;
            flight.CargoGateAirdrop = airdrop;
            Host.LogInfo("[flight] " + flight.Name + " · delivery onto " + Airfields.NameOf(field) + " by gates · " + why);
            return true;
        }
    }
}
