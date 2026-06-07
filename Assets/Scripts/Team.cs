using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Represents a team sharing fog-of-war, resource storage, and a home.
/// </summary>
public class Team
{
    public int TeamIndex { get; }
    public Color Color { get; }
    public List<HexUnit> Units { get; } = new();

    // ------------------------------------------------------------------ turn order

    /// <summary>
    /// Fixed unit-action order for the entire game.
    /// Set once in TurnManager.Initialize(); never shuffled again.
    /// Indices into <see cref="Units"/>; dead units are skipped at runtime
    /// but remain in the list so their slot is preserved.
    /// </summary>
    public int[] FixedUnitOrder { get; set; }

    // ------------------------------------------------------------------ home

    /// <summary>Index of this team's home cell. -1 if not yet placed.</summary>
    public int HomeCellIndex { get; set; } = -1;

    /// <summary>Current home level (1–3).</summary>
    public int HomeLevel { get; set; } = 1;

    /// <summary>Current home hit points.</summary>
    public int HomeHP { get; set; }

    /// <summary>The live scene Transform of the home prefab instance.</summary>
    public Transform HomeInstance { get; set; }

    // ------------------------------------------------------------------ resources

    public int WoodStored { get; private set; }
    public int StoneStored { get; private set; }

    bool[] exploredCells;

    public Team(int teamIndex, Color color, int cellCount)
    {
        TeamIndex = teamIndex;
        Color = color;
        exploredCells = new bool[cellCount];
    }

    // ------------------------------------------------------------------ resource ops

    public void StoreResources(int wood, int stone)
    {
        WoodStored += wood;
        StoneStored += stone;
    }

    /// <summary>
    /// Attempt to spend resources from the unit's carried inventory first,
    /// then from home storage for any remainder.
    /// Returns false (nothing spent) if the combined pool is insufficient.
    /// </summary>
    public bool TrySpendResources(HexUnit unit, int woodCost, int stoneCost)
    {
        if (unit.WoodCarried + WoodStored < woodCost) return false;
        if (unit.StoneCarried + StoneStored < stoneCost) return false;

        int woodFromUnit = Mathf.Min(unit.WoodCarried, woodCost);
        unit.SpendWood(woodFromUnit);
        WoodStored -= woodCost - woodFromUnit;

        int stoneFromUnit = Mathf.Min(unit.StoneCarried, stoneCost);
        unit.SpendStone(stoneFromUnit);
        StoneStored -= stoneCost - stoneFromUnit;

        return true;
    }

    /// <summary>
    /// Subtract resources from home storage only (used for loot payouts
    /// when an attacker raids the home — resources leave storage).
    /// Clamps to zero; never goes negative.
    /// </summary>
    public void SpendStoredResources(int wood, int stone)
    {
        WoodStored = Mathf.Max(0, WoodStored - wood);
        StoneStored = Mathf.Max(0, StoneStored - stone);
    }

    // ------------------------------------------------------------------ team cell history (anti-mirror)
    //
    // Sliding window of the last N cells visited by any unit of this team,
    // tagged with WHICH unit visited them. Used in 2v2 scenarios so we can
    // penalise an ally for stepping onto a cell its teammate just walked
    // through — that's the "going through the same cells" mirror pattern
    // the policy falls into when both units share weights, and the cheapest
    // way to break it is a small per-step penalty whenever a unit lands on
    // a cell another teammate visited in the recent past.
    //
    // Cleared at the start of every episode by ClearTeamMoveHistory().

    readonly Queue<(int cellIndex, HexUnit unit)> recentTeamMoves = new();
    const int TeamMoveWindowSize = 8;

    public void RecordTeamMove(HexUnit unit, int cellIndex)
    {
        recentTeamMoves.Enqueue((cellIndex, unit));
        while (recentTeamMoves.Count > TeamMoveWindowSize)
            recentTeamMoves.Dequeue();
    }

    public bool DidOtherTeammateRecentlyVisit(HexUnit excluding, int cellIndex)
    {
        foreach (var (ci, u) in recentTeamMoves)
            if (ci == cellIndex && u != excluding) return true;
        return false;
    }

    public void ClearTeamMoveHistory() => recentTeamMoves.Clear();

    // ------------------------------------------------------------------ fog-of-war

    public bool IsCellExplored(int cellIndex) => exploredCells[cellIndex];

    /// <summary>
    /// Force a cell into this team's explored set. Used right after the home
    /// cell is placed so the home is always visible to its team — even if it
    /// happens to be on a cliff or otherwise blocked from the unit's natural
    /// vision radius, the team always knows where their own base is.
    /// </summary>
    public void MarkExplored(int cellIndex)
    {
        if (cellIndex >= 0 && cellIndex < exploredCells.Length)
            exploredCells[cellIndex] = true;
    }

    public void SaveExploredFromGrid(HexCellData[] cellData)
    {
        for (int i = 0; i < exploredCells.Length; i++)
            if (cellData[i].flags.HasAll(HexFlags.Explored | HexFlags.Explorable))
                exploredCells[i] = true;

        // Belt-and-braces: regardless of what vision did this turn, ensure
        // the home cell is still in the explored set. Stops fog from ever
        // re-covering the base.
        if (HomeCellIndex >= 0 && HomeCellIndex < exploredCells.Length)
            exploredCells[HomeCellIndex] = true;
    }

    public void ApplyExploredToGrid(HexCellData[] cellData)
    {
        for (int i = 0; i < exploredCells.Length; i++)
            cellData[i].flags = exploredCells[i]
                ? cellData[i].flags.With(HexFlags.Explored)
                : cellData[i].flags.Without(HexFlags.Explored);
    }

    public static void ClearExploredFromGrid(HexCellData[] cellData)
    {
        for (int i = 0; i < cellData.Length; i++)
            cellData[i].flags = cellData[i].flags.Without(HexFlags.Explored);
    }
}