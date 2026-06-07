using UnityEngine;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// Component that manages the map feature visualizations for a hex grid chunk.
/// </summary>
public class HexFeatureManager : MonoBehaviour
{
    [System.Serializable]
    public struct HexFeatureCollection
    {
        public Transform[] prefabs;

        public readonly Transform Pick(float choice) =>
            prefabs[(int)(choice * prefabs.Length)];
    }

    [SerializeField]
    HexFeatureCollection[] homeCollections, stoneCollections, woodCollections;

    [SerializeField]
    HexMesh walls;

    [SerializeField]
    Transform wallTower, bridge;

    [SerializeField]
    Transform[] special;

    Transform container;

    struct FeatureRequest
    {
        public HexCellData cell;
        public Vector3 position;
        public float sortKey;
    }

    readonly List<FeatureRequest> pendingFeatures = new();

    /// <summary>
    /// Clear all features. Uses DestroyImmediate so that old prefabs are
    /// removed synchronously — before the scene renders — rather than being
    /// deferred to the end of the frame where they would remain visible for
    /// one extra frame after a resource level drops.
    /// </summary>
    public void Clear()
    {
        if (container)
        {
            // DestroyImmediate removes the old container and all its child
            // prefabs right now, not at the end of the frame. This is safe
            // in play mode and is the correct fix for deferred-destruction
            // visual artifacts when cell resource levels change mid-game.
            DestroyImmediate(container.gameObject);
        }
        container = new GameObject("Features Container").transform;
        container.SetParent(transform, false);
        walls.Clear();
        pendingFeatures.Clear();
    }

    /// <summary>
    /// Apply triangulation. Resolves all pending feature slot requests into
    /// spawned prefabs, then applies the wall mesh.
    /// </summary>
    public void Apply()
    {
        var groups = pendingFeatures.GroupBy(r => r.cell.coordinates.ToString());

        foreach (var group in groups)
        {
            List<FeatureRequest> slots =
                group.OrderBy(r => r.sortKey).ToList();

            HexCellData cell = slots[0].cell;

            int slotIndex = 0;
            slotIndex = PlaceFeaturesOfType(
                homeCollections, cell.HomeLevel, slots, slotIndex);
            slotIndex = PlaceFeaturesOfType(
                stoneCollections, cell.StoneLevel, slots, slotIndex);
            PlaceFeaturesOfType(
                woodCollections, cell.WoodLevel, slots, slotIndex);
        }

        walls.Apply();
    }

    /// <summary>
    /// Record a feature slot for later processing. Called once per valid
    /// triangulation slot by <see cref="HexGridChunk"/>.
    /// </summary>
    public void AddFeature(HexCellData cell, Vector3 position)
    {
        HexHash hash = HexMetrics.SampleHashGrid(position);
        pendingFeatures.Add(new FeatureRequest
        {
            cell = cell,
            position = position,
            sortKey = hash.a,
        });
    }

    int GetCollectionIndex(
        HexFeatureCollection[] collection, int level, float hash)
    {
        float[] thresholds = HexMetrics.GetFeatureThresholds(level - 1);
        for (int i = 0; i < thresholds.Length; i++)
        {
            if (hash < thresholds[i])
            {
                return i;
            }
        }
        return collection.Length - 1;
    }

    int PlaceFeaturesOfType(
        HexFeatureCollection[] collection,
        int level,
        List<FeatureRequest> slots,
        int slotIndex)
    {
        if (level <= 0 || slotIndex >= slots.Count)
        {
            return slotIndex;
        }

        int count = Mathf.Min(level, slots.Count - slotIndex);

        for (int i = 0; i < count; i++)
        {
            FeatureRequest slot = slots[slotIndex + i];
            HexHash hash = HexMetrics.SampleHashGrid(slot.position);
            int tier = GetCollectionIndex(collection, level, hash.a);

            Transform prefab = collection[tier].Pick(hash.d);
            Transform instance = Instantiate(prefab);

            Vector3 position = slot.position;
            position.y += instance.localScale.y * 0.5f;
            instance.SetLocalPositionAndRotation(
                HexMetrics.Perturb(position),
                Quaternion.Euler(0f, 360f * hash.e, 0f));
            instance.SetParent(container, false);
        }

        return slotIndex + count;
    }

    /// <summary>
    /// Add a bridge between two road centers.
    /// </summary>
    public void AddBridge(Vector3 roadCenter1, Vector3 roadCenter2)
    {
        roadCenter1 = HexMetrics.Perturb(roadCenter1);
        roadCenter2 = HexMetrics.Perturb(roadCenter2);
        Transform instance = Instantiate(bridge);
        instance.localPosition = (roadCenter1 + roadCenter2) * 0.5f;
        instance.forward = roadCenter2 - roadCenter1;
        float length = Vector3.Distance(roadCenter1, roadCenter2);
        instance.localScale = new Vector3(
            1f, 1f, length * (1f / HexMetrics.bridgeDesignLength));
        instance.SetParent(container, false);
    }

    /// <summary>
    /// Add a wall along the edge between two cells.
    /// </summary>
    public void AddWall(
        EdgeVertices near, HexCellData nearCell,
        EdgeVertices far, HexCellData farCell,
        bool hasRiver, bool hasRoad)
    {
        if (nearCell.Walled != farCell.Walled &&
            !nearCell.IsUnderwater && !farCell.IsUnderwater &&
            nearCell.GetEdgeType(farCell) != HexEdgeType.Cliff)
        {
            AddWallSegment(near.v1, far.v1, near.v2, far.v2);
            if (hasRiver || hasRoad)
            {
                AddWallCap(near.v2, far.v2);
                AddWallCap(far.v4, near.v4);
            }
            else
            {
                AddWallSegment(near.v2, far.v2, near.v3, far.v3);
                AddWallSegment(near.v3, far.v3, near.v4, far.v4);
            }
            AddWallSegment(near.v4, far.v4, near.v5, far.v5);
        }
    }

    /// <summary>
    /// Add a wall through the corner where three cells meet.
    /// </summary>
    public void AddWall(
        Vector3 c1, HexCellData cell1,
        Vector3 c2, HexCellData cell2,
        Vector3 c3, HexCellData cell3)
    {
        if (cell1.Walled)
        {
            if (cell2.Walled)
            {
                if (!cell3.Walled) AddWallSegment(c3, cell3, c1, cell1, c2, cell2);
            }
            else if (cell3.Walled)
            {
                AddWallSegment(c2, cell2, c3, cell3, c1, cell1);
            }
            else
            {
                AddWallSegment(c1, cell1, c2, cell2, c3, cell3);
            }
        }
        else if (cell2.Walled)
        {
            if (cell3.Walled) AddWallSegment(c1, cell1, c2, cell2, c3, cell3);
            else AddWallSegment(c2, cell2, c3, cell3, c1, cell1);
        }
        else if (cell3.Walled)
        {
            AddWallSegment(c3, cell3, c1, cell1, c2, cell2);
        }
    }

    void AddWallSegment(
        Vector3 nearLeft, Vector3 farLeft,
        Vector3 nearRight, Vector3 farRight,
        bool addTower = false)
    {
        nearLeft = HexMetrics.Perturb(nearLeft);
        farLeft = HexMetrics.Perturb(farLeft);
        nearRight = HexMetrics.Perturb(nearRight);
        farRight = HexMetrics.Perturb(farRight);

        Vector3 left = HexMetrics.WallLerp(nearLeft, farLeft);
        Vector3 right = HexMetrics.WallLerp(nearRight, farRight);

        Vector3 leftThicknessOffset = HexMetrics.WallThicknessOffset(nearLeft, farLeft);
        Vector3 rightThicknessOffset = HexMetrics.WallThicknessOffset(nearRight, farRight);

        float leftTop = left.y + HexMetrics.wallHeight;
        float rightTop = right.y + HexMetrics.wallHeight;

        Vector3 v1, v2, v3, v4;
        v1 = v3 = left - leftThicknessOffset;
        v2 = v4 = right - rightThicknessOffset;
        v3.y = leftTop;
        v4.y = rightTop;
        walls.AddQuadUnperturbed(v1, v2, v3, v4);

        Vector3 t1 = v3, t2 = v4;

        v1 = v3 = left + leftThicknessOffset;
        v2 = v4 = right + rightThicknessOffset;
        v3.y = leftTop;
        v4.y = rightTop;
        walls.AddQuadUnperturbed(v2, v1, v4, v3);
        walls.AddQuadUnperturbed(t1, t2, v3, v4);

        if (addTower)
        {
            Transform towerInstance = Instantiate(wallTower);
            towerInstance.transform.localPosition = (left + right) * 0.5f;
            Vector3 rightDirection = right - left;
            rightDirection.y = 0f;
            towerInstance.transform.right = rightDirection;
            towerInstance.SetParent(container, false);
        }
    }

    void AddWallSegment(
        Vector3 pivot, HexCellData pivotCell,
        Vector3 left, HexCellData leftCell,
        Vector3 right, HexCellData rightCell)
    {
        if (pivotCell.IsUnderwater) return;

        bool hasLeftWall = !leftCell.IsUnderwater &&
            pivotCell.GetEdgeType(leftCell) != HexEdgeType.Cliff;
        bool hasRightWall = !rightCell.IsUnderwater &&
            pivotCell.GetEdgeType(rightCell) != HexEdgeType.Cliff;

        if (hasLeftWall)
        {
            if (hasRightWall)
            {
                bool hasTower = false;
                if (leftCell.Elevation == rightCell.Elevation)
                {
                    HexHash hash = HexMetrics.SampleHashGrid(
                        (pivot + left + right) * (1f / 3f));
                    hasTower = hash.e < HexMetrics.wallTowerThreshold;
                }
                AddWallSegment(pivot, left, pivot, right, hasTower);
            }
            else if (leftCell.Elevation < rightCell.Elevation)
            {
                AddWallWedge(pivot, left, right);
            }
            else
            {
                AddWallCap(pivot, left);
            }
        }
        else if (hasRightWall)
        {
            if (rightCell.Elevation < leftCell.Elevation)
                AddWallWedge(right, pivot, left);
            else
                AddWallCap(right, pivot);
        }
    }

    void AddWallCap(Vector3 near, Vector3 far)
    {
        near = HexMetrics.Perturb(near);
        far = HexMetrics.Perturb(far);

        Vector3 center = HexMetrics.WallLerp(near, far);
        Vector3 thickness = HexMetrics.WallThicknessOffset(near, far);

        Vector3 v1, v2, v3, v4;
        v1 = v3 = center - thickness;
        v2 = v4 = center + thickness;
        v3.y = v4.y = center.y + HexMetrics.wallHeight;
        walls.AddQuadUnperturbed(v1, v2, v3, v4);
    }

    void AddWallWedge(Vector3 near, Vector3 far, Vector3 point)
    {
        near = HexMetrics.Perturb(near);
        far = HexMetrics.Perturb(far);
        point = HexMetrics.Perturb(point);

        Vector3 center = HexMetrics.WallLerp(near, far);
        Vector3 thickness = HexMetrics.WallThicknessOffset(near, far);

        Vector3 v1, v2, v3, v4;
        Vector3 pointTop = point;
        point.y = center.y;

        v1 = v3 = center - thickness;
        v2 = v4 = center + thickness;
        v3.y = v4.y = pointTop.y = center.y + HexMetrics.wallHeight;

        walls.AddQuadUnperturbed(v1, point, v3, pointTop);
        walls.AddQuadUnperturbed(point, v2, pointTop, v4);
        walls.AddTriangleUnperturbed(pointTop, v3, v4);
    }
}