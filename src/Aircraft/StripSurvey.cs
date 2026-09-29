using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using UnityEngine;

namespace NOrders
{
    // Survey for rolling deliveries (phase 1): read-only.
    //
    // A rolling delivery lands a fixed-wing transport on open ground, slows
    // it to a walking-pace roll with brakes and reverse thrust, lets the
    // cargo roll out of the ramp -- the game spawns it at the aircraft's own
    // speed -- and takes off again from the rest of the ground. Before any of
    // that is flown, this finds where it could be: strips of flat, clear land
    // near a point, long enough for this aircraft's float, stop, roll-out and
    // take-off, with clear air over both ends. The map draws what it found,
    // and the log says what was tried, what ground was turned down and why,
    // and what the aircraft brings (reverser, blown flaps) -- so the numbers
    // can be checked in game before anything lands.
    internal static class StripSurvey
    {
        internal sealed class Strip
        {
            internal GlobalPosition Touchdown, Deploy, End;
            internal Vector3 Direction;
            internal float Heading, Slope, Cross, Rough, Headroom, FromPoint, Score;
        }

        internal sealed class Result
        {
            internal Flight Flight;
            internal GlobalPosition Point;
            internal float Radius, Needed, Float, Stop, Roll, Takeoff;
            internal readonly List<Strip> Usable = new List<Strip>();
            internal Strip Best => Usable.Count > 0 ? Usable[0] : null;
            internal readonly Dictionary<string, int> Rejected = new Dictionary<string, int>();
            internal int Tried;
            internal float Milliseconds;
        }

        // The last survey, for the map to draw.
        internal static Result Last;

        private const float Search = 1000f;          // deploy point within this of the click
        private const float Step = 20f;              // along-strip sample spacing
        private const float HalfWidth = 22f;         // wingtip clearance either side
        private const float RollOutSpeed = 8f;       // m/s the cargo leaves at
        private const float Deceleration = 2.5f;     // m/s², brakes and reverser on rough ground
        private const float DeploySeconds = 10f;     // ramp open, rail run, clear of the tail
        private const float FloatDistance = 120f;    // flare to touchdown
        private const float Approach = 1500f, Glide = 3f, Climb = 4f, Clearance = 12f;
        private const float MaxSlope = 0.04f, MaxStep = 0.08f, MaxCross = 0.06f, MaxRough = 2f;

        internal static Result Survey(Flight flight, GlobalPosition point)
        {
            var watch = Stopwatch.StartNew();
            Aircraft aircraft = flight?.Aircraft;
            AircraftParameters p = aircraft != null ? aircraft.GetAircraftParameters() : null;
            var result = new Result { Flight = flight, Point = point, Radius = Search };
            float landing = p != null && p.landingSpeed > 0f ? p.landingSpeed : 45f;
            float takeoffSpeed = p != null && p.takeoffSpeed > 0f ? p.takeoffSpeed : 60f;
            float takeoffDistance = p != null && p.takeoffDistance > 0f ? p.takeoffDistance : 800f;
            result.Float = FloatDistance;
            result.Stop = Mathf.Max(0f, (landing * landing - RollOutSpeed * RollOutSpeed) / (2f * Deceleration));
            result.Roll = RollOutSpeed * DeploySeconds;
            // Rolling already, and lighter by the cargo: the part of the
            // take-off run above roll-out speed, with a fifth in hand.
            float fraction = 1f - Mathf.Clamp01(RollOutSpeed / takeoffSpeed) * Mathf.Clamp01(RollOutSpeed / takeoffSpeed);
            result.Takeoff = takeoffDistance * fraction * 1.2f;
            result.Needed = result.Float + result.Stop + result.Roll + result.Takeoff;

            float before = result.Float + result.Stop;       // touchdown to deploy
            float after = result.Roll + result.Takeoff;       // deploy to lift-off
            var centres = new List<Vector3> { Vector3.zero };
            for (float r = 250f; r <= Search + 1f; r += 250f)
            {
                int n = Mathf.RoundToInt(2f * Mathf.PI * r / 250f);
                for (int i = 0; i < n; i++)
                {
                    float a = i * 2f * Mathf.PI / n;
                    centres.Add(new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a)) * r);
                }
            }
            Vector3 origin = point.ToLocalPosition();
            foreach (Vector3 offset in centres)
            {
                for (float heading = 0f; heading < 360f; heading += 15f)
                {
                    result.Tried++;
                    Vector3 dir = Quaternion.Euler(0f, heading, 0f) * Vector3.forward;
                    Vector3 deploy = origin + offset;
                    string why = Assess(deploy - dir * before, dir, result.Needed, out Strip strip);
                    if (why != null)
                    {
                        result.Rejected.TryGetValue(why, out int count);
                        result.Rejected[why] = count + 1;
                        continue;
                    }
                    strip.Heading = heading;
                    strip.Deploy = (deploy.WithY(strip.Touchdown.ToLocalPosition().y)).ToGlobalPosition();
                    strip.FromPoint = offset.magnitude;
                    // Near the point, flat, smooth, with air to spare.
                    strip.Score = strip.FromPoint / Search + strip.Slope / MaxSlope * 0.5f + strip.Cross / MaxCross * 0.3f +
                        strip.Rough / MaxRough * 0.5f - Mathf.Min(strip.Headroom, 60f) / 60f * 0.3f;
                    result.Usable.Add(strip);
                }
            }
            result.Usable.Sort((a, b) => a.Score.CompareTo(b.Score));
            watch.Stop();
            result.Milliseconds = (float)watch.Elapsed.TotalMilliseconds;
            Last = result;
            Host.LogInfo(Describe(result, aircraft, p));
            return result;
        }

        private static Vector3 WithY(this Vector3 v, float y) { v.y = y; return v; }

        // One strip from its touchdown point along dir. Null when usable.
        private static string Assess(Vector3 touchdown, Vector3 dir, float length, out Strip strip)
        {
            strip = null;
            Vector3 side = new Vector3(dir.z, 0f, -dir.x) * HalfWidth;
            int samples = Mathf.CeilToInt(length / Step);
            var heights = new float[samples + 1];
            float previous = float.NaN;
            // The centreline first and alone: most ground fails here, cheaply.
            for (int i = 0; i <= samples; i++)
            {
                Vector3 at = touchdown + dir * (i * Step);
                string why = Ground(at, out float h);
                if (why != null) return why;
                if (!float.IsNaN(previous) && Mathf.Abs(h - previous) / Step > MaxStep) return "a bump or ditch";
                heights[i] = previous = h;
            }
            float slope = (heights[samples] - heights[0]) / (samples * Step);
            if (Mathf.Abs(slope) > MaxSlope) return "sloped along its length";
            float rough = 0f;
            for (int i = 0; i <= samples; i++)
                rough = Mathf.Max(rough, Mathf.Abs(heights[i] - (heights[0] + slope * i * Step)));
            if (rough > MaxRough) return "uneven";

            // Wingtips: clear and not much higher or lower than the centre.
            float cross = 0f;
            for (int i = 0; i <= samples; i += 2)
            {
                Vector3 at = touchdown + dir * (i * Step);
                foreach (Vector3 tip in new[] { at + side, at - side })
                {
                    string why = Ground(tip, out float h);
                    if (why != null) return why == "water" ? "water under a wingtip" : why == "a building or unit" ? "an obstacle by a wingtip" : why;
                    cross = Mathf.Max(cross, Mathf.Abs(h - heights[i]) / HalfWidth);
                }
            }
            if (cross > MaxCross) return "sloped across";

            // Clear air: under a glide path in, under a climb out.
            float headroom = float.MaxValue;
            float td = heights[0], lift = heights[samples];
            for (float d = 100f; d <= Approach; d += 100f)
            {
                float top = Top(touchdown - dir * d);
                headroom = Mathf.Min(headroom, td + d * Mathf.Tan(Glide * Mathf.Deg2Rad) - top);
                if (headroom < Clearance) return "the approach is blocked";
            }
            Vector3 end = touchdown + dir * length;
            for (float d = 100f; d <= Approach; d += 100f)
            {
                float top = Top(end + dir * d);
                headroom = Mathf.Min(headroom, lift + d * Mathf.Tan(Climb * Mathf.Deg2Rad) - top);
                if (headroom < Clearance) return "the climb-out is blocked";
            }
            strip = new Strip
            {
                Touchdown = touchdown.WithY(td).ToGlobalPosition(),
                End = end.WithY(lift).ToGlobalPosition(),
                Direction = dir,
                Slope = Mathf.Abs(slope),
                Cross = cross,
                Rough = rough,
                Headroom = headroom,
            };
            return null;
        }

        private static readonly Dictionary<string, int> surfaces = new Dictionary<string, int>();

        // The ground under a point: its height, or why it cannot be rolled on.
        private static string Ground(Vector3 at, out float height)
        {
            height = Datum.LocalSeaY;
            if (!Physics.Linecast(at.WithY(Datum.LocalSeaY + 3000f), at.WithY(Datum.LocalSeaY - 50f), out RaycastHit hit, PhysicsLayers.StaticsMask))
                return "no ground found";
            height = hit.point.y;
            if (hit.point.y <= Datum.LocalSeaY + 0.5f) return "water";
            if (hit.collider != null && hit.collider.GetComponentInParent<Unit>() != null) return "a building or unit";
            string kind = hit.collider != null ? hit.collider.GetType().Name : "none";
            surfaces.TryGetValue(kind, out int n);
            surfaces[kind] = n + 1;
            if (hit.normal.y < Mathf.Cos(10f * Mathf.Deg2Rad)) return "steep ground";
            return null;
        }

        // Highest thing under a point, buildings included.
        private static float Top(Vector3 at)
        {
            return Physics.Linecast(at.WithY(Datum.LocalSeaY + 3000f), at.WithY(Datum.LocalSeaY - 50f), out RaycastHit hit, PhysicsLayers.StaticsMask)
                ? Mathf.Max(hit.point.y, Datum.LocalSeaY) : Datum.LocalSeaY;
        }

        private static string Describe(Result r, Aircraft aircraft, AircraftParameters p)
        {
            var s = new StringBuilder("[strip] survey for ");
            s.Append(r.Flight?.Name ?? "?").Append(" (").Append(aircraft?.definition?.unitName ?? "?").Append(")");
            s.Append(" at ").Append(Named(r.Point));
            s.Append("\n    needs ").Append(r.Needed.ToString("0")).Append(" m: float ").Append(r.Float.ToString("0"))
                .Append(" + stop ").Append(r.Stop.ToString("0")).Append(" + roll-out ").Append(r.Roll.ToString("0"))
                .Append(" + take-off ").Append(r.Takeoff.ToString("0"));
            if (p != null)
                s.Append("\n    aircraft: landing ").Append(p.landingSpeed.ToString("0")).Append(" m/s · short landing ")
                    .Append(p.shortLandingSpeed.ToString("0")).Append(" · approach ").Append(p.approachSpeed.ToString("0"))
                    .Append(" · take-off ").Append(p.takeoffSpeed.ToString("0")).Append(" m/s over ").Append(p.takeoffDistance.ToString("0"))
                    .Append(" m · vertical landing ").Append(p.verticalLanding);
            if (aircraft != null)
            {
                s.Append("\n    fitted: ").Append(Has(aircraft, "AryxThrustReverser") ? "thrust reverser" : "no reverser")
                    .Append(" · ").Append(Has(aircraft, "BlownWingController") ? "blown flaps" : "no blown flaps")
                    .Append(" · mass ").Append(aircraft.GetMass().ToString("0")).Append(" kg · cargo: ").Append(Cargo(aircraft));
            }
            s.Append("\n    ").Append(r.Tried).Append(" strips tried in ").Append(r.Milliseconds.ToString("0")).Append(" ms · ")
                .Append(r.Usable.Count).Append(" usable");
            for (int i = 0; i < Mathf.Min(5, r.Usable.Count); i++)
            {
                Strip x = r.Usable[i];
                s.Append("\n    ").Append(i == 0 ? "best" : "  #" + (i + 1)).Append(" · heading ").Append(x.Heading.ToString("000"))
                    .Append("° · deploy ").Append(x.FromPoint.ToString("0")).Append(" m from the point · slope ")
                    .Append((x.Slope * 100f).ToString("0.0")).Append("% · across ").Append((x.Cross * 100f).ToString("0.0"))
                    .Append("% · uneven ").Append(x.Rough.ToString("0.0")).Append(" m · air clear by ").Append(x.Headroom.ToString("0"))
                    .Append(" m · touchdown at ").Append((x.Touchdown.ToLocalPosition().y - Datum.LocalSeaY).ToString("0")).Append(" m ASL");
            }
            var reasons = new List<KeyValuePair<string, int>>(r.Rejected);
            reasons.Sort((a, b) => b.Value.CompareTo(a.Value));
            s.Append("\n    turned down:");
            foreach (KeyValuePair<string, int> reason in reasons) s.Append(" ").Append(reason.Key).Append(" ").Append(reason.Value).Append(" ·");
            s.Append("\n    ground hit by type:");
            foreach (KeyValuePair<string, int> kind in surfaces) s.Append(" ").Append(kind.Key).Append(" ").Append(kind.Value);
            surfaces.Clear();
            return s.ToString();
        }

        private static string Named(GlobalPosition point)
        {
            Vector3 v = point.ToLocalPosition();
            return "(" + (point.x / 1000f).ToString("0.0") + ", " + (point.z / 1000f).ToString("0.0") + ") km, ground " +
                (Top(v) - Datum.LocalSeaY).ToString("0") + " m ASL";
        }

        private static bool Has(Aircraft aircraft, string type)
        {
            foreach (MonoBehaviour behaviour in aircraft.GetComponentsInChildren<MonoBehaviour>(true))
                if (behaviour != null && behaviour.GetType().Name == type) return true;
            return false;
        }

        private static string Cargo(Aircraft aircraft)
        {
            var names = new List<string>();
            if (aircraft.weaponStations != null)
                foreach (WeaponStation station in aircraft.weaponStations)
                    if (station?.WeaponInfo != null && (station.Cargo || station.WeaponInfo.cargo) && station.Ammo > 0)
                        names.Add(station.WeaponInfo.weaponName + " ×" + station.Ammo);
            return names.Count > 0 ? string.Join(", ", names) : "none";
        }

        internal static void Clear() => Last = null;
    }
}
