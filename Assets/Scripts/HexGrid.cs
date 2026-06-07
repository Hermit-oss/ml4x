using UnityEngine;
using UnityEngine.UI;
using System.IO;
using System.Collections.Generic;

/// <summary>
/// Component that represents an entire hexagon map.
/// </summary>
public class HexGrid : MonoBehaviour
{
	[SerializeField]
	Text cellLabelPrefab;

	[SerializeField]
	HexGridChunk chunkPrefab;

	[SerializeField]
	HexUnit unitPrefab;

	[SerializeField]
	Texture2D noiseSource;
	 
	[SerializeField]
	int seed;

	/// <summary>
	/// Amount of cells in the X dimension.
	/// </summary>
	public int CellCountX
	{ get; private set; }

	/// <summary>
	/// Amount of cells in the Z dimension.
	/// </summary>
	public int CellCountZ
	{ get; private set; }

	/// <summary>
	/// Whether there currently exists a path that should be displayed.
	/// </summary>
	public bool HasPath => currentPathExists;

	/// <summary>
	/// Whether east-west wrapping is enabled.
	/// </summary>
	public bool Wrapping
	{ get; private set; }

	Transform[] columns;

	HexGridChunk[] chunks;

	/// <summary>
	/// Bundled cell data.
	/// </summary>
	public HexCellData[] CellData
	{ get; private set; }

	/// <summary>
	/// Separate cell positions.
	/// </summary>
	public Vector3[] CellPositions
	{ get; private set; }

    public List<HexUnit>[] CellUnits
	{ get; private set; }

    HexCellSearchData[] searchData;

	/// <summary>
	/// Search data array usable for current map.
	/// </summary>
	public HexCellSearchData[] SearchData => searchData;

	int[] cellVisibility;

	HexGridChunk[] cellGridChunks;

	RectTransform[] cellUIRects;

	/// <summary>
	/// The <see cref="HexCellShaderData"/> container
	/// for cell visualization data.
	/// </summary>
	public HexCellShaderData ShaderData => cellShaderData;

	int chunkCountX, chunkCountZ;

	HexCellPriorityQueue searchFrontier;

	int searchFrontierPhase;

	int currentPathFromIndex = -1, currentPathToIndex = -1;
	bool currentPathExists;

	int currentCenterColumnIndex = -1;

#pragma warning disable IDE0044 // Add readonly modifier
	List<HexUnit> units = new();
#pragma warning restore IDE0044 // Add readonly modifier

	HexCellShaderData cellShaderData;

    public int[] CellWoodResources { get; private set; }
	public int[] CellStoneResources { get; private set; }
    void Awake()
	{
		CellCountX = 20;
		CellCountZ = 15;
		HexMetrics.noiseSource = noiseSource;
		HexMetrics.InitializeHashGrid(seed);
		HexUnit.unitPrefab = unitPrefab;
		cellShaderData = gameObject.AddComponent<HexCellShaderData>();
		cellShaderData.Grid = this;
		CreateMap(CellCountX, CellCountZ, Wrapping);
	}

	/// <summary>
	/// Add a unit to the map.
	/// </summary>
	/// <param name="unit">Unit to add.</param>
	/// <param name="location">Cell in which to place the unit.</param>
	/// <param name="orientation">Orientation of the unit.</param>
	public void AddUnit(HexUnit unit, HexCell location, float orientation)
	{
		units.Add(unit);
		unit.Grid = this;
		unit.Location = location;
		unit.Orientation = orientation;
	}

	/// <summary>
	/// Remove a unit from the map.
	/// </summary>
	/// <param name="unit">The unit to remove.</param>
	public void RemoveUnit(HexUnit unit)
	{
		units.Remove(unit);
		unit.Die();
	}

	/// <summary>
	/// Forget a unit from the grid's internal tracking list WITHOUT calling
	/// <see cref="HexUnit.Die"/>. Used by <c>TurnManager.EliminateTeam</c>,
	/// which runs its own destruction pipeline (cell removal + visibility
	/// decrease + Destroy) and just needs the grid to drop its reference
	/// so the next episode's <c>ClearUnits</c> doesn't trip over a stale
	/// pointer. Calling this before <c>Object.Destroy(unit.gameObject)</c>
	/// is the canonical pattern.
	/// </summary>
	public void UntrackUnit(HexUnit unit)
	{
		units.Remove(unit);
	}

	/// <summary>
	/// Make a game object a child of a map column.
	/// </summary>
	/// <param name="child"><see cref="Transform"/>
	/// of the child game object.</param>
	/// <param name="columnIndex">Index of the parent column.</param>
	public void MakeChildOfColumn(Transform child, int columnIndex) =>
		child.SetParent(columns[columnIndex], false);

	/// <summary>
	/// Create a new map.
	/// </summary>
	/// <param name="x">X size of the map.</param>
	/// <param name="z">Z size of the map.</param>
	/// <param name="wrapping">Whether the map wraps east-west.</param>
	/// <returns>Whether the map was successfully created. It fails when the X
	/// or Z size is not a multiple of the respective chunk size.</returns>
	public bool CreateMap(int x, int z, bool wrapping)
	{
		if (
			x <= 0 || x % HexMetrics.chunkSizeX != 0 ||
			z <= 0 || z % HexMetrics.chunkSizeZ != 0
		)
		{
			Debug.LogError("Unsupported map size.");
			return false;
		}

		ClearPath();
		ClearUnits();
		if (columns != null)
		{
			for (int i = 0; i < columns.Length; i++)
			{
				Destroy(columns[i].gameObject);
			}
		}

		CellCountX = x;
		CellCountZ = z;
		Wrapping = wrapping;
		currentCenterColumnIndex = -1;
		HexMetrics.wrapSize = wrapping ? CellCountX : 0;
		chunkCountX = CellCountX / HexMetrics.chunkSizeX;
		chunkCountZ = CellCountZ / HexMetrics.chunkSizeZ;
		cellShaderData.Initialize(CellCountX, CellCountZ);
		CreateChunks();
		CreateCells();
		return true;
	}

	void CreateChunks()
	{
		columns = new Transform[chunkCountX];
		for (int x = 0; x < chunkCountX; x++)
		{
			columns[x] = new GameObject("Column").transform;
			columns[x].SetParent(transform, false);
		}

		chunks = new HexGridChunk[chunkCountX * chunkCountZ];
		for (int z = 0, i = 0; z < chunkCountZ; z++)
		{
			for (int x = 0; x < chunkCountX; x++)
			{
				HexGridChunk chunk = chunks[i++] = Instantiate(chunkPrefab);
				chunk.transform.SetParent(columns[x], false);
				chunk.Grid = this;
			}
		}
	}

	void CreateCells()
	{
		CellData = new HexCellData[CellCountZ * CellCountX];
		CellPositions = new Vector3[CellData.Length];
		cellUIRects = new RectTransform[CellData.Length];
		cellGridChunks = new HexGridChunk[CellData.Length];
        CellUnits = new List<HexUnit>[CellData.Length];
        for (int i = 0; i < CellData.Length; i++)
            CellUnits[i] = new List<HexUnit>();
        searchData = new HexCellSearchData[CellData.Length];
		cellVisibility = new int[CellData.Length];
        CellWoodResources  = new int[CellData.Length];
		CellStoneResources = new int[CellData.Length];

        for (int z = 0, i = 0; z < CellCountZ; z++)
		{
			for (int x = 0; x < CellCountX; x++)
			{
				CreateCell(x, z, i++);
			}
		}
	}

	void ClearUnits()
	{
		for (int i = 0; i < units.Count; i++)
		{
			// Skip entries whose underlying GameObject was already
			// destroyed elsewhere (typically TurnManager.EliminateTeam
			// during the previous episode). Unity's overloaded equality
			// operator returns true for destroyed objects compared to
			// null, so this short-circuits before Die() can touch a
			// stale .gameObject and throw MissingReferenceException.
			if (units[i] != null) units[i].Die();
		}
		units.Clear();
	}

	void OnEnable()
	{
		if (!HexMetrics.noiseSource)
		{
			HexMetrics.noiseSource = noiseSource;
			HexMetrics.InitializeHashGrid(seed);
			HexUnit.unitPrefab = unitPrefab;
			HexMetrics.wrapSize = Wrapping ? CellCountX : 0;
			ResetVisibility();
		}
	}

	/// <summary>
	/// Get a cell given a <see cref="Ray"/>.
	/// </summary>
	/// <param name="ray"><see cref="Ray"/> used to perform a raycast.</param>
	/// <param name="stickyCell">Cell to stick to if close enough.</param>
	/// <returns>The hit cell, if any.</returns>
	public HexCell GetCell(Ray ray, HexCell stickyCell = default)
	{
		if (Physics.Raycast(ray, out RaycastHit hit))
		{
			return GetCell(hit.point, stickyCell);
		}
		return default;
	}

	/// <summary>
	/// Get the cell that contains a position.
	/// </summary>
	/// <param name="position">Position to check.</param>
	/// <param name="stickyCell">Cell to stick to if close enough.</param>
	/// <returns>The cell containing the position, if it exists.</returns>
	public HexCell GetCell(Vector3 position, HexCell stickyCell = default)
	{
		position = transform.InverseTransformPoint(position);
		if (stickyCell)
		{
			Vector3 v = position - stickyCell.Position;
			if (
				v.x * v.x + v.z * v.z <
				HexMetrics.stickyRadius * HexMetrics.stickyRadius)
			{
				return stickyCell;
			}
		}
		HexCoordinates coordinates = HexCoordinates.FromPosition(position);
		return GetCell(coordinates);
	}

	/// <summary>
	/// Get the cell with specific <see cref="HexCoordinates"/>.
	/// </summary>
	/// <param name="coordinates"><see cref="HexCoordinates"/>
	/// of the cell.</param>
	/// <returns>The cell with the given coordinates, if it exists.</returns>
	public HexCell GetCell(HexCoordinates coordinates)
	{
		int z = coordinates.Z;
		int x = coordinates.X + z / 2;
		if (z < 0 || z >= CellCountZ || x < 0 || x >= CellCountX)
		{
			return default;
		}
		return new HexCell(x + z * CellCountX, this);
	}

	/// <summary>
	/// Try to get the cell with specific <see cref="HexCoordinates"/>.
	/// </summary>
	/// <param name="coordinates"><see cref="HexCoordinates"/>
	/// of the cell.</param>
	/// <param name="cell">The cell, if it exists.</param>
	/// <returns>Whether the cell exists.</returns>
	public bool TryGetCell(HexCoordinates coordinates, out HexCell cell)
	{
		int z = coordinates.Z;
		int x = coordinates.X + z / 2;
		if (z < 0 || z >= CellCountZ || x < 0 || x >= CellCountX)
		{
			cell = default;
			return false;
		}
		cell = new HexCell(x + z * CellCountX, this);
		return true;
	}

	/// <summary>
	/// Try to get the cell index for specific <see cref="HexCoordinates"/>.
	/// </summary>
	/// <param name="coordinates"><see cref="HexCoordinates"/>
	/// of the cell.</param>
	/// <param name="cell">The cell index, if it exists, otherwise -1.</param>
	/// <returns>Whether the cell index exists.</returns>
	public bool TryGetCellIndex(HexCoordinates coordinates, out int cellIndex)
	{
		int z = coordinates.Z;
		int x = coordinates.X + z / 2;
		if (z < 0 || z >= CellCountZ || x < 0 || x >= CellCountX)
		{
			cellIndex = -1;
			return false;
		}
		cellIndex = x + z * CellCountX;
		return true;
	}

	/// <summary>
	/// Get the cell index with specific offset coordinates.
	/// </summary>
	/// <param name="xOffset">X array offset coordinate.</param>
	/// <param name="zOffset">Z array offset coordinate.</param>
	/// <returns>Cell index.</returns>
	public int GetCellIndex(int xOffset, int zOffset) =>
		xOffset + zOffset * CellCountX;

	/// <summary>
	/// Get the cell with a specific index.
	/// </summary>
	/// <param name="cellIndex">Cell index, which should be valid.</param>
	/// <returns>The indicated cell.</returns>
	public HexCell GetCell(int cellIndex) => new(cellIndex, this);

	/// <summary>
	/// Check whether a cell is visibile.
	/// </summary>
	/// <param name="cellIndex">Index of the cell to check.</param>
	/// <returns>Whether the cell is visible.</returns>
	public bool IsCellVisible(int cellIndex) => cellVisibility[cellIndex] > 0;

	/// <summary>
	/// Control whether the map UI should be visible or hidden.
	/// </summary>
	/// <param name="visible">Whether the UI should be visibile.</param>
	public void ShowUI(bool visible)
	{
		for (int i = 0; i < chunks.Length; i++)
		{
			chunks[i].ShowUI(visible);
		}
	}

	void CreateCell(int x, int z, int i)
	{
		Vector3 position;
		position.x = (x + z * 0.5f - z / 2) * HexMetrics.innerDiameter;
		position.y = 0f;
		position.z = z * (HexMetrics.outerRadius * 1.5f);

		var cell = new HexCell(i, this);
		CellPositions[i] = position;
		CellData[i].coordinates = HexCoordinates.FromOffsetCoordinates(x, z);

		bool explorable = Wrapping ?
			z > 0 && z < CellCountZ - 1 :
			x > 0 && z > 0 && x < CellCountX - 1 && z < CellCountZ - 1;
		cell.Flags = explorable ?
			cell.Flags.With(HexFlags.Explorable) :
			cell.Flags.Without(HexFlags.Explorable);

		Text label = Instantiate(cellLabelPrefab);
		label.rectTransform.anchoredPosition =
			new Vector2(position.x, position.z);
		RectTransform rect = cellUIRects[i] = label.rectTransform;

		cell.Values = cell.Values.WithElevation(0);
		RefreshCellPosition(i);

		int chunkX = x / HexMetrics.chunkSizeX;
		int chunkZ = z / HexMetrics.chunkSizeZ;
		HexGridChunk chunk = chunks[chunkX + chunkZ * chunkCountX];

		int localX = x - chunkX * HexMetrics.chunkSizeX;
		int localZ = z - chunkZ * HexMetrics.chunkSizeZ;
		cellGridChunks[i] = chunk;
		chunk.AddCell(localX + localZ * HexMetrics.chunkSizeX, i, rect);
	}

	/// <summary>
	/// Refresh the chunk the cell is part of.
	/// </summary>
	/// <param name="cellIndex">Cell index.</param>
	public void RefreshCell(int cellIndex) =>
		cellGridChunks[cellIndex].Refresh();

	/// <summary>
	/// Refresh the cell, all its neighbors, and its unit.
	/// </summary>
	/// <param name="cellIndex">Cell index.</param>
	public void RefreshCellWithDependents (int cellIndex)
	{
		HexGridChunk chunk = cellGridChunks[cellIndex];
		chunk.Refresh();
		HexCoordinates coordinates = CellData[cellIndex].coordinates;
		for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
		{
			if (TryGetCellIndex(coordinates.Step(d), out int neighborIndex))
			{
				HexGridChunk neighborChunk = cellGridChunks[neighborIndex];
				if (chunk != neighborChunk)
				{
					neighborChunk.Refresh();
				}
			}
		}
        foreach (HexUnit unit in CellUnits[cellIndex])
            unit.ValidateLocation();
    }

    /// <summary>
    /// Recalculate and apply the visual offset position for every unit
    /// currently on a cell, so co-occupying units spread out and don't stack.
    /// Call this whenever a unit arrives at or departs from a cell.
    /// </summary>
    public void RefreshCellUnitPositions(int cellIndex)
    {
        List<HexUnit> list = CellUnits[cellIndex];
        for (int i = 0; i < list.Count; i++)
            list[i].UpdateCellPosition();
    }

    /// <summary>
    /// Refresh the world position of a cell.
    /// </summary>
    /// <param name="cellIndex">Cell index.</param>
    public void RefreshCellPosition (int cellIndex)
	{
		Vector3 position = CellPositions[cellIndex];
		position.y = CellData[cellIndex].Elevation * HexMetrics.elevationStep;
		position.y +=
			(HexMetrics.SampleNoise(position).y * 2f - 1f) *
			HexMetrics.elevationPerturbStrength;
		CellPositions[cellIndex] = position;

		RectTransform rectTransform = cellUIRects[cellIndex];
		Vector3 uiPosition = rectTransform.localPosition;
		uiPosition.z = -position.y;
		rectTransform.localPosition = uiPosition;
	}

	/// <summary>
	/// Refresh all cells, to be done after generating a map.
	/// </summary>
	public void RefreshAllCells()
	{
		for (int i = 0; i < CellData.Length; i++)
		{
			SearchData[i].searchPhase = 0;
			RefreshCellPosition(i);
			ShaderData.RefreshTerrain(i);
			ShaderData.RefreshVisibility(i);
		}
	}

	/// <summary>
	/// Save the map.
	/// </summary>
	/// <param name="writer"><see cref="BinaryWriter"/> to use.</param>
	public void Save(BinaryWriter writer)
	{
		writer.Write(CellCountX);
		writer.Write(CellCountZ);
		writer.Write(Wrapping);

		for (int i = 0; i < CellData.Length; i++)
		{
			HexCellData data = CellData[i];
			data.values.Save(writer);
			data.flags.Save(writer);
		}

		writer.Write(units.Count);
		for (int i = 0; i < units.Count; i++)
		{
			units[i].Save(writer);
		}
	}

	/// <summary>
	/// Load the map.
	/// </summary>
	/// <param name="reader"><see cref="BinaryReader"/> to use.</param>
	/// <param name="header">Header version.</param>
	public void Load(BinaryReader reader, int header)
	{
		ClearPath();
		ClearUnits();
		int x = 20, z = 15;
		if (header >= 1)
		{
			x = reader.ReadInt32();
			z = reader.ReadInt32();
		}
		bool wrapping = header >= 5 && reader.ReadBoolean();
		if (x != CellCountX || z != CellCountZ || this.Wrapping != wrapping)
		{
			if (!CreateMap(x, z, wrapping))
			{
				return;
			}
		}

		bool originalImmediateMode = cellShaderData.ImmediateMode;
		cellShaderData.ImmediateMode = true;

		for (int i = 0; i < CellData.Length; i++)
		{
			HexCellData data = CellData[i];
			data.values = HexValues.Load(reader, header);
			data.flags = data.flags.Load(reader, header);
			CellData[i] = data;
			RefreshCellPosition(i);
			ShaderData.RefreshTerrain(i);
			ShaderData.RefreshVisibility(i);
		}
		for (int i = 0; i < chunks.Length; i++)
		{
			chunks[i].Refresh();
		}

		if (header >= 2)
		{
			int unitCount = reader.ReadInt32();
			for (int i = 0; i < unitCount; i++)
			{
				HexUnit.Load(reader, this);
			}
		}

		cellShaderData.ImmediateMode = originalImmediateMode;
	}

	/// <summary>
	/// Get a list of cell indices representing the currently visible path.
	/// </summary>
	/// <returns>The current path list, if a visible path exists.</returns>
	public List<int> GetPath()
	{
		if (!currentPathExists)
		{
			return null;
		}
		List<int> path = ListPool<int>.Get();
		for (int i = currentPathToIndex;
			i != currentPathFromIndex;
			i = searchData[i].pathFrom)
		{
			path.Add(i);
		}
		path.Add(currentPathFromIndex);
		path.Reverse();
		return path;
	}

	void SetLabel(int cellIndex, string text) =>
		cellUIRects[cellIndex].GetComponent<Text>().text = text;

	void DisableHighlight(int cellIndex) =>
		cellUIRects[cellIndex].GetChild(0).GetComponent<Image>().enabled =
			false;

	void EnableHighlight(int cellIndex, Color color)
	{
		Image highlight =
			cellUIRects[cellIndex].GetChild(0).GetComponent<Image>();
		highlight.color = color;
		highlight.enabled = true;
	}

	/// <summary>
	/// Clear the current path.
	/// </summary>
	public void ClearPath()
	{
		if (currentPathExists)
		{
			int currentIndex = currentPathToIndex;
			while (currentIndex != currentPathFromIndex)
			{
				SetLabel(currentIndex, null);
				DisableHighlight(currentIndex);
				currentIndex = searchData[currentIndex].pathFrom;
			}
			DisableHighlight(currentIndex);
			currentPathExists = false;
		}
		else if (currentPathFromIndex >= 0)
		{
			DisableHighlight(currentPathFromIndex);
			DisableHighlight(currentPathToIndex);
		}
		currentPathFromIndex = currentPathToIndex = -1;
	}

	void ShowPath(int speed)
	{
		if (currentPathExists)
		{
			int currentIndex = currentPathToIndex;
			while (currentIndex != currentPathFromIndex)
			{
                SetLabel(currentIndex, searchData[currentIndex].distance.ToString());
                EnableHighlight(currentIndex, Color.white);
				currentIndex = searchData[currentIndex].pathFrom;
			}
		}
		EnableHighlight(currentPathFromIndex, Color.blue);
		EnableHighlight(currentPathToIndex, Color.red);
	}

	/// <summary>
	/// Try to find a path.
	/// </summary>
	/// <param name="fromCell">Cell to start the search from.</param>
	/// <param name="toCell">Cell to find a path towards.</param>
	/// <param name="unit">Unit for which the path is.</param>
	public void FindPath(HexCell fromCell, HexCell toCell, HexUnit unit)
	{
		ClearPath();
		currentPathFromIndex = fromCell.Index;
		currentPathToIndex = toCell.Index;
		currentPathExists = Search(fromCell, toCell, unit);
		ShowPath(unit.Speed);
	}

	bool Search(HexCell fromCell, HexCell toCell, HexUnit unit)
	{
		int speed = unit.Speed;
		searchFrontierPhase += 2;
		searchFrontier ??= new HexCellPriorityQueue(this);
		searchFrontier.Clear();
		
		searchData[fromCell.Index] = new HexCellSearchData
		{
			searchPhase = searchFrontierPhase
		};
		searchFrontier.Enqueue(fromCell.Index);
		while (searchFrontier.TryDequeue(out int currentIndex))
		{
			var current = new HexCell(currentIndex, this);
			int currentDistance = searchData[currentIndex].distance;
			searchData[currentIndex].searchPhase += 1;

			if (current == toCell)
			{
				return true;
			}

			int currentTurn = (currentDistance - 1) / speed;

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (!current.TryGetNeighbor(d, out HexCell neighbor))
				{
					continue;
				}
				HexCellSearchData neighborData = searchData[neighbor.Index];
				if (neighborData.searchPhase > searchFrontierPhase ||
					!unit.IsValidDestination(neighbor))
				{
					continue;
				}
				int moveCost = unit.GetMoveCost(current, neighbor, d);
				if (moveCost < 0)
				{
					continue;
				}

				int distance = currentDistance + moveCost;
				int turn = (distance - 1) / speed;
				if (turn > currentTurn)
				{
					distance = turn * speed + moveCost;
				}

				if (neighborData.searchPhase < searchFrontierPhase)
				{
					searchData[neighbor.Index] = new HexCellSearchData
					{
						searchPhase = searchFrontierPhase,
						distance = distance,
						pathFrom = currentIndex,
						heuristic = neighbor.Coordinates.DistanceTo(
							toCell.Coordinates)
					};
					searchFrontier.Enqueue(neighbor.Index);
				}
				else if (distance < neighborData.distance)
				{
					searchData[neighbor.Index].distance = distance;
					searchData[neighbor.Index].pathFrom = currentIndex;
					searchFrontier.Change(
						neighbor.Index, neighborData.SearchPriority);
				}
			}
		}
		return false;
	}

    /// <summary>
    /// Increase the visibility of all cells relative to a view cell.
    /// Uses flat distance instead of elevation line-of-sight.
    /// </summary>
    public void IncreaseVisibility(HexCell fromCell, int range)
    {
        List<HexCell> cells = GetCellsInRange(fromCell, range); // ← changed
        for (int i = 0; i < cells.Count; i++)
        {
            int cellIndex = cells[i].Index;
            if (++cellVisibility[cellIndex] == 1)
            {
                HexCell c = cells[i];
                c.Flags = c.Flags.With(HexFlags.Explored);
                cellShaderData.RefreshVisibility(cellIndex);
            }
        }
        ListPool<HexCell>.Add(cells);
    }

    /// <summary>
    /// Decrease the visibility of all cells relative to a view cell.
    /// Uses flat distance instead of elevation line-of-sight.
    /// </summary>
    public void DecreaseVisibility(HexCell fromCell, int range)
    {
        List<HexCell> cells = GetCellsInRange(fromCell, range); // ← changed
        for (int i = 0; i < cells.Count; i++)
        {
            int cellIndex = cells[i].Index;
            if (--cellVisibility[cellIndex] == 0)
            {
                cellShaderData.RefreshVisibility(cellIndex);
            }
        }
        ListPool<HexCell>.Add(cells);
    }

    /// <summary>
    /// Returns every cell within <paramref name="range"/> hex steps of
    /// <paramref name="fromCell"/> using plain BFS — no elevation or
    /// line-of-sight checks. Add this method alongside GetVisibleCells.
    /// </summary>
    List<HexCell> GetCellsInRange(HexCell fromCell, int range)
    {
        List<HexCell> result = ListPool<HexCell>.Get();
        var visited = new HashSet<int>();
        var queue = new Queue<(HexCell cell, int dist)>();

        visited.Add(fromCell.Index);
        queue.Enqueue((fromCell, 0));

        while (queue.Count > 0)
        {
            var (cell, dist) = queue.Dequeue();
            result.Add(cell);

            if (dist >= range) continue;

            for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
            {
                if (!cell.TryGetNeighbor(d, out HexCell neighbor)) continue;
                if (visited.Contains(neighbor.Index)) continue;
                visited.Add(neighbor.Index);
                queue.Enqueue((neighbor, dist + 1));
            }
        }

        return result;
    }

    /// <summary>
    /// Reset visibility of the entire map, viewing only from units whose
    /// team is currently active (i.e. <see cref="HexUnit.TeamVisibilityActive"/>
    /// is <c>true</c>). This preserves team-based fog of war when visibility
    /// is reset due to elevation changes.
    /// </summary>
    public void ResetVisibility()
    {
        for (int i = 0; i < cellVisibility.Length; i++)
        {
            if (cellVisibility[i] > 0)
            {
                cellVisibility[i] = 0;
                cellShaderData.RefreshVisibility(i);
            }
        }
        for (int i = 0; i < units.Count; i++)
        {
            HexUnit unit = units[i];
            // Only restore visibility for units whose team is active.
            // Inactive-team units have teamVisibilityActive == false and
            // must not contribute to the current team's fog of war.
            if (unit.TeamVisibilityActive)
            {
                IncreaseVisibility(unit.Location, unit.VisionRange);
            }
        }
    }

    List<HexCell> GetVisibleCells(HexCell fromCell, int range)
	{
		List<HexCell> visibleCells = ListPool<HexCell>.Get();

		searchFrontierPhase += 2;
		searchFrontier ??= new HexCellPriorityQueue(this);
		searchFrontier.Clear();

		range += fromCell.Values.ViewElevation;
		searchData[fromCell.Index] = new HexCellSearchData
		{
			searchPhase = searchFrontierPhase,
			pathFrom = searchData[fromCell.Index].pathFrom
		};
		searchFrontier.Enqueue(fromCell.Index);
		HexCoordinates fromCoordinates = fromCell.Coordinates;
		while (searchFrontier.TryDequeue(out int currentIndex))
		{
			var current = new HexCell(currentIndex, this);
			searchData[currentIndex].searchPhase += 1;
			visibleCells.Add(current);

			for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
			{
				if (!current.TryGetNeighbor(d, out HexCell neighbor))
				{
					continue;
				}
				HexCellSearchData currentData = searchData[neighbor.Index];
				if (currentData.searchPhase > searchFrontierPhase ||
					neighbor.Flags.HasNone(HexFlags.Explorable))
				{
					continue;
				}

				int distance = searchData[currentIndex].distance + 1;
				if (distance + neighbor.Values.ViewElevation > range ||
					distance > fromCoordinates.DistanceTo(neighbor.Coordinates))
				{
					continue;
				}

				if (currentData.searchPhase < searchFrontierPhase)
				{
					searchData[neighbor.Index] = new HexCellSearchData
					{
						searchPhase = searchFrontierPhase,
						distance = distance,
						pathFrom = currentData.pathFrom
					};
					searchFrontier.Enqueue(neighbor.Index);
				}
				else if (distance < searchData[neighbor.Index].distance)
				{
					searchData[neighbor.Index].distance = distance;
					searchFrontier.Change(
						neighbor.Index, currentData.SearchPriority);
				}
			}
		}
		return visibleCells;
	}

	/// <summary>
	/// Center the map given an X position, to facilitate east-west wrapping.
	/// </summary>
	/// <param name="xPosition">X position.</param>
	public void CenterMap(float xPosition)
	{
		int centerColumnIndex = (int)
			(xPosition / (HexMetrics.innerDiameter * HexMetrics.chunkSizeX));
		
		if (centerColumnIndex == currentCenterColumnIndex)
		{
			return;
		}
		currentCenterColumnIndex = centerColumnIndex;

		int minColumnIndex = centerColumnIndex - chunkCountX / 2;
		int maxColumnIndex = centerColumnIndex + chunkCountX / 2;

		Vector3 position;
		position.y = position.z = 0f;
		for (int i = 0; i < columns.Length; i++)
		{
			if (i < minColumnIndex)
			{
				position.x = chunkCountX *
					(HexMetrics.innerDiameter * HexMetrics.chunkSizeX);
			}
			else if (i > maxColumnIndex)
			{
				position.x = chunkCountX *
					-(HexMetrics.innerDiameter * HexMetrics.chunkSizeX);
			}
			else
			{
				position.x = 0f;
			}
			columns[i].localPosition = position;
		}
	}
    /// <summary>
    /// Seed cell resource amounts from the wood/stone levels written by
    /// the map generator. Call this once after GenerateMap completes.
    /// </summary>
    public void InitializeCellResources(int resourcesPerLevel)
    {
        for (int i = 0; i < CellData.Length; i++)
        {
            CellWoodResources[i] = CellData[i].WoodLevel * resourcesPerLevel;
            CellStoneResources[i] = CellData[i].StoneLevel * resourcesPerLevel;
        }
    }

    /// <summary>
    /// Remove up to <paramref name="requested"/> wood from a cell, update its
    /// wood level, and refresh the chunk. Returns the amount actually removed.
    /// </summary>
    public int GatherWood(int cellIndex, int requested, int resourcesPerLevel)
    {
        int gathered = Mathf.Min(requested, CellWoodResources[cellIndex]);
        if (gathered <= 0) return 0;

        CellWoodResources[cellIndex] -= gathered;
        int newLevel = ResourceLevel(CellWoodResources[cellIndex], resourcesPerLevel);
        // SetWoodLevel already calls Refresh() which retriggers chunk triangulation,
        // so the feature prefab count updates automatically.
        GetCell(cellIndex).SetWoodLevel(newLevel);
        return gathered;
    }

    /// <summary>
    /// Remove up to <paramref name="requested"/> stone from a cell, update its
    /// stone level, and refresh the chunk. Returns the amount actually removed.
    /// </summary>
    public int GatherStone(int cellIndex, int requested, int resourcesPerLevel)
    {
        int gathered = Mathf.Min(requested, CellStoneResources[cellIndex]);
        if (gathered <= 0) return 0;

        CellStoneResources[cellIndex] -= gathered;
        int newLevel = ResourceLevel(CellStoneResources[cellIndex], resourcesPerLevel);
        GetCell(cellIndex).SetStoneLevel(newLevel);
        return gathered;
    }

    /// <summary>
    /// Ceiling-division level: 0 resources → 0, 1..100 → 1, 101..200 → 2, etc.
    /// </summary>
    static int ResourceLevel(int resources, int resourcesPerLevel) =>
        resources <= 0 ? 0 : (resources - 1) / resourcesPerLevel + 1;

}