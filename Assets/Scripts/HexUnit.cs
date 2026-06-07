using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System.IO;

/// <summary>
/// Component representing a unit on the hex map.
/// </summary>
public class HexUnit : MonoBehaviour
{
    // ── Static offsets for co-occupying units on the same cell ───────────
    // Indexed by (unit count - 1) then unit index within that group.
    // Expressed as fractions; scaled by innerRadius at runtime.
    static readonly Vector3[][] cellOffsets =
    {
        new[] { new Vector3( 0.00f, 0f,  0.00f) },                                         // 1 unit
        new[] { new Vector3(-0.35f, 0f,  0.00f), new Vector3( 0.35f, 0f,  0.00f) },        // 2 units
        new[] { new Vector3(-0.35f, 0f, -0.25f), new Vector3( 0.35f, 0f, -0.25f),
                new Vector3( 0.00f, 0f,  0.30f) },                                          // 3 units
        new[] { new Vector3(-0.35f, 0f, -0.25f), new Vector3( 0.35f, 0f, -0.25f),
                new Vector3(-0.35f, 0f,  0.25f), new Vector3( 0.35f, 0f,  0.25f) },        // 4 units
    };

    static Vector3 GetCellOffset(int unitIndex, int unitCount)
    {
        int group = Mathf.Clamp(unitCount - 1, 0, cellOffsets.Length - 1);
        int slot = Mathf.Clamp(unitIndex, 0, cellOffsets[group].Length - 1);
        return cellOffsets[group][slot] * HexMetrics.innerRadius;
    }

    // ======================================================================

    const float rotationSpeed = 180f;
    const float travelSpeed = 4f;

    public static HexUnit unitPrefab;
    public HexGrid Grid { get; set; }

    // ── Team ──────────────────────────────────────────────────────────────
    public Team Team { get; set; }

    // ── Action points ─────────────────────────────────────────────────────
    [SerializeField] int actionPointsPerTurn = 24;

    public int ActionPointsRemaining { get; private set; }
    public void StartTurn() => ActionPointsRemaining = actionPointsPerTurn;
    public void SpendActionPoints(int amount) =>
        ActionPointsRemaining = Mathf.Max(0, ActionPointsRemaining - amount);

    // ── HP & death ────────────────────────────────────────────────────────
    [SerializeField] int maxHP = 100;

    public int MaxHP => maxHP;
    public int CurrentHP { get; private set; }
    public bool IsDead { get; private set; }

    /// <summary>
    /// True once the unit has sat out at least one turn since dying,
    /// meaning it is now eligible to respawn when the home cell is clear.
    /// </summary>
    public bool ReadyToRespawn { get; private set; }
    public void SetReadyToRespawn(bool value) => ReadyToRespawn = value;

    /// <summary>
    /// Called by TurnManager on the unit's turn to respawn it.
    /// </summary>
       public void Respawn(HexCell homeCell)
    {
        CurrentHP = maxHP;
        IsDead = false;
        ReadyToRespawn = false;
        gameObject.SetActive(true);
        Location = homeCell;
        Grid.RefreshCellUnitPositions(homeCell.Index);
    }

    /// <summary>
    /// Deal <paramref name="damage"/> to this unit.
    /// Returns true if the unit died.
    /// </summary>
    public bool TakeDamage(int damage)
    {
        if (IsDead) return false;
        CurrentHP = Mathf.Max(0, CurrentHP - damage);
        Debug.Log($"[HexUnit] {name} took {damage} dmg — HP: {CurrentHP}/{maxHP}");
        if (CurrentHP <= 0)
        {
            MarkDead();
            return true;
        }
        return false;
    }

    /// <summary>
    /// Remove this unit from the board visually/logically but keep it in
    /// the team list so its fixed turn slot is preserved for respawn.
    /// </summary>
    void MarkDead()
    {
        IsDead = true;
        ReadyToRespawn = false;
        if (locationCellIndex >= 0)
        {
            HexCell cell = Grid.GetCell(locationCellIndex);
            if (teamVisibilityActive) Grid.DecreaseVisibility(cell, VisionRange);
            cell.RemoveUnit(this);
            Grid.RefreshCellUnitPositions(locationCellIndex);
        }
        gameObject.SetActive(false);
        Debug.Log($"[HexUnit] {name} died and will respawn next turn.");
    }

    // ── Carried resources ─────────────────────────────────────────────────
    public int WoodCarried { get; private set; }
    public int StoneCarried { get; private set; }
    public int TotalCarried => WoodCarried + StoneCarried;

    public void GainWood(int amount) => WoodCarried += amount;
    public void GainStone(int amount) => StoneCarried += amount;
    public void SpendWood(int amount) => WoodCarried = Mathf.Max(0, WoodCarried - amount);
    public void SpendStone(int amount) => StoneCarried = Mathf.Max(0, StoneCarried - amount);

    public void DepositToTeam()
    {
        Team?.StoreResources(WoodCarried, StoneCarried);
        WoodCarried = StoneCarried = 0;
    }

    // ── Travel ────────────────────────────────────────────────────────────
    public bool IsTraveling => pathToTravel != null;

    // ── Team visibility ───────────────────────────────────────────────────
    bool teamVisibilityActive;
    public bool TeamVisibilityActive => teamVisibilityActive;

    public void ActivateTeamVisibility()
    {
        teamVisibilityActive = true;
        if (locationCellIndex >= 0 && !IsDead)
            Grid.IncreaseVisibility(Grid.GetCell(locationCellIndex), VisionRange);
    }

    public void DeactivateTeamVisibility()
    {
        if (locationCellIndex >= 0 && !IsDead)
            Grid.DecreaseVisibility(Grid.GetCell(locationCellIndex), VisionRange);
        teamVisibilityActive = false;
    }

    // ── Material ──────────────────────────────────────────────────────────
    public void ApplyTeamMaterial(Material material)
    {
        if (material == null) return;
        foreach (Renderer r in GetComponentsInChildren<Renderer>())
            r.sharedMaterial = material;
    }

    // ── Location ──────────────────────────────────────────────────────────
    public HexCell Location
    {
        get => Grid.GetCell(locationCellIndex);
        set
        {
            if (locationCellIndex >= 0)
            {
                HexCell old = Grid.GetCell(locationCellIndex);
                if (teamVisibilityActive) Grid.DecreaseVisibility(old, VisionRange);
                old.RemoveUnit(this);
                // Do NOT refresh positions here — caller does it after setup.
            }
            locationCellIndex = value.Index;
            value.Unit = this;          // adds to list via the patched setter
            if (teamVisibilityActive) Grid.IncreaseVisibility(value, VisionRange);
            // Position is set by UpdateCellPosition after RefreshCellUnitPositions.
            Grid.MakeChildOfColumn(transform, value.Coordinates.ColumnIndex);
        }
    }

    /// <summary>
    /// Recompute this unit's world position based on how many units share
    /// the cell and this unit's index within that list.
    /// </summary>
    public void UpdateCellPosition()
    {
        if (locationCellIndex < 0 || IsDead) return;
        List<HexUnit> list = Grid.CellUnits[locationCellIndex];
        int index = list.IndexOf(this);
        if (index < 0) return;
        transform.localPosition =
            Grid.CellPositions[locationCellIndex] + GetCellOffset(index, list.Count);
    }

    public void ValidateLocation() => UpdateCellPosition();

    int locationCellIndex = -1, currentTravelLocationCellIndex = -1;
    public int LocationCellIndex => locationCellIndex;

    // ── Stats ─────────────────────────────────────────────────────────────
    public float Orientation
    {
        get => orientation;
        set { orientation = value; transform.localRotation = Quaternion.Euler(0f, value, 0f); }
    }

    public int Speed => 24;
    public int VisionRange => 3;

    float orientation;
    List<int> pathToTravel;

    // ── Destination validation ────────────────────────────────────────────

    /// <summary>
    /// A cell is a valid destination when:
    /// - Explored and explorable
    /// - Not underwater
    /// - Not occupied by any enemy unit (enemy cells trigger attack via right-click,
    ///   but pathfinding should not route through them)
    /// Same-team cells are allowed so friendly units can share a cell.
    /// </summary>
    public bool IsValidDestination(HexCell cell)
    {
        if (!cell.Flags.HasAll(HexFlags.Explored | HexFlags.Explorable)) return false;
        if (cell.Values.IsUnderwater) return false;
        return !cell.HasEnemyUnits(Team);
    }

    // ── Move cost ─────────────────────────────────────────────────────────
    public int CalculatePathCost(List<int> path)
    {
        if (path == null || path.Count < 2) return -1;
        int total = 0;
        for (int i = 0; i < path.Count - 1; i++)
        {
            HexCell from = Grid.GetCell(path[i]);
            HexCell to = Grid.GetCell(path[i + 1]);
            int cost = GetMoveCost(from, to, FindDirectionBetween(from, to));
            if (cost < 0) return -1;
            total += cost;
        }
        return total;
    }

    HexDirection FindDirectionBetween(HexCell from, HexCell to)
    {
        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
            if (Grid.TryGetCellIndex(from.Coordinates.Step(d), out int idx) && idx == to.Index)
                return d;
        return HexDirection.NE;
    }

    /// <summary>
    /// True when either end of the from→to edge is this unit's team's home
    /// cell. Used to bypass cliff impassability so a base can never end up
    /// completely walled off by terrain — a unit can always scramble in or
    /// out of its own home regardless of elevation difference.
    /// </summary>
    bool IsHomeEdge(HexCell fromCell, HexCell toCell)
    {
        if (Team == null) return false;
        int homeIdx = Team.HomeCellIndex;
        if (homeIdx < 0) return false;
        return fromCell.Index == homeIdx || toCell.Index == homeIdx;
    }

    public int GetMoveCost(HexCell fromCell, HexCell toCell, HexDirection direction)
    {
        if (!IsValidDestination(toCell)) return -1;
        HexEdgeType edgeType = HexMetrics.GetEdgeType(
            fromCell.Values.Elevation, toCell.Values.Elevation);
        bool homeEdge = IsHomeEdge(fromCell, toCell);
        if (edgeType == HexEdgeType.Cliff && !homeEdge) return -1;

        int moveCost;
        if (fromCell.Flags.HasRoad(direction))
            moveCost = 1;
        else if (fromCell.Flags.HasAny(HexFlags.Walled) != toCell.Flags.HasAny(HexFlags.Walled)
                 && !homeEdge)
            return -1;
        else
        {
            // Cliffs on home edges treated as slopes for cost purposes.
            moveCost = edgeType == HexEdgeType.Flat ? 5 : 10;
            HexValues v = toCell.Values;
            moveCost += v.HomeLevel + v.StoneLevel + v.WoodLevel;
        }
        return moveCost;
    }

    /// <summary>
    /// Compute the move cost to enter <paramref name="adjacent"/> from the
    /// unit's current location. Returns -1 if not adjacent or impassable.
    /// Does NOT check IsValidDestination so it works for enemy cells too.
    /// </summary>
    public int GetAdjacentMoveCost(HexCell adjacent)
    {
        HexCell from = Location;
        HexDirection dir = FindDirectionBetween(from, adjacent);

        // Check passability without the destination-unit check.
        HexEdgeType edgeType = HexMetrics.GetEdgeType(
            from.Values.Elevation, adjacent.Values.Elevation);
        bool homeEdge = IsHomeEdge(from, adjacent);
        if (edgeType == HexEdgeType.Cliff && !homeEdge) return -1;

        int cost;
        if (from.Flags.HasRoad(dir))
            cost = 1;
        else if (from.Flags.HasAny(HexFlags.Walled) != adjacent.Flags.HasAny(HexFlags.Walled)
                 && !homeEdge)
            return -1;
        else
        {
            // Cliffs on home edges treated as slopes for cost purposes.
            cost = edgeType == HexEdgeType.Flat ? 5 : 10;
            HexValues v = adjacent.Values;
            cost += v.HomeLevel + v.StoneLevel + v.WoodLevel;
        }
        return cost;
    }

    // ── Travel ────────────────────────────────────────────────────────────
    public void Travel(List<int> path)
    {
        // Detach from current cell immediately (occupancy bookkeeping).
        HexCell oldCell = Grid.GetCell(locationCellIndex);
        oldCell.RemoveUnit(this);
        Grid.RefreshCellUnitPositions(oldCell.Index);

        // Register at destination so it is "occupied" during the animation.
        HexCell newCell = Grid.GetCell(path[^1]);
        locationCellIndex = newCell.Index;
        newCell.Unit = this;   // patched setter: adds to list

        pathToTravel = path;
        StopAllCoroutines();
        StartCoroutine(TravelPath());
    }

    IEnumerator TravelPath()
    {
        Vector3 a, b, c = Grid.GetCell(pathToTravel[0]).Position;
        yield return LookAt(Grid.GetCell(pathToTravel[1]).Position);

        if (currentTravelLocationCellIndex < 0)
            currentTravelLocationCellIndex = pathToTravel[0];

        HexCell currentTravelLocation = Grid.GetCell(currentTravelLocationCellIndex);
        Grid.DecreaseVisibility(currentTravelLocation, VisionRange);
        int currentColumn = currentTravelLocation.Coordinates.ColumnIndex;

        float t = Time.deltaTime * travelSpeed;
        for (int i = 1; i < pathToTravel.Count; i++)
        {
            currentTravelLocation = Grid.GetCell(pathToTravel[i]);
            currentTravelLocationCellIndex = currentTravelLocation.Index;
            a = c;
            b = Grid.GetCell(pathToTravel[i - 1]).Position;

            int nextColumn = currentTravelLocation.Coordinates.ColumnIndex;
            if (currentColumn != nextColumn)
            {
                if (nextColumn < currentColumn - 1)
                {
                    a.x -= HexMetrics.innerDiameter * HexMetrics.wrapSize;
                    b.x -= HexMetrics.innerDiameter * HexMetrics.wrapSize;
                }
                else if (nextColumn > currentColumn + 1)
                {
                    a.x += HexMetrics.innerDiameter * HexMetrics.wrapSize;
                    b.x += HexMetrics.innerDiameter * HexMetrics.wrapSize;
                }
                Grid.MakeChildOfColumn(transform, nextColumn);
                currentColumn = nextColumn;
            }

            c = (b + currentTravelLocation.Position) * 0.5f;
            Grid.IncreaseVisibility(Grid.GetCell(pathToTravel[i]), VisionRange);

            for (; t < 1f; t += Time.deltaTime * travelSpeed)
            {
                transform.localPosition = Bezier.GetPoint(a, b, c, t);
                Vector3 d = Bezier.GetDerivative(a, b, c, t);
                d.y = 0f;
                transform.localRotation = Quaternion.LookRotation(d);
                yield return null;
            }
            Grid.DecreaseVisibility(Grid.GetCell(pathToTravel[i]), VisionRange);
            t -= 1f;
        }
        currentTravelLocationCellIndex = -1;

        HexCell finalLocation = Grid.GetCell(locationCellIndex);
        a = c; b = finalLocation.Position; c = b;
        Grid.IncreaseVisibility(finalLocation, VisionRange);
        for (; t < 1f; t += Time.deltaTime * travelSpeed)
        {
            transform.localPosition = Bezier.GetPoint(a, b, c, t);
            Vector3 d = Bezier.GetDerivative(a, b, c, t);
            d.y = 0f;
            transform.localRotation = Quaternion.LookRotation(d);
            yield return null;
        }

        // Snap to final position with correct cell offset.
        Grid.RefreshCellUnitPositions(locationCellIndex);

        orientation = transform.localRotation.eulerAngles.y;
        ListPool<int>.Add(pathToTravel);
        pathToTravel = null;
    }

    IEnumerator LookAt(Vector3 point)
    {
        if (HexMetrics.Wrapping)
        {
            float xDistance = point.x - transform.localPosition.x;
            if (xDistance < -HexMetrics.innerRadius * HexMetrics.wrapSize)
                point.x += HexMetrics.innerDiameter * HexMetrics.wrapSize;
            else if (xDistance > HexMetrics.innerRadius * HexMetrics.wrapSize)
                point.x -= HexMetrics.innerDiameter * HexMetrics.wrapSize;
        }
        point.y = transform.localPosition.y;
        Quaternion fromRot = transform.localRotation;
        Quaternion toRot = Quaternion.LookRotation(point - transform.localPosition);
        float angle = Quaternion.Angle(fromRot, toRot);
        if (angle > 0f)
        {
            float speed = rotationSpeed / angle;
            for (float t = Time.deltaTime * speed; t < 1f; t += Time.deltaTime * speed)
            {
                transform.localRotation = Quaternion.Slerp(fromRot, toRot, t);
                yield return null;
            }
        }
        transform.LookAt(point);
        orientation = transform.localRotation.eulerAngles.y;
    }

    // ── Death (permanent — map editor / RemoveUnit) ───────────────────────
    public void Die()
    {
        if (locationCellIndex >= 0)
        {
            HexCell loc = Grid.GetCell(locationCellIndex);
            if (teamVisibilityActive) Grid.DecreaseVisibility(loc, VisionRange);
            loc.RemoveUnit(this);
            Grid.RefreshCellUnitPositions(locationCellIndex);
        }
        Team?.Units.Remove(this);
        Destroy(gameObject);
    }

    // ── Save / Load ───────────────────────────────────────────────────────
    public void Save(BinaryWriter writer)
    {
        Grid.GetCell(locationCellIndex).Coordinates.Save(writer);
        writer.Write(orientation);
    }

    public static void Load(BinaryReader reader, HexGrid grid)
    {
        HexCoordinates c = HexCoordinates.Load(reader);
        float ori = reader.ReadSingle();
        grid.AddUnit(Instantiate(unitPrefab), grid.GetCell(c), ori);
    }

    // ── Unity lifecycle ───────────────────────────────────────────────────
    void Awake() => CurrentHP = maxHP;

    void OnEnable()
    {
        if (locationCellIndex >= 0 && !IsDead)
        {
            UpdateCellPosition();
            if (currentTravelLocationCellIndex >= 0 && teamVisibilityActive)
            {
                HexCell travelLoc = Grid.GetCell(currentTravelLocationCellIndex);
                Grid.IncreaseVisibility(Location, VisionRange);
                Grid.DecreaseVisibility(travelLoc, VisionRange);
                currentTravelLocationCellIndex = -1;
            }
        }
    }
}