using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

/// <summary>
/// Component that applies UI commands to the hex map.
/// Public methods are hooked up to the in-game UI.
/// </summary>
public class HexMapEditor : MonoBehaviour
{
	static readonly int cellHighlightingId = Shader.PropertyToID(
		"_CellHighlighting");

	[SerializeField]
	HexGrid hexGrid;

	[SerializeField]
	HexGameUI gameUI;

	[SerializeField]
	NewMapMenu newMapMenu;

	[SerializeField]
	SaveLoadMenu saveLoadMenu;

	[SerializeField]
	Material terrainMaterial;

	[SerializeField]
	UIDocument sidePanels;

    [SerializeField]
	HexDebug hexDebug;

    int activeElevation;
	int activeWaterLevel;

	int activeHomeLevel, activeStoneLevel, activeWoodLevel;

	int activeTerrainTypeIndex;

	int brushSize;

	bool applyElevation = true;
	bool applyWaterLevel = true;

	bool applyHomeLevel, applyStoneLevel, applyWoodLevel;

	enum OptionalToggle
	{
		Ignore, Yes, No
	}

	OptionalToggle riverMode, roadMode, walledMode;

	bool isDrag;
	HexDirection dragDirection;
	HexCell previousCell;

	InputAction interactAction, positionAction;
	
	InputAction createUnitAction, destroyUnitAction;

	void Awake()
	{
        terrainMaterial.DisableKeyword("_SHOW_GRID");
        Shader.DisableKeyword("_HEX_MAP_EDIT_MODE"); // starts in game mode
        enabled = false;                              // editor input off until toggled on

        VisualElement root = sidePanels.rootVisualElement;

        root.Q<Toggle>("DebugMode").RegisterValueChangedCallback(change => {
            hexDebug.SetDebugMode(change.newValue);
        });

        root.Q<RadioButtonGroup>("Terrain").RegisterValueChangedCallback(
			change => activeTerrainTypeIndex = change.newValue - 1);

		root.Q<Toggle>("ApplyElevation").RegisterValueChangedCallback(
			change => applyElevation = change.newValue);
		root.Q<SliderInt>("Elevation").RegisterValueChangedCallback(
			change => activeElevation = change.newValue);

		root.Q<Toggle>("ApplyWaterLevel").RegisterValueChangedCallback(
			change => applyWaterLevel = change.newValue);
		root.Q<SliderInt>("WaterLevel").RegisterValueChangedCallback(
			change => activeWaterLevel = change.newValue);
		
		root.Q<RadioButtonGroup>("River").RegisterValueChangedCallback(
			change => riverMode = (OptionalToggle)change.newValue);
		
		root.Q<RadioButtonGroup>("Roads").RegisterValueChangedCallback(
			change => roadMode = (OptionalToggle)change.newValue);

		root.Q<SliderInt>("BrushSize").RegisterValueChangedCallback(
			change => brushSize = change.newValue);

		root.Q<Toggle>("ApplyHomeLevel").RegisterValueChangedCallback(
			change => applyHomeLevel = change.newValue);
		root.Q<SliderInt>("HomeLevel").RegisterValueChangedCallback(
			change => activeHomeLevel = change.newValue);
		
		root.Q<Toggle>("ApplyStoneLevel").RegisterValueChangedCallback(
			change => applyStoneLevel = change.newValue);
		root.Q<SliderInt>("StoneLevel").RegisterValueChangedCallback(
			change => activeStoneLevel = change.newValue);
		
		root.Q<Toggle>("ApplyWoodLevel").RegisterValueChangedCallback(
			change => applyWoodLevel = change.newValue);
		root.Q<SliderInt>("WoodLevel").RegisterValueChangedCallback(
			change => activeWoodLevel = change.newValue);

		root.Q<RadioButtonGroup>("Walled").RegisterValueChangedCallback(
			change => walledMode = (OptionalToggle)change.newValue);
		
		root.Q<Button>("SaveButton").clicked += () => saveLoadMenu.Open(true);
		root.Q<Button>("LoadButton").clicked += () => saveLoadMenu.Open(false);

		root.Q<Button>("NewMapButton").clicked += newMapMenu.Open;

		root.Q<Toggle>("Grid").RegisterValueChangedCallback(change => {
			if (change.newValue)
			{
				terrainMaterial.EnableKeyword("_SHOW_GRID");
			}
			else
			{
				terrainMaterial.DisableKeyword("_SHOW_GRID");
			}
		});

        VisualElement leftPanel = root.Q<VisualElement>("LeftPanel");
        VisualElement editorControls = root.Q<VisualElement>("EditorControls");

        root.Q<Toggle>("EditMode").RegisterValueChangedCallback(change => {
            enabled = change.newValue;
            gameUI.SetEditMode(change.newValue);

            // Show / hide editor-only panels.
            DisplayStyle editorStyle = change.newValue ? DisplayStyle.Flex : DisplayStyle.None;
            if (leftPanel != null) leftPanel.style.display = editorStyle;
            if (editorControls != null) editorControls.style.display = editorStyle;
        });

        interactAction = InputSystem.actions.FindAction("Interact");
		positionAction = InputSystem.actions.FindAction("Position");
		createUnitAction = InputSystem.actions.FindAction("CreateUnit");
		destroyUnitAction = InputSystem.actions.FindAction("Destroyunit");
    }

    void Update()
	{
		if (!EventSystem.current.IsPointerOverGameObject())
		{
			if (interactAction.inProgress)
			{
				HandleInput();
				return;
			}
			else
			{
				// Potential optimization:
				// only do this if camera or cursor has changed.
				UpdateCellHighlightData(GetCellUnderCursor());
			}
			if (destroyUnitAction.WasPerformedThisFrame())
			{
				DestroyUnit();
				return;
			}
			if (createUnitAction.WasPerformedThisFrame())
			{
				CreateUnit();
				return;
			}
		}
		else
		{
			ClearCellHighlightData();
		}
		previousCell = default;
	}

	HexCell GetCellUnderCursor() => hexGrid.GetCell(
		Camera.main.ScreenPointToRay(positionAction.ReadValue<Vector2>()),
		previousCell);

	void CreateUnit()
	{
		HexCell cell = GetCellUnderCursor();
		if (cell && !cell.Unit)
		{
			hexGrid.AddUnit(
				Instantiate(HexUnit.unitPrefab), cell, Random.Range(0f, 360f)
			);
		}
	}

	void DestroyUnit()
	{
		HexCell cell = GetCellUnderCursor();
		if (cell && cell.Unit)
		{
			hexGrid.RemoveUnit(cell.Unit);
		}
	}

	void HandleInput()
	{
		HexCell currentCell = GetCellUnderCursor();
		if (currentCell)
		{
			if (previousCell && previousCell != currentCell)
			{
				ValidateDrag(currentCell);
			}
			else
			{
				isDrag = false;
			}
			EditCells(currentCell);
			previousCell = currentCell;
		}
		else
		{
			previousCell = default;
		}
		UpdateCellHighlightData(currentCell);
	}

	void UpdateCellHighlightData(HexCell cell)
	{
		if (!cell)
		{
			ClearCellHighlightData();
			return;
		}

		// Works up to brush size 6.
		Shader.SetGlobalVector(
			cellHighlightingId,
			new Vector4(
				cell.Coordinates.HexX,
				cell.Coordinates.HexZ,
				brushSize * brushSize + 0.5f,
				HexMetrics.wrapSize
			)
		);
	}

	void ClearCellHighlightData() => Shader.SetGlobalVector(
		cellHighlightingId, new Vector4(0f, 0f, -1f, 0f));

	void ValidateDrag(HexCell currentCell)
	{
		for (dragDirection = HexDirection.NE;
			dragDirection <= HexDirection.NW;
			dragDirection++)
		{
			if (previousCell.GetNeighbor(dragDirection) ==
				currentCell)
			{
				isDrag = true;
				return;
			}
		}
		isDrag = false;
	}

	void EditCells(HexCell center)
	{
		int centerX = center.Coordinates.X;
		int centerZ = center.Coordinates.Z;

		for (int r = 0, z = centerZ - brushSize; z <= centerZ; z++, r++)
		{
			for (int x = centerX - r; x <= centerX + brushSize; x++)
			{
				EditCell(hexGrid.GetCell(new HexCoordinates(x, z)));
			}
		}
		for (int r = 0, z = centerZ + brushSize; z > centerZ; z--, r++)
		{
			for (int x = centerX - brushSize; x <= centerX + r; x++)
			{
				EditCell(hexGrid.GetCell(new HexCoordinates(x, z)));
			}
		}
	}

	void EditCell(HexCell cell)
	{
		if (cell)
		{
			if (activeTerrainTypeIndex >= 0)
			{
				cell.SetTerrainTypeIndex(activeTerrainTypeIndex);
			}
			if (applyElevation)
			{
				cell.SetElevation(activeElevation);
			}
			if (applyWaterLevel)
			{
				cell.SetWaterLevel(activeWaterLevel);
			}
			if (applyHomeLevel)
			{
				cell.SetHomeLevel(activeHomeLevel);
			}
			if (applyStoneLevel)
			{
				cell.SetStoneLevel(activeStoneLevel);
			}
			if (applyWoodLevel)
			{
				cell.SetWoodLevel(activeWoodLevel);
			}
			if (riverMode == OptionalToggle.No)
			{
				cell.RemoveRiver();
			}
			if (roadMode == OptionalToggle.No)
			{
				cell.RemoveRoads();
			}
			if (walledMode != OptionalToggle.Ignore)
			{
				cell.SetWalled(walledMode == OptionalToggle.Yes);
			}
			if (isDrag && cell.TryGetNeighbor(
				dragDirection.Opposite(), out HexCell otherCell))
			{
				if (riverMode == OptionalToggle.Yes)
				{
					otherCell.SetOutgoingRiver(dragDirection);
				}
				if (roadMode == OptionalToggle.Yes)
				{
					otherCell.AddRoad(dragDirection);
				}
			}
		}
	}
}
