using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UIElements;

/// <summary>
/// Manages all in-game input: selection, movement, resource actions,
/// home upgrade, attacking, raising enemy homes, and claiming victory.
/// </summary>
public class HexGameUI : MonoBehaviour
{
    [SerializeField] HexGrid grid;
    [SerializeField] UIDocument sidePanels;

    [Tooltip("When true the game starts in map-editor mode. " +
             "Uncheck to start directly in game mode (recommended for play).")]
    [SerializeField] bool startInEditMode = false;

    HexCell currentCell;
    HexUnit selectedUnit;

    InputAction selectAction, commandAction, positionAction;

    Label turnInfoLabel;
    Button endTurnButton;
    Button gatherWoodButton;
    Button gatherStoneButton;
    Button depositButton;
    Button upgradeHomeButton;
    Button raiseHomeButton;
    Button claimVictoryButton;

    void Awake()
    {
        selectAction = InputSystem.actions.FindAction("Interact");
        commandAction = InputSystem.actions.FindAction("Command");
        positionAction = InputSystem.actions.FindAction("Position");
    }

    void Start()
    {
        if (sidePanels == null)
        { Debug.LogError("[HexGameUI] sidePanels UIDocument not assigned."); return; }

        VisualElement root = sidePanels.rootVisualElement;
        turnInfoLabel = root.Q<Label>("TurnInfoLabel");
        endTurnButton = root.Q<Button>("EndTurnButton");
        gatherWoodButton = root.Q<Button>("GatherWoodButton");
        gatherStoneButton = root.Q<Button>("GatherStoneButton");
        depositButton = root.Q<Button>("DepositButton");
        upgradeHomeButton = root.Q<Button>("UpgradeHomeButton");
        raiseHomeButton = root.Q<Button>("RaiseHomeButton");
        claimVictoryButton = root.Q<Button>("ClaimVictoryButton");

        if (endTurnButton != null) endTurnButton.clicked += () => { Debug.Log("[HexGameUI] End Turn."); TurnManager.Instance?.EndCurrentUnitTurn(); };
        if (gatherWoodButton != null) gatherWoodButton.clicked += () => TurnManager.Instance?.TryGatherWood();
        if (gatherStoneButton != null) gatherStoneButton.clicked += () => TurnManager.Instance?.TryGatherStone();
        if (depositButton != null) depositButton.clicked += () => TurnManager.Instance?.TryDeposit();
        if (upgradeHomeButton != null) upgradeHomeButton.clicked += () => TurnManager.Instance?.TryUpgradeHome();
        if (raiseHomeButton != null) raiseHomeButton.clicked += () => TurnManager.Instance?.TryRaiseHome();
        if (claimVictoryButton != null) claimVictoryButton.clicked += () => TurnManager.Instance?.TryWinByUpgrade();

        // Apply the initial edit-mode state from the Inspector field.
        // HexMapEditor.Awake has already run (same frame), so SetEditMode here
        // correctly reflects the intended starting state.
        SetEditMode(startInEditMode);
    }

    /// <summary>
    /// Show or hide the game HUD elements.
    /// Called by HexMapEditor when the Edit Mode toggle changes,
    /// and once from Start() using the Inspector value.
    /// </summary>
    public void SetEditMode(bool toggle)
    {
        enabled = !toggle;
        grid.ShowUI(!toggle);
        grid.ClearPath();

        // Game HUD — visible only outside edit mode.
        DisplayStyle gameStyle = toggle ? DisplayStyle.None : DisplayStyle.Flex;
        SetDisplay(turnInfoLabel, gameStyle);
        SetDisplay(endTurnButton, gameStyle);

        // Context-sensitive action buttons always start hidden;
        // UpdateActionButtons() reveals them each frame as appropriate.
        SetDisplay(gatherWoodButton, DisplayStyle.None);
        SetDisplay(gatherStoneButton, DisplayStyle.None);
        SetDisplay(depositButton, DisplayStyle.None);
        SetDisplay(upgradeHomeButton, DisplayStyle.None);
        SetDisplay(raiseHomeButton, DisplayStyle.None);
        SetDisplay(claimVictoryButton, DisplayStyle.None);

        if (toggle) Shader.EnableKeyword("_HEX_MAP_EDIT_MODE");
        else Shader.DisableKeyword("_HEX_MAP_EDIT_MODE");
    }

    void Update()
    {
        UpdateTurnInfo();
        UpdateActionButtons();

        if (!EventSystem.current.IsPointerOverGameObject())
        {
            if (selectAction.WasPerformedThisFrame())
                DoSelection();
            else if (selectedUnit)
            {
                if (commandAction.WasPerformedThisFrame()) DoCommand();
                else DoPathfinding();
            }
        }
    }

    // ======================================================================
    // HUD
    // ======================================================================

    void UpdateTurnInfo()
    {
        if (turnInfoLabel == null) return;
        TurnManager tm = TurnManager.Instance;

        if (tm == null || !tm.Initialized)
        { turnInfoLabel.text = ""; return; }

        if (tm.GameOver)
        {
            // Keep the last active team name visible in the winner message.
            string winner = tm.Teams.Count == 1
                ? $"Team {tm.Teams[0].TeamIndex + 1} wins!"
                : tm.ActiveTeam != null
                    ? $"Team {tm.ActiveTeam.TeamIndex + 1} wins!"
                    : "Draw!";
            turnInfoLabel.text = $"Game Over\n{winner}";
            return;
        }

        if (tm.ActiveTeam == null || tm.ActiveUnit == null)
        { turnInfoLabel.text = ""; return; }

        HexUnit u = tm.ActiveUnit;
        Team team = tm.ActiveTeam;

        string homeHPStr = $"Home Lv{team.HomeLevel} HP: {team.HomeHP}/{team.HomeLevel * 100}";
        string upgradeStr = team.HomeLevel < 3
            ? $"Upgrade: {tm.NextUpgradeWoodCost}W/{tm.NextUpgradeStoneCost}S"
            : $"Victory: {tm.WinWoodCost}W/{tm.WinStoneCost}S";

        turnInfoLabel.text =
            $"Round {tm.RoundNumber}  |  Team {team.TeamIndex + 1}\n" +
            $"HP: {u.CurrentHP}/{u.MaxHP}   AP: {u.ActionPointsRemaining}\n" +
            $"Carry: {u.WoodCarried}W/{u.StoneCarried}S\n" +
            $"Stored: {team.WoodStored}W/{team.StoneStored}S\n" +
            $"{homeHPStr}  {upgradeStr}";
    }

    void UpdateActionButtons()
    {
        TurnManager tm = TurnManager.Instance;
        bool ready = tm != null && tm.Initialized && !tm.GameOver;

        SetDisplay(gatherWoodButton, ready && tm.CanGatherWood() ? DisplayStyle.Flex : DisplayStyle.None);
        SetDisplay(gatherStoneButton, ready && tm.CanGatherStone() ? DisplayStyle.Flex : DisplayStyle.None);
        SetDisplay(depositButton, ready && tm.CanDeposit() ? DisplayStyle.Flex : DisplayStyle.None);
        SetDisplay(upgradeHomeButton, ready && tm.CanUpgradeHome() ? DisplayStyle.Flex : DisplayStyle.None);
        SetDisplay(raiseHomeButton, ready && tm.CanRaiseHome() ? DisplayStyle.Flex : DisplayStyle.None);
        SetDisplay(claimVictoryButton, ready && tm.CanWinByUpgrade() ? DisplayStyle.Flex : DisplayStyle.None);
    }

    // ======================================================================
    // Input
    // ======================================================================

    void DoSelection()
    {
        grid.ClearPath();
        UpdateCurrentCell();
        if (currentCell)
        {
            HexUnit unit = currentCell.Unit;
            selectedUnit = (unit && TurnManager.Instance != null &&
                            TurnManager.Instance.IsActiveUnit(unit)) ? unit : null;
        }
    }

    void DoCommand()
    {
        if (!UpdateCurrentCell()) return;
        TurnManager tm = TurnManager.Instance;

        // Attack adjacent enemy unit cell.
        if (currentCell && tm != null && tm.IsActiveUnit(selectedUnit))
        {
            if (currentCell.HasEnemyUnits(selectedUnit.Team) &&
                IsAdjacentToSelected(currentCell))
            {
                tm.TryAttackUnits(currentCell);
                grid.ClearPath();
                return;
            }
        }

        // Move along existing path.
        if (!grid.HasPath) return;

        if (tm != null && tm.IsActiveUnit(selectedUnit))
        {
            if (tm.TryMoveActiveUnit(grid.GetPath()))
                grid.ClearPath();
        }
        else
        {
            selectedUnit.Travel(grid.GetPath());
            grid.ClearPath();
        }
    }

    void DoPathfinding()
    {
        if (TurnManager.Instance != null &&
            !TurnManager.Instance.IsActiveUnit(selectedUnit))
        { selectedUnit = null; grid.ClearPath(); return; }

        if (UpdateCurrentCell())
        {
            if (currentCell && selectedUnit.IsValidDestination(currentCell))
                grid.FindPath(selectedUnit.Location, currentCell, selectedUnit);
            else
                grid.ClearPath();
        }
    }

    bool IsAdjacentToSelected(HexCell cell)
    {
        if (selectedUnit == null) return false;
        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
            if (selectedUnit.Location.TryGetNeighbor(d, out HexCell nb) && nb == cell)
                return true;
        return false;
    }

    bool UpdateCurrentCell()
    {
        HexCell cell = grid.GetCell(
            Camera.main.ScreenPointToRay(positionAction.ReadValue<Vector2>()));
        if (cell) { currentCell = cell; return true; }
        return false;
    }

    static void SetDisplay(VisualElement el, DisplayStyle style)
    { if (el != null) el.style.display = style; }
}