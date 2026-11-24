using JXReborn.Primitives.Items;
using JXReborn.TestBase.WireCaptures;
using JXReborn.WireParityProxy.ShadowStates;

namespace JXReborn.WireParityProxy.Translation;

/// <summary>
/// Resolves dynamic entity IDs (<c>SelfNpcId</c>, <c>NpcId</c>, <c>ItemId</c>, and bag grid coordinates)
/// from Primary (<c>legacy-linux</c>) runtime space into Shadow (<c>reborn-server</c>) runtime space.
/// </summary>
public sealed class EntityIdTranslator
{
    private const double MaxNpcMatchDistanceMps = 256.0;
    private const double MaxMonsterPatrolMatchDistanceMps = 1024.0;

    private readonly ProxyShadowClientState _primary;
    private readonly ProxyShadowClientState _shadow;

    public EntityIdTranslator(ProxyShadowClientState primary, ProxyShadowClientState shadow)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(shadow);
        _primary = primary;
        _shadow = shadow;
    }

    public bool TryTranslateNpcId(uint primaryNpcId, out uint shadowNpcId)
    {
        if (primaryNpcId == 0)
        {
            shadowNpcId = 0;
            return false;
        }

        if (primaryNpcId == _primary.SelfNpcId && _shadow.SelfNpcId != 0)
        {
            shadowNpcId = _shadow.SelfNpcId;
            return true;
        }

        IReadOnlyDictionary<uint, VirtualJxNpcEntity> primaryNpcs = _primary.SnapshotNpcs();
        IReadOnlyDictionary<uint, VirtualJxNpcEntity> shadowNpcs = _shadow.SnapshotNpcs();

        if (!primaryNpcs.TryGetValue(primaryNpcId, out VirtualJxNpcEntity? primaryNpc))
        {
            shadowNpcId = 0;
            return false;
        }

        VirtualJxNpcEntity? best = FindClosestMatchingNpc(
            shadowNpcs.Values,
            primaryNpc,
            static (p, c) =>
                (p.TemplateId > 0 && c.TemplateId == p.TemplateId) ||
                (!string.IsNullOrWhiteSpace(p.NameUnicode) &&
                 string.Equals(c.NameUnicode.Trim(), p.NameUnicode.Trim(), StringComparison.Ordinal)));

        if (best is null && primaryNpc.TemplateId == 0 && string.IsNullOrEmpty(primaryNpc.NameUnicode))
        {
            best = FindClosestMatchingNpc(
                shadowNpcs.Values,
                primaryNpc,
                static (p, c) => c.Camp == p.Camp)
                ?? FindClosestMatchingNpc(
                    shadowNpcs.Values,
                    primaryNpc,
                    static (_, _) => true,
                    maxDistanceMps: 64.0);
        }
        else if (best is null)
        {
            best = FindClosestMatchingNpc(
                shadowNpcs.Values,
                primaryNpc,
                static (_, c) => c.TemplateId == 0 && string.IsNullOrEmpty(c.NameUnicode),
                maxDistanceMps: 64.0);
        }

        // Wandering combat monsters (Camp != 6) patrol independently on Primary vs Shadow (> 256 mps apart).
        if (best is null && (primaryNpc.Kind == JXReborn.Primitives.Characters.EntityKind.Monster || primaryNpc.Camp != 6))
        {
            best = FindClosestMatchingNpc(
                shadowNpcs.Values,
                primaryNpc,
                static (p, c) =>
                    c.LifePercent128 > 0 &&
                    ((p.TemplateId > 0 && c.TemplateId == p.TemplateId) ||
                     (!string.IsNullOrWhiteSpace(p.NameUnicode) &&
                      string.Equals(c.NameUnicode.Trim(), p.NameUnicode.Trim(), StringComparison.Ordinal)) ||
                     (c.Camp == p.Camp && c.Camp != 6)),
                maxDistanceMps: MaxMonsterPatrolMatchDistanceMps);
        }

        if (best is not null)
        {
            shadowNpcId = best.NpcId;
            return true;
        }

        shadowNpcId = 0;
        return false;
    }

    public bool TryTranslateObjId(int primaryObjId, out int shadowObjId, bool isMouseClick = false)
    {
        if (primaryObjId <= 0)
        {
            shadowObjId = 0;
            return false;
        }

        IReadOnlyDictionary<int, ShadowGroundObject> primaryObjs = _primary.SnapshotGroundObjects();
        IReadOnlyDictionary<int, ShadowGroundObject> shadowObjs = _shadow.SnapshotGroundObjects();

        if (shadowObjs.Count == 0)
        {
            shadowObjId = 0;
            return false;
        }

        bool hasPrimaryObj = primaryObjs.TryGetValue(primaryObjId, out ShadowGroundObject primaryObj);
        if (!hasPrimaryObj && !isMouseClick)
        {
            shadowObjId = 0;
            return false;
        }

        if (hasPrimaryObj && primaryObj.HasCoords)
        {
            ShadowGroundObject? bestByCoord = null;
            double bestDist = double.MaxValue;
            foreach (ShadowGroundObject candidate in shadowObjs.Values)
            {
                if (!candidate.HasCoords)
                {
                    continue;
                }

                long dx = (long)candidate.MpsX - primaryObj.MpsX;
                long dy = (long)candidate.MpsY - primaryObj.MpsY;
                double dist = Math.Sqrt((dx * dx) + (dy * dy));
                if (dist <= MaxNpcMatchDistanceMps && dist < bestDist)
                {
                    bestDist = dist;
                    bestByCoord = candidate;
                }
            }

            if (bestByCoord is { } matched)
            {
                shadowObjId = matched.ObjId;
                return true;
            }
        }
        else if (isMouseClick)
        {
            // On c2s_objmouseclick (0x72), the player must stand within 201 mps of the clicked static object.
            // Primary either emits 0x65 (HasCoords=false) or emits 0 packets (default State==0 from Region_C.dat),
            // whereas Shadow emits 0x64 (HasCoords=true) with exact MpsX/MpsY.
            byte expectedState = hasPrimaryObj ? primaryObj.State : (byte)0;
            int refX = _shadow.MpsX != 0 || _shadow.MpsY != 0 ? _shadow.MpsX : _primary.MpsX;
            int refY = _shadow.MpsX != 0 || _shadow.MpsY != 0 ? _shadow.MpsY : _primary.MpsY;
            if (refX != 0 || refY != 0)
            {
                ShadowGroundObject? bestNearPlayer = null;
                double bestDist = double.MaxValue;
                foreach (ShadowGroundObject candidate in shadowObjs.Values)
                {
                    if (!candidate.HasCoords || candidate.State != expectedState)
                    {
                        continue;
                    }

                    long dx = (long)candidate.MpsX - refX;
                    long dy = (long)candidate.MpsY - refY;
                    double dist = Math.Sqrt((dx * dx) + (dy * dy));
                    if (dist <= MaxNpcMatchDistanceMps && dist < bestDist)
                    {
                        bestDist = dist;
                        bestNearPlayer = candidate;
                    }
                }

                if (bestNearPlayer is { } nearMatched)
                {
                    shadowObjId = nearMatched.ObjId;
                    return true;
                }
            }
        }

        if (!hasPrimaryObj)
        {
            shadowObjId = 0;
            return false;
        }

        List<ShadowGroundObject> orderedPrimary = primaryObjs.Values.OrderBy(static o => o.FirstSeenOrder).ToList();
        List<ShadowGroundObject> orderedShadow = shadowObjs.Values.OrderBy(static o => o.FirstSeenOrder).ToList();

        if (!primaryObj.HasCoords)
        {
            List<ShadowGroundObject> staticPrimary = orderedPrimary.Where(o => !o.HasCoords && o.State == primaryObj.State).ToList();
            List<ShadowGroundObject> staticShadow = orderedShadow.Where(o => !o.HasCoords && o.State == primaryObj.State).ToList();
            int staticIdx = staticPrimary.FindIndex(o => o.ObjId == primaryObjId);
            if (staticIdx >= 0 && staticIdx < staticShadow.Count)
            {
                shadowObjId = staticShadow[staticIdx].ObjId;
                return true;
            }
        }

        List<ShadowGroundObject> sameStatePrimary = orderedPrimary.Where(o => o.State == primaryObj.State).ToList();
        List<ShadowGroundObject> sameStateShadow = orderedShadow.Where(o => o.State == primaryObj.State).ToList();
        int stateIdx = sameStatePrimary.FindIndex(o => o.ObjId == primaryObjId);
        if (stateIdx >= 0 && stateIdx < sameStateShadow.Count)
        {
            shadowObjId = sameStateShadow[stateIdx].ObjId;
            return true;
        }

        int fullIdx = orderedPrimary.FindIndex(o => o.ObjId == primaryObjId);
        if (fullIdx >= 0 && fullIdx < orderedShadow.Count)
        {
            shadowObjId = orderedShadow[fullIdx].ObjId;
            return true;
        }

        shadowObjId = 0;
        return false;
    }

    private static VirtualJxNpcEntity? FindClosestMatchingNpc(
        IEnumerable<VirtualJxNpcEntity> candidates,
        VirtualJxNpcEntity primaryNpc,
        Func<VirtualJxNpcEntity, VirtualJxNpcEntity, bool> predicate,
        double maxDistanceMps = MaxNpcMatchDistanceMps)
    {
        VirtualJxNpcEntity? best = null;
        double bestDist = double.MaxValue;

        foreach (VirtualJxNpcEntity candidate in candidates)
        {
            if (!predicate(primaryNpc, candidate))
            {
                continue;
            }

            long dx = (long)candidate.MpsX - primaryNpc.MpsX;
            long dy = (long)candidate.MpsY - primaryNpc.MpsY;
            double dist = Math.Sqrt((dx * dx) + (dy * dy));
            if (dist <= maxDistanceMps && dist < bestDist)
            {
                bestDist = dist;
                best = candidate;
            }
        }

        return best;
    }

    public bool TryTranslateItemByIdOrCell(
        uint primaryItemId,
        ItemContainerPlace primaryPlace,
        byte primaryGridX,
        byte primaryGridY,
        out VirtualJxItemSlot? shadowItem)
    {
        IReadOnlyDictionary<uint, VirtualJxItemSlot> primaryItems = _primary.SnapshotItems();
        IReadOnlyDictionary<uint, VirtualJxItemSlot> shadowItems = _shadow.SnapshotItems();

        VirtualJxItemSlot? primarySlot = null;
        if (primaryItemId != 0 && primaryItems.TryGetValue(primaryItemId, out VirtualJxItemSlot? byId))
        {
            primarySlot = byId;
        }
        else
        {
            primarySlot = FindCoveringItem(primaryItems.Values, primaryPlace, primaryGridX, primaryGridY);
        }

        if (primarySlot is null)
        {
            shadowItem = null;
            return false;
        }

        // 1. Prefer exact (Genre, Detail, Particular, Level, Place, GridX, GridY)
        foreach (VirtualJxItemSlot candidate in shadowItems.Values)
        {
            if (SameTemplate(primarySlot, candidate) &&
                candidate.ContainerPlace == primarySlot.ContainerPlace &&
                candidate.GridX == primarySlot.GridX &&
                candidate.GridY == primarySlot.GridY)
            {
                shadowItem = candidate;
                return true;
            }
        }

        // 2. Fallback: same template in the same container place
        foreach (VirtualJxItemSlot candidate in shadowItems.Values)
        {
            if (SameTemplate(primarySlot, candidate) &&
                candidate.ContainerPlace == primarySlot.ContainerPlace)
            {
                shadowItem = candidate;
                return true;
            }
        }

        // 3. Fallback: same template anywhere
        foreach (VirtualJxItemSlot candidate in shadowItems.Values)
        {
            if (SameTemplate(primarySlot, candidate))
            {
                shadowItem = candidate;
                return true;
            }
        }

        shadowItem = null;
        return false;
    }

    private static bool SameTemplate(VirtualJxItemSlot a, VirtualJxItemSlot b) =>
        a.Genre == b.Genre &&
        a.DetailType == b.DetailType &&
        a.ParticularType == b.ParticularType &&
        a.Level == b.Level;

    private static VirtualJxItemSlot? FindCoveringItem(
        IEnumerable<VirtualJxItemSlot> items,
        ItemContainerPlace place,
        byte x,
        byte y)
    {
        foreach (VirtualJxItemSlot slot in items)
        {
            if (slot.ContainerPlace != place)
            {
                continue;
            }

            if (place == ItemContainerPlace.Equip)
            {
                if (slot.GridX == x)
                {
                    return slot;
                }

                continue;
            }

            if (x >= slot.GridX && x < slot.GridX + Math.Max(1, slot.WidthCells) &&
                y >= slot.GridY && y < slot.GridY + Math.Max(1, slot.HeightCells))
            {
                return slot;
            }
        }

        return null;
    }
}
