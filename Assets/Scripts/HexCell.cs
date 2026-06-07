using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Struct that identifies a hex cell.
/// </summary>
[System.Serializable]
public struct HexCell
{
#pragma warning disable IDE0044 // Add readonly modifier
	int index;

	HexGrid grid;
#pragma warning restore IDE0044 // Add readonly modifier

	/// <summary>
	/// Creates a cell given an index and grid.
	/// </summary>
	/// <param name="index">Index of the cell.</param>
	/// <param name="grid">Grid the cell is a part of.</param>
	public HexCell(int index, HexGrid grid)
	{
		this.index = index;
		this.grid = grid;
	}

	/// <summary>
	/// Hexagonal coordinates unique to the cell.
	/// </summary>
	public readonly HexCoordinates Coordinates =>
		grid.CellData[index].coordinates;

	/// <summary>
	/// Unique global index of the cell.
	/// </summary>
	public readonly int Index => index;

	/// <summary>
	/// Local position of this cell.
	/// </summary>
	public readonly Vector3 Position => grid.CellPositions[index];

	/// <summary>
	/// Set the elevation level.
	/// </summary>
	/// <param name="elevation">Elevation level.</param>
	public readonly void SetElevation (int elevation)
	{
		if (Values.Elevation != elevation)
		{
			Values = Values.WithElevation(elevation);
			grid.ShaderData.ViewElevationChanged(index);
			grid.RefreshCellPosition(index);
			ValidateRivers();
			HexFlags flags = Flags;
			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (flags.HasRoad(d))
				{
					HexCell neighbor = GetNeighbor(d);
					if (Mathf.Abs(elevation - neighbor.Values.Elevation) > 1)
					{
						RemoveRoad(d);
					}
				}
			}
			grid.RefreshCellWithDependents(index);
		}
	}

	/// <summary>
	/// Set the water level.
	/// </summary>
	/// <param name="waterLevel">Water level.</param>
	public readonly void SetWaterLevel (int waterLevel)
	{
		if (Values.WaterLevel != waterLevel)
		{
			Values = Values.WithWaterLevel(waterLevel);
			grid.ShaderData.ViewElevationChanged(index);
			ValidateRivers();
			grid.RefreshCellWithDependents(index);
		}
	}

	/// <summary>
	/// Set the home level.
	/// </summary>
	/// <param name="homeLevel">Home level.</param>
	public readonly void SetHomeLevel (int homeLevel)
	{
		if (Values.HomeLevel != homeLevel)
		{
			Values = Values.WithHomeLevel(homeLevel);
			Refresh();
		}
	}

    /// <summary>
    /// Set the stone level.
    /// </summary>
    /// <param name="stoneLevel">Stone level.</param>
    public readonly void SetStoneLevel (int stoneLevel)
	{
		if (Values.StoneLevel != stoneLevel)
		{
			Values = Values.WithStoneLevel(stoneLevel);
			Refresh();
		}
	}

    /// <summary>
    /// Set the wood level.
    /// </summary>
    /// <param name="woodLevel">Wood level.</param>
    public readonly void SetWoodLevel(int woodLevel)
	{
		if (Values.WoodLevel != woodLevel)
		{
			Values = Values.WithWoodLevel(woodLevel);
			Refresh();
		}
	}

	/// <summary>
	/// Set whether the cell is walled.
	/// </summary>
	/// <param name="walled">Whether the cell is walled.</param>
	public readonly void SetWalled (bool walled)
	{
		HexFlags flags = Flags;
		HexFlags newFlags = walled ?
			flags.With(HexFlags.Walled) : flags.Without(HexFlags.Walled);
		if (flags != newFlags)
		{
			Flags = newFlags;
			grid.RefreshCellWithDependents(index);
		}
	}

	/// <summary>
	/// Set the terrain type index.
	/// </summary>
	/// <param name="terrainTypeIndex">Terrain type index.</param>
	public readonly void SetTerrainTypeIndex (int terrainTypeIndex)
	{
		if (Values.TerrainTypeIndex != terrainTypeIndex)
		{
			Values = Values.WithTerrainTypeIndex(terrainTypeIndex);
			grid.ShaderData.RefreshTerrain(index);
		}
	}

    /// <summary>
    /// Primary unit on this cell (first in occupant list), or null.
    /// For iteration over all occupants use <see cref="Units"/>.
    /// Setting this to a non-null value ADDS the unit to the cell.
    /// Setting to null is a no-op — use <see cref="RemoveUnit"/> instead.
    /// </summary>
    public readonly HexUnit Unit
    {
        get => grid.CellUnits[index].Count > 0 ? grid.CellUnits[index][0] : null;
        set
        {
            if (value != null && !grid.CellUnits[index].Contains(value))
                grid.CellUnits[index].Add(value);
        }
    }

    /// <summary>All units currently occupying this cell.</summary>
    public readonly IReadOnlyList<HexUnit> Units => grid.CellUnits[index];

    /// <summary>
    /// Remove a specific unit from this cell's occupant list.
    /// Use this instead of <c>Unit = null</c>.
    /// </summary>
    public readonly void RemoveUnit(HexUnit unit) =>
        grid.CellUnits[index].Remove(unit);

    /// <summary>
    /// Returns true when any unit on this cell belongs to a different team.
    /// </summary>
    public readonly bool HasEnemyUnits(Team viewingTeam)
    {
        foreach (HexUnit u in grid.CellUnits[index])
            if (u.Team != viewingTeam) return true;
        return false;
    }

    /// <summary>
    /// Flags of the cell.
    /// </summary>
    public readonly HexFlags Flags
	{
		get => grid.CellData[index].flags;
		set => grid.CellData[index].flags = value;
	}

	/// <summary>
	/// Values of the cell.
	/// </summary>
	public readonly HexValues Values
	{
		get => grid.CellData[index].values;
		set => grid.CellData[index].values = value;
	}

	/// <summary>
	/// Get one of the neighbor cells. Only valid if that neighbor exists.
	/// </summary>
	/// <param name="direction">Neighbor direction relative to the cell.</param>
	/// <returns>Neighbor cell, if it exists.</returns>
	public readonly HexCell GetNeighbor(HexDirection direction) =>
		grid.GetCell(Coordinates.Step(direction));

	/// <summary>
	/// Try to get one of the neighbor cells.
	/// </summary>
	/// <param name="direction">Neighbor direction relative to the cell.</param>
	/// <param name="cell">The neighbor cell, if it exists.</param>
	/// <returns>Whether the neighbor exists.</returns>
	public readonly bool TryGetNeighbor(
		HexDirection direction, out HexCell cell) =>
		grid.TryGetCell(Coordinates.Step(direction), out cell);
	
	readonly void RemoveIncomingRiver()
	{
		if (Flags.HasAny(HexFlags.RiverIn))
		{
			HexCell neighbor = GetNeighbor(Flags.RiverInDirection());
			Flags = Flags.Without(HexFlags.RiverIn);
			neighbor.Flags = neighbor.Flags.Without(HexFlags.RiverOut);
			neighbor.Refresh();
			Refresh();
		}
	}

	readonly void RemoveOutgoingRiver()
	{
		if (Flags.HasAny(HexFlags.RiverOut))
		{
			HexCell neighbor = GetNeighbor(Flags.RiverOutDirection());
			Flags = Flags.Without(HexFlags.RiverOut);
			neighbor.Flags = neighbor.Flags.Without(HexFlags.RiverIn);
			neighbor.Refresh();
			Refresh();
		}
	}

	/// <summary>
	/// Clear the cell of rivers.
	/// </summary>
	public readonly void RemoveRiver()
	{
		RemoveIncomingRiver();
		RemoveOutgoingRiver();
	}

	static bool CanRiverFlow (HexValues from, HexValues to) =>
		from.Elevation >= to.Elevation || from.WaterLevel == to.Elevation;

	/// <summary>
	/// Set the outgoing river.
	/// </summary>
	/// <param name="direction">River direction.</param>
	public readonly void SetOutgoingRiver (HexDirection direction)
	{
		if (Flags.HasRiverOut(direction))
		{
			return;
		}

		HexCell neighbor = GetNeighbor(direction);
		if (!CanRiverFlow(Values, neighbor.Values))
		{
			return;
		}

		RemoveOutgoingRiver();
		if (Flags.HasRiverIn(direction))
		{
			RemoveIncomingRiver();
		}

		Flags = Flags.WithRiverOut(direction);
		neighbor.RemoveIncomingRiver();
		neighbor.Flags = neighbor.Flags.WithRiverIn(direction.Opposite());

		RemoveRoad(direction);
	}

	/// <summary>
	/// Add a road in the given direction.
	/// </summary>
	/// <param name="direction">Road direction.</param>
	public readonly void AddRoad(HexDirection direction)
	{
		HexFlags flags = Flags;
		HexCell neighbor = GetNeighbor(direction);
		if (
			!flags.HasRoad(direction) && !flags.HasRiver(direction) &&
			Mathf.Abs(Values.Elevation - neighbor.Values.Elevation) <= 1
		)
		{
			Flags = flags.WithRoad(direction);
			neighbor.Flags = neighbor.Flags.WithRoad(direction.Opposite());
			neighbor.Refresh();
			Refresh();
		}
	}

	/// <summary>
	/// Clear the cell of roads.
	/// </summary>
	public readonly void RemoveRoads()
	{
		HexFlags flags = Flags;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (flags.HasRoad(d))
			{
				RemoveRoad(d);
			}
		}
	}

	readonly void ValidateRivers()
	{
		HexFlags flags = Flags;
		if (flags.HasAny(HexFlags.RiverOut) &&
			!CanRiverFlow(Values, GetNeighbor(flags.RiverOutDirection()).Values)
		)
		{
			RemoveOutgoingRiver();
		}
		if (flags.HasAny(HexFlags.RiverIn) &&
			!CanRiverFlow(GetNeighbor(flags.RiverInDirection()).Values, Values))
		{
			RemoveIncomingRiver();
		}
	}

	readonly void RemoveRoad(HexDirection direction)
	{
		Flags = Flags.WithoutRoad(direction);
		HexCell neighbor = GetNeighbor(direction);
		neighbor.Flags = neighbor.Flags.WithoutRoad(direction.Opposite());
		neighbor.Refresh();
		Refresh();
	}

	readonly void Refresh() => grid.RefreshCell(index);

	/// <inheritdoc/>
	public readonly override bool Equals(object obj) =>
		obj is HexCell cell && this == cell;

	/// <inheritdoc/>
	public readonly override int GetHashCode() =>
		grid != null ? index.GetHashCode() ^ grid.GetHashCode() : 0;
	
	/// <summary>
	/// A cell counts as true if it is part of a grid.
	/// </summary>
	/// <param name="cell">The cell to check.</param>
	public static implicit operator bool(HexCell cell) => cell.grid != null;

	public static bool operator ==(HexCell a, HexCell b) =>
		a.index == b.index && a.grid == b.grid;
	
	public static bool operator !=(HexCell a, HexCell b) =>
		a.index != b.index || a.grid != b.grid;
}
