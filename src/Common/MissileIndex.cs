using System.Collections.Generic;
using UnityEngine;

namespace NOrders
{
    // Every missile in the air, scanned once a frame and indexed by what it is
    // after and who fired it. Threat assessment (per flight), jamming cover,
    // self-protection ECM, shot discipline, the crank, laser and escort
    // defence, ship engagement and the map each walked every unit in the
    // game, several times a second and some of them per flight or per target.
    // Now the first of them each frame pays for one walk and the rest read the
    // result. A missile fired later in the same frame shows up the next one.
    // Callers still check `disabled`: a missile can die between the scan and
    // the read.
    internal static class MissileIndex
    {
        private static int frame = -1;
        private static readonly List<Missile> all = new List<Missile>();
        private static readonly Dictionary<PersistentID, List<Missile>> byTarget = new Dictionary<PersistentID, List<Missile>>();
        private static readonly Dictionary<Unit, List<Missile>> byOwner = new Dictionary<Unit, List<Missile>>();
        private static readonly Stack<List<Missile>> spare = new Stack<List<Missile>>();
        private static readonly List<Missile> none = new List<Missile>();

        // Every live missile.
        internal static List<Missile> All { get { Refresh(); return all; } }

        // Missiles whose target is this unit.
        internal static List<Missile> At(Unit target)
        {
            if (target == null) return none;
            Refresh();
            return byTarget.TryGetValue(target.persistentID, out List<Missile> list) ? list : none;
        }

        // Missiles this unit fired.
        internal static List<Missile> From(Unit owner)
        {
            if (owner == null) return none;
            Refresh();
            return byOwner.TryGetValue(owner, out List<Missile> list) ? list : none;
        }

        private static void Refresh()
        {
            if (Time.frameCount == frame) return;
            frame = Time.frameCount;
            all.Clear();
            foreach (List<Missile> list in byTarget.Values) { list.Clear(); spare.Push(list); }
            foreach (List<Missile> list in byOwner.Values) { list.Clear(); spare.Push(list); }
            byTarget.Clear();
            byOwner.Clear();
            foreach (Unit unit in UnitRegistry.allUnits)
            {
                if (!(unit is Missile missile) || missile.disabled) continue;
                all.Add(missile);
                if (missile.targetID.IsValid) Add(byTarget, missile.targetID, missile);
                if (missile.owner != null) Add(byOwner, missile.owner, missile);
            }
        }

        private static void Add<TKey>(Dictionary<TKey, List<Missile>> index, TKey key, Missile missile)
        {
            if (!index.TryGetValue(key, out List<Missile> list))
            {
                list = spare.Count > 0 ? spare.Pop() : new List<Missile>();
                index[key] = list;
            }
            list.Add(missile);
        }
    }
}
