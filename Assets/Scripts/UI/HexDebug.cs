using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.EventSystems;

public class HexDebug : MonoBehaviour
{
    [SerializeField] HexGrid hexGrid;

    InputAction positionAction;

    bool debugEnabled;
    HexCell previousCell; // track previous cell
    HexCell currentCell;
    bool hasCell;

    Vector2 panelOffset = new Vector2(15f, 15f);
    Vector2 panelPosition;
    Vector2 panelVelocity;
    float panelSmoothTime = 0.05f;

    // Font / line height
    float lineHeight = 30f;

    void Awake()
    {
        positionAction = InputSystem.actions.FindAction("Position");
    }

    public void SetDebugMode(bool enabled)
    {
        debugEnabled = enabled;
    }

    void Update()
    {
        if (!debugEnabled || EventSystem.current.IsPointerOverGameObject())
        {
            hasCell = false;
            return;
        }

        HexCell cell = hexGrid.GetCell(
            Camera.main.ScreenPointToRay(positionAction.ReadValue<Vector2>()),
            previousCell
        );

        // Use only if cell is valid
        hasCell = !cell.Equals(default(HexCell));
        if (hasCell) currentCell = cell;

        previousCell = cell;

        // Smooth panel follow
        Vector2 targetPosition = positionAction.ReadValue<Vector2>() + panelOffset;
        panelPosition = Vector2.SmoothDamp(panelPosition, targetPosition, ref panelVelocity, panelSmoothTime);
    }

    void OnGUI()
    {
        if (!debugEnabled || !hasCell) return;

        // Prepare all debug lines
        string[] lines = new string[]
        {
            $"Coordinates: {currentCell.Coordinates}",
            $"Global Index: {currentCell.Index}",
            $"Terrain: {currentCell.Values.TerrainTypeIndex}",
            $"Elevation: {currentCell.Values.Elevation}",
            $"Water Level: {currentCell.Values.WaterLevel}",
            $"Home Level: {currentCell.Values.HomeLevel}",
            $"Stone Level: {currentCell.Values.StoneLevel}",
            $"Wood Level: {currentCell.Values.WoodLevel}",
            $"Unit: {(currentCell.Unit ? "Yes" : "No")}"
        };

        // Calculate panel height dynamically
        float panelHeight = (lines.Length + 1) * lineHeight;
        float panelWidth = 200f;

        Rect panelRect = new Rect(
            panelPosition.x,
            Screen.height - panelPosition.y - panelHeight,
            panelWidth,
            panelHeight
        );

        GUI.Box(panelRect, "Hex Debug Info");

        GUILayout.BeginArea(panelRect);
        GUILayout.Space(20); // leave space for box title

        foreach (var line in lines)
        {
            GUILayout.Label(line);
        }

        GUILayout.EndArea();
    }
}