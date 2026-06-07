using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Manages round-based turns, unit spawning, home placement, resource
/// gathering/depositing, home upgrades, unit combat, and team elimination.
/// Includes ML-Agents scenario support via SetScenario().
/// </summary>
[DefaultExecutionOrder(-100)]
public class TurnManager : MonoBehaviour
{
    // ======================================================================
    // Singleton
    // ======================================================================

    public static TurnManager Instance { get; private set; }

    // ======================================================================
    // Inspector
    // ======================================================================

    [Header("Teams")]
    [SerializeField, Range(1, 4)] int teamCount = 2;
    [SerializeField] int[] unitsPerTeam = { 2, 2, 2, 2 };
    [SerializeField] Material[] teamMaterials;

    [Header("Home Prefabs")]
    [Tooltip("Three prefabs: Home 1, Home 2, Home 3 (index 0 spawns at start).")]
    [SerializeField] Transform[] homePrefabs = new Transform[3];

    [Header("Resources")]
    [SerializeField] int resourcesPerLevel = 100;
    [SerializeField] int gatherAmount = 20;
    [SerializeField] int gatherCost = 5;
    [SerializeField] int maxCarryCapacity = 200;
    [SerializeField] int depositCost = 5;

    [Header("Home Upgrade")]
    [SerializeField] int[] upgradeWoodCost = { 200, 400 };
    [SerializeField] int[] upgradeStoneCost = { 200, 400 };
    [SerializeField] int upgradeCost = 20;

    [Tooltip("Wood required to claim victory (level 3 upgrade).")]
    [SerializeField] int winWoodCost = 1000;

    [Tooltip("Stone required to claim victory (level 3 upgrade).")]
    [SerializeField] int winStoneCost = 1000;

    [Header("Combat")]
    [Tooltip("Damage a unit deals when attacking an enemy unit.")]
    [SerializeField] int unitDamage = 10;

    [Tooltip("AP cost of the Raise Home action.")]
    [SerializeField] int raiseHomeCost = 10;

    [Tooltip("Fraction of an upgrade tier's resource cost given as loot when a " +
             "home level is destroyed. Capped by attacker's carry capacity.")]
    [SerializeField, Range(0f, 1f)] float lootFraction = 0.5f;

    [Tooltip("HP each home level provides (level 1 = 1×, level 2 = 2×, etc.).")]
    [SerializeField] int hpPerHomeLevel = 100;

    [Header("References")]
    [SerializeField] HexGrid grid;
    [SerializeField] HexMapGenerator mapGenerator;
    [SerializeField] HexUnit unitPrefab;

    [Header("Auto-initialise")]
    [SerializeField] bool autoInitializeOnStart = true;
    [SerializeField] int mapSizeX = 20;
    [SerializeField] int mapSizeZ = 15;
    [SerializeField] bool wrapping;

    // ======================================================================
    // Runtime state
    // ======================================================================

    readonly List<Team> teams = new();

    Team activeTeam;
    HexUnit activeUnit;

    // Fixed team order, set once at Initialize.
    int[] teamOrder;
    int currentTeamOrderIndex;

    // Per team-turn unit slot cursor (iterates team.FixedUnitOrder).
    int currentUnitSlot;

    int roundNumber;
    bool initialized;

    // ── Scenario overrides (set by HexMLEnvironment before Initialize) ────
    // Null means "use the Inspector value". Set via SetScenario().
    int? scenarioTeamCount;
    int[] scenarioUnitsPerTeam;
    int[] scenarioUpgradeWoodCost;
    int[] scenarioUpgradeStoneCost;
    int? scenarioWinWoodCost;
    int? scenarioWinStoneCost;

    // ── Active values for the current episode ─────────────────────────────
    // Resolved from Inspector values and scenario overrides in Initialize().
    // All gameplay methods read these instead of the Inspector fields so
    // that scenario-specific costs take effect for the whole episode.
    int activeTeamCount;
    int[] activeUnitsPerTeam;
    int[] activeUpgradeWoodCost;
    int[] activeUpgradeStoneCost;
    int activeWinWoodCost;
    int activeWinStoneCost;

    // ======================================================================
    // Unity lifecycle
    // ======================================================================

    void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
    }

    void Start()
    {
        if (autoInitializeOnStart)
        {
            mapGenerator.GenerateMap(mapSizeX, mapSizeZ, wrapping);
            Initialize();
        }
    }

    // ======================================================================
    // Public API — state
    // ======================================================================

    public Team ActiveTeam => activeTeam;
    public HexUnit ActiveUnit => activeUnit;
    public int RoundNumber => roundNumber;
    public bool Initialized => initialized;
    public bool GameOver { get; private set; }
    public IReadOnlyList<Team> Teams => teams;

    // Config exposed for HexGameUI / HexUnitAgent.
    public int GatherCost => gatherCost;
    public int DepositCost => depositCost;
    public int GatherAmount => gatherAmount;
    public int MaxCarryCapacity => maxCarryCapacity;
    public int UpgradeCost => upgradeCost;
    public int UnitDamage => unitDamage;
    public int RaiseHomeCost => raiseHomeCost;
    public int WinWoodCost => initialized ? activeWinWoodCost : winWoodCost;
    public int WinStoneCost => initialized ? activeWinStoneCost : winStoneCost;

    public int NextUpgradeWoodCost =>
        activeTeam != null && activeTeam.HomeLevel - 1 < activeUpgradeWoodCost.Length
            ? activeUpgradeWoodCost[activeTeam.HomeLevel - 1] : -1;

    public int NextUpgradeStoneCost =>
        activeTeam != null && activeTeam.HomeLevel - 1 < activeUpgradeStoneCost.Length
            ? activeUpgradeStoneCost[activeTeam.HomeLevel - 1] : -1;

    public bool CanControlUnit(HexUnit unit) =>
        initialized && unit != null &&
        activeTeam != null && unit.Team == activeTeam;

    public bool IsActiveUnit(HexUnit unit) =>
        initialized && unit != null && !unit.IsDead && unit == activeUnit;

    // ======================================================================
    // Public API — scenario
    // ======================================================================

    /// <summary>
    /// Override scenario parameters for the next <see cref="Initialize"/> call.
    /// Called by <c>HexMLEnvironment</c> before each training episode.
    /// Pass null to fall back to the Inspector values for that parameter.
    /// </summary>
    public void SetScenario(int teamCount, int[] unitsPerTeam,
                            int[] upgWoodCost = null,
                            int[] upgStoneCost = null,
                            int? winWoodCost = null,
                            int? winStoneCost = null)
    {
        scenarioTeamCount = teamCount;
        scenarioUnitsPerTeam = unitsPerTeam;
        scenarioUpgradeWoodCost = upgWoodCost;
        scenarioUpgradeStoneCost = upgStoneCost;
        scenarioWinWoodCost = winWoodCost;
        scenarioWinStoneCost = winStoneCost;
    }

    // ======================================================================
    // Public API — resource actions
    // ======================================================================

    public bool CanGatherWood()
    {
        if (!initialized || activeUnit == null || activeUnit.IsTraveling) return false;
        return grid.CellWoodResources[activeUnit.Location.Index] > 0 &&
               activeUnit.ActionPointsRemaining >= gatherCost &&
               activeUnit.TotalCarried < maxCarryCapacity;
    }

    public bool CanGatherStone()
    {
        if (!initialized || activeUnit == null || activeUnit.IsTraveling) return false;
        return grid.CellStoneResources[activeUnit.Location.Index] > 0 &&
               activeUnit.ActionPointsRemaining >= gatherCost &&
               activeUnit.TotalCarried < maxCarryCapacity;
    }

    public bool CanDeposit()
    {
        if (!initialized || activeUnit == null || activeUnit.IsTraveling) return false;
        return activeUnit.Location.Index == activeTeam.HomeCellIndex &&
               activeUnit.TotalCarried > 0 &&
               activeUnit.ActionPointsRemaining >= depositCost;
    }

    public bool CanUpgradeHome()
    {
        if (!initialized || activeUnit == null || activeUnit.IsTraveling) return false;
        if (activeUnit.Location.Index != activeTeam.HomeCellIndex) return false;
        if (activeUnit.ActionPointsRemaining < upgradeCost) return false;
        int tier = activeTeam.HomeLevel - 1;
        if (tier >= activeUpgradeWoodCost.Length || tier >= activeUpgradeStoneCost.Length)
            return false;
        return activeUnit.WoodCarried + activeTeam.WoodStored >= activeUpgradeWoodCost[tier] &&
               activeUnit.StoneCarried + activeTeam.StoneStored >= activeUpgradeStoneCost[tier];
    }

    public bool CanWinByUpgrade()
    {
        if (!initialized || GameOver) return false;
        if (activeUnit == null || activeUnit.IsTraveling) return false;
        if (activeTeam.HomeLevel < 3) return false;
        if (activeUnit.Location.Index != activeTeam.HomeCellIndex) return false;
        if (activeUnit.ActionPointsRemaining < upgradeCost) return false;
        return activeUnit.WoodCarried + activeTeam.WoodStored >= activeWinWoodCost &&
               activeUnit.StoneCarried + activeTeam.StoneStored >= activeWinStoneCost;
    }

    public bool CanRaiseHome()
    {
        if (!initialized || activeUnit == null || activeUnit.IsTraveling) return false;
        if (activeUnit.ActionPointsRemaining < raiseHomeCost) return false;
        int cellIdx = activeUnit.Location.Index;
        foreach (Team t in teams)
            if (t != activeTeam && t.HomeCellIndex == cellIdx) return true;
        return false;
    }

    public bool TryGatherWood()
    {
        if (!CanGatherWood()) return false;
        int actual = grid.GatherWood(activeUnit.Location.Index,
                         Mathf.Min(gatherAmount, maxCarryCapacity - activeUnit.TotalCarried),
                         resourcesPerLevel);
        if (actual <= 0) return false;
        activeUnit.GainWood(actual);
        activeUnit.SpendActionPoints(gatherCost);
        return true;
    }

    public bool TryGatherStone()
    {
        if (!CanGatherStone()) return false;
        int actual = grid.GatherStone(activeUnit.Location.Index,
                         Mathf.Min(gatherAmount, maxCarryCapacity - activeUnit.TotalCarried),
                         resourcesPerLevel);
        if (actual <= 0) return false;
        activeUnit.GainStone(actual);
        activeUnit.SpendActionPoints(gatherCost);
        return true;
    }

    public bool TryDeposit()
    {
        if (!CanDeposit()) return false;
        activeUnit.DepositToTeam();
        activeUnit.SpendActionPoints(depositCost);
        return true;
    }

    public bool TryUpgradeHome()
    {
        if (!CanUpgradeHome()) return false;
        int tier = activeTeam.HomeLevel - 1;
        if (!activeTeam.TrySpendResources(activeUnit,
                activeUpgradeWoodCost[tier], activeUpgradeStoneCost[tier]))
            return false;
        activeUnit.SpendActionPoints(upgradeCost);
        activeTeam.HomeLevel++;
        ReplaceHomePrefab(activeTeam);
        Debug.Log($"[TurnManager] Team {activeTeam.TeamIndex} home upgraded " +
                  $"to Lv{activeTeam.HomeLevel}.");
        return true;
    }

    public bool TryWinByUpgrade()
    {
        if (!CanWinByUpgrade()) return false;
        if (!activeTeam.TrySpendResources(activeUnit, activeWinWoodCost, activeWinStoneCost))
            return false;
        activeUnit.SpendActionPoints(upgradeCost);
        Debug.Log($"[TurnManager] *** Team {activeTeam.TeamIndex} claimed VICTORY! ***");
        GameOver = true;
        // Silence every DecisionRequester now that the game is over — the
        // env will end the episode in the next LateUpdate, and any callback
        // between now and then is wasted experience.
        foreach (Team t in teams)
            foreach (HexUnit u in t.Units)
                // (decision pump in HexMLEnvironment is gated on ActiveUnit;
                // no per-unit toggle is needed here)
        TurnManagerEvents.OnGameWon?.Invoke(activeTeam);
        return true;
    }

    public bool TryRaiseHome()
    {
        if (!CanRaiseHome()) return false;
        int cellIdx = activeUnit.Location.Index;
        foreach (Team t in teams)
        {
            if (t != activeTeam && t.HomeCellIndex == cellIdx)
            {
                activeUnit.SpendActionPoints(raiseHomeCost);
                DealHomeDamage(t, activeUnit, unitDamage);
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Total wood needed for <paramref name="team"/> to win from its current
    /// home level: sum of all remaining upgrade tier costs + the victory cost.
    /// HexUnitAgent uses this to detect when the team already has enough
    /// resources and should stop gathering and head home instead.
    /// </summary>
    public int GetTotalWoodToWin(Team team)
    {
        int total = activeWinWoodCost;
        for (int tier = team.HomeLevel - 1; tier < activeUpgradeWoodCost.Length; tier++)
            total += activeUpgradeWoodCost[tier];
        return total;
    }

    /// <summary>
    /// Total stone needed for <paramref name="team"/> to win from its current
    /// home level: sum of all remaining upgrade tier costs + the victory cost.
    /// </summary>
    public int GetTotalStoneToWin(Team team)
    {
        int total = activeWinStoneCost;
        for (int tier = team.HomeLevel - 1; tier < activeUpgradeStoneCost.Length; tier++)
            total += activeUpgradeStoneCost[tier];
        return total;
    }

    // ======================================================================
    // Public API — combat
    // ======================================================================

    /// <summary>
    /// Attack a randomly chosen enemy unit on <paramref name="targetCell"/>.
    /// AP cost equals the move cost to enter that cell.
    /// </summary>
    public bool TryAttackUnits(HexCell targetCell)
    {
        if (!initialized || activeUnit == null || activeUnit.IsTraveling) return false;
        if (!targetCell.HasEnemyUnits(activeTeam)) return false;

        int apCost = activeUnit.GetAdjacentMoveCost(targetCell);
        if (apCost < 0 || activeUnit.ActionPointsRemaining < apCost) return false;

        activeUnit.SpendActionPoints(apCost);

        var defenders = new List<HexUnit>();
        foreach (HexUnit u in targetCell.Units)
            if (u.Team != activeTeam) defenders.Add(u);

        if (defenders.Count == 0) return false;

        HexUnit target = defenders[Random.Range(0, defenders.Count)];
        bool killed = target.TakeDamage(unitDamage);
        TurnManagerEvents.OnUnitDealtDamage?.Invoke(activeUnit, unitDamage);
        if (killed)
        {
            HandleUnitDeath(target);
            TurnManagerEvents.OnUnitKilled?.Invoke(activeUnit, target);
        }

        Debug.Log($"[TurnManager] {activeUnit.name} attacked {target.name} " +
                  $"on {targetCell.Coordinates} for {unitDamage} dmg.");
        return true;
    }

    // ======================================================================
    // Public API — turn management
    // ======================================================================

    public void Initialize()
    {
        teams.Clear();
        activeTeam = null;
        activeUnit = null;
        roundNumber = 0;
        initialized = false;
        GameOver = false;

        // Resolve active values from scenario overrides or Inspector defaults.
        activeTeamCount = scenarioTeamCount ?? teamCount;
        activeUnitsPerTeam = scenarioUnitsPerTeam ?? unitsPerTeam;
        activeUpgradeWoodCost = scenarioUpgradeWoodCost ?? upgradeWoodCost;
        activeUpgradeStoneCost = scenarioUpgradeStoneCost ?? upgradeStoneCost;
        activeWinWoodCost = scenarioWinWoodCost ?? winWoodCost;
        activeWinStoneCost = scenarioWinStoneCost ?? winStoneCost;

        int cellCount = grid.CellData.Length;
        for (int i = 0; i < activeTeamCount; i++)
        {
            Color fallback = i switch
            {
                0 => Color.red,
                1 => new Color(.2f, .4f, 1f),
                2 => Color.green,
                _ => new Color(1f, .6f, 0f)
            };
            teams.Add(new Team(i, fallback, cellCount));
        }

        Random.State prev = Random.state;
        Random.InitState(mapGenerator.Seed ^ 0x6B5C4D3E);

        SpawnAllUnits();

        Random.state = prev;

        grid.InitializeCellResources(resourcesPerLevel);
        initialized = true;

        // Fixed team order — decided once, never reshuffled.
        teamOrder = RandomOrder(teams.Count);
        StartRound();
    }

    public bool TryMoveActiveUnit(List<int> path)
    {
        if (!initialized || activeUnit == null ||
            path == null || path.Count < 2 || activeUnit.IsTraveling) return false;
        int cost = activeUnit.CalculatePathCost(path);
        if (cost < 0 || cost > activeUnit.ActionPointsRemaining) return false;
        activeUnit.SpendActionPoints(cost);
        activeUnit.Travel(path);
        return true;
    }

    public void EndCurrentUnitTurn()
    {
        if (!initialized || GameOver)
        {
            if (GameOver) Debug.Log("[TurnManager] Game is over — no more turns.");
            else Debug.LogWarning("[TurnManager] EndCurrentUnitTurn called before Initialize.");
            return;
        }
        if (activeUnit != null && activeUnit.IsTraveling)
        { Debug.Log("[TurnManager] Unit still travelling."); return; }

        AdvanceUnitSlot();
    }

    // ======================================================================
    // Combat internals
    // ======================================================================

    void HandleUnitDeath(HexUnit unit)
    {
        Debug.Log($"[TurnManager] {unit.name} (Team {unit.Team?.TeamIndex}) was killed.");
        TurnManagerEvents.OnUnitDied?.Invoke(unit);
    }

    void DealHomeDamage(Team target, HexUnit attacker, int damage)
    {
        target.HomeHP -= damage;
        TurnManagerEvents.OnHomeDamageDealt?.Invoke(attacker, target, damage);
        Debug.Log($"[TurnManager] Team {target.TeamIndex} home hit for {damage} — " +
                  $"HP: {target.HomeHP}/{target.HomeLevel * hpPerHomeLevel}");

        while (target.HomeHP <= (target.HomeLevel - 1) * hpPerHomeLevel)
        {
            if (target.HomeLevel <= 1)
            {
                EliminateTeam(target, attacker);
                return;
            }

            int tier = target.HomeLevel - 2;
            GiveLoot(attacker, tier);

            target.HomeLevel--;
            ReplaceHomePrefab(target);
            Debug.Log($"[TurnManager] Team {target.TeamIndex} home reduced " +
                      $"to Lv{target.HomeLevel}.");
            TurnManagerEvents.OnHomeLevelReduced?.Invoke(attacker, target);
        }
    }

    void GiveLoot(HexUnit attacker, int tier)
    {
        if (attacker == null) return;

        // Loot uses the inspector base costs regardless of scenario overrides
        // so that combat rewards are consistent across scenarios.
        int woodLoot = tier < upgradeWoodCost.Length
            ? Mathf.RoundToInt(upgradeWoodCost[tier] * lootFraction) : 0;
        int stoneLoot = tier < upgradeStoneCost.Length
            ? Mathf.RoundToInt(upgradeStoneCost[tier] * lootFraction) : 0;

        int space = maxCarryCapacity - attacker.TotalCarried;
        woodLoot = Mathf.Min(woodLoot, space);
        stoneLoot = Mathf.Min(stoneLoot, Mathf.Max(0, space - woodLoot));

        attacker.GainWood(woodLoot);
        attacker.GainStone(stoneLoot);
        Debug.Log($"[TurnManager] Attacker looted {woodLoot}W / {stoneLoot}S.");
    }

    void EliminateTeam(Team team, HexUnit killer)
    {
        Debug.Log($"[TurnManager] Team {team.TeamIndex} eliminated!");
        TurnManagerEvents.OnTeamEliminated?.Invoke(killer, team);

        if (team.HomeInstance != null)
        {
            Object.Destroy(team.HomeInstance.gameObject);
            team.HomeInstance = null;
        }

        var unitsCopy = new List<HexUnit>(team.Units);
        foreach (HexUnit unit in unitsCopy)
        {
            if (unit == null) continue;
            if (unit.LocationCellIndex >= 0 && !unit.IsDead)
            {
                HexCell cell = grid.GetCell(unit.LocationCellIndex);
                if (unit.TeamVisibilityActive) grid.DecreaseVisibility(cell, unit.VisionRange);
                cell.RemoveUnit(unit);
                grid.RefreshCellUnitPositions(unit.LocationCellIndex);
            }
            // Deregister from the grid's tracking list BEFORE we destroy
            // the GameObject — otherwise the reference lingers in
            // HexGrid.units and the next episode's ClearUnits() trips
            // over it (MissingReferenceException when accessing
            // .gameObject on a destroyed Unity object).
            grid.UntrackUnit(unit);
            Object.Destroy(unit.gameObject);
        }
        team.Units.Clear();

        int teamIdx = teams.IndexOf(team);
        teams.Remove(team);

        // Rebuild teamOrder to reflect surviving teams only.
        var survivors = new List<int>();
        for (int i = 0; i < teams.Count; i++) survivors.Add(i);
        teamOrder = survivors.ToArray();

        if (currentTeamOrderIndex >= teams.Count)
            currentTeamOrderIndex = 0;

        if (teams.Count == 1)
        {
            Debug.Log($"[TurnManager] *** Team {teams[0].TeamIndex} WINS! ***");
            TurnManagerEvents.OnGameWon?.Invoke(teams[0]);
            GameOver = true;
            return;
        }
        if (teams.Count == 0)
        {
            Debug.Log("[TurnManager] All teams eliminated — draw.");
            GameOver = true;
            return;
        }

        if (team == activeTeam)
        {
            activeTeam = null;
            activeUnit = null;
            StartTeamTurn(teams[currentTeamOrderIndex % teams.Count]);
        }
    }

    // ======================================================================
    // Spawning
    // ======================================================================

    void SpawnAllUnits()
    {
        List<int> allLand = new();
        for (int i = 0; i < grid.CellData.Length; i++)
            if (!grid.CellData[i].IsUnderwater) allLand.Add(i);

        if (allLand.Count == 0) { Debug.LogError("[TurnManager] No land cells."); return; }

        List<int> homeCells = PickHomeCells(allLand, activeTeamCount);

        for (int t = 0; t < activeTeamCount; t++)
        {
            Team team = teams[t];
            int count = Mathf.Clamp(
                t < activeUnitsPerTeam.Length
                    ? activeUnitsPerTeam[t]
                    : activeUnitsPerTeam[^1],
                1, 4);
            Material mat = teamMaterials != null && teamMaterials.Length > 0
                ? teamMaterials[Mathf.Min(t, teamMaterials.Length - 1)] : null;

            HexCell homeCell = grid.GetCell(homeCells[t]);
            team.HomeCellIndex = homeCells[t];
            team.HomeLevel = 1;
            team.HomeHP = 1 * hpPerHomeLevel;
            team.HomeInstance = SpawnHomePrefab(homeCell, 0, mat);
            // Home is always known to its team — guarantees it shows up in
            // the explored set regardless of whether unit vision can reach
            // it (e.g. when surrounded by cliffs).
            team.MarkExplored(team.HomeCellIndex);
            // Force the home cell explorable too. Borders are normally not
            // explorable, and on rare maps the maximin placement can pick a
            // non-underwater border cell — without this, IsValidDestination
            // would refuse to route the unit onto its own home.
            homeCell.Flags = homeCell.Flags.With(HexFlags.Explorable);

            int spawned = SpawnUnitsAroundHome(homeCell, count, team, mat);
            if (spawned < count)
                Debug.LogWarning(
                    $"[TurnManager] Team {t} only spawned {spawned}/{count} units.");

            // Fixed unit order — set once per team, never reshuffled.
            team.FixedUnitOrder = RandomOrder(team.Units.Count);

            team.ApplyExploredToGrid(grid.CellData);
            foreach (HexUnit u in team.Units) u.ActivateTeamVisibility();
            team.SaveExploredFromGrid(grid.CellData);
            foreach (HexUnit u in team.Units) u.DeactivateTeamVisibility();
            Team.ClearExploredFromGrid(grid.CellData);
        }
    }

    void ReplaceHomePrefab(Team team)
    {
        if (team.HomeInstance != null) Object.Destroy(team.HomeInstance.gameObject);
        Material mat = teamMaterials != null && teamMaterials.Length > 0
            ? teamMaterials[Mathf.Min(team.TeamIndex, teamMaterials.Length - 1)] : null;
        int prefabIdx = Mathf.Clamp(team.HomeLevel - 1, 0, homePrefabs.Length - 1);
        team.HomeInstance = SpawnHomePrefab(grid.GetCell(team.HomeCellIndex), prefabIdx, mat);
    }

    Transform SpawnHomePrefab(HexCell cell, int prefabIndex, Material mat)
    {
        if (homePrefabs == null || prefabIndex >= homePrefabs.Length ||
            homePrefabs[prefabIndex] == null)
        {
            Debug.LogWarning($"[TurnManager] homePrefabs[{prefabIndex}] not assigned.");
            return null;
        }

        Transform inst = Object.Instantiate(homePrefabs[prefabIndex]);
        Vector3 pos = cell.Position;
        pos.y += inst.localScale.y * 0.5f;
        inst.position = HexMetrics.Perturb(pos);
        if (mat != null)
            foreach (Renderer r in inst.GetComponentsInChildren<Renderer>())
                r.sharedMaterial = mat;
        grid.MakeChildOfColumn(inst, cell.Coordinates.ColumnIndex);
        return inst;
    }

    int SpawnUnitsAroundHome(HexCell homeCell, int count, Team team, Material mat)
    {
        int spawned = 0;
        var visited = new HashSet<int> { homeCell.Index };
        var queue = new Queue<int>();

        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
            if (homeCell.TryGetNeighbor(d, out HexCell nb) && !visited.Contains(nb.Index))
            { visited.Add(nb.Index); queue.Enqueue(nb.Index); }

        while (queue.Count > 0 && spawned < count)
        {
            int ci = queue.Dequeue();
            HexCell cell = grid.GetCell(ci);
            if (!grid.CellData[ci].IsUnderwater)
            {
                HexUnit unit = Object.Instantiate(unitPrefab);
                grid.AddUnit(unit, cell, Random.Range(0f, 360f));
                unit.Team = team;
                team.Units.Add(unit);
                if (mat != null) unit.ApplyTeamMaterial(mat);
                grid.RefreshCellUnitPositions(ci);

                // Decision pump is handled centrally in HexMLEnvironment
                // via Academy.AgentPreStep, gated on TurnManager.ActiveUnit.
                // Spawned units carry no DecisionRequester component, so
                // there is nothing to disable here.

                // NOTE: BehaviorParameters.TeamId is intentionally left at
                // its prefab default (0) for both teams. Setting different
                // team_ids per side would split FullyQualifiedBehaviorName
                // ("HexUnit?team=0" vs "HexUnit?team=1"), and since
                // config.yaml only declares a trainer for "HexUnit", agents
                // with team_id != 0 would receive no policy and silently
                // fall back to Heuristic() — which here always issues
                // EndTurn, freezing one side. To enable proper self-play
                // later, re-stamp the TeamId here AND add a `self_play:`
                // block in config.yaml that links the two FQBNs.

                spawned++;
            }
            for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
                if (cell.TryGetNeighbor(d, out HexCell nb) && !visited.Contains(nb.Index))
                { visited.Add(nb.Index); queue.Enqueue(nb.Index); }
        }
        return spawned;
    }

    // ======================================================================
    // Home placement
    //
    // For 2 teams we use SYMMETRIC placement: both bases sit at similar hex
    // distance from the map centre, on opposite sides. This avoids the
    // maximin bias diagnosed in 1v1 training, where team 0 (random pick)
    // ended up statistically central and team 1 (max-distance from team 0)
    // ended up in a corner. Equal centredness → equal number of valid move
    // neighbours per cell → both shared-policy agents weight Move equally
    // in their action distribution, so neither side appears to "run around
    // constantly" while the other gathers.
    //
    // For 1 team (solo) we just pick at random. For 3+ teams we fall back
    // to maximin since symmetric placement around centre is not well-defined
    // for odd counts and is awkward for 4+.
    // ======================================================================

    List<int> PickHomeCells(List<int> landCells, int count)
    {
        if (count == 1)
        {
            return new List<int>
            {
                landCells[Random.Range(0, landCells.Count)]
            };
        }

        if (count == 2)
        {
            // Target separation: far enough that combat takes deliberate
            // travel (≥ 8 hexes), close enough that combat is a real
            // strategic option vs pure economy (≤ 14 hexes — about three
            // turns of pure travel one way at 5 AP per move).
            //
            // We try the tight range first. If the procedural map happens
            // to have water or holes that prevent finding a valid mirror
            // pair in that range, we widen the band before falling back
            // to maximin (which would produce the old corner-to-corner
            // problem).
            var tight  = TryPickSymmetricPair(landCells,  8, 14);
            if (tight  != null) return tight;

            var medium = TryPickSymmetricPair(landCells,  7, 18);
            if (medium != null) return medium;

            var loose  = TryPickSymmetricPair(landCells,  6, 24);
            if (loose  != null) return loose;
        }

        return PickMaximinSequence(landCells, count);
    }

    /// <summary>
    /// Pick two land cells that are mirror-images of each other through the
    /// map centre, with hex distance in the closed range
    /// [<paramref name="minSeparation"/>, <paramref name="maxSeparation"/>].
    /// Returns null when no such pair can be found within the attempt
    /// budget (e.g. on very irregular maps); caller decides whether to
    /// loosen the constraint or fall back to maximin.
    ///
    /// The previous version had only a minimum and an unbounded maximum,
    /// which combined with the reflection-through-centre algorithm produced
    /// bases at maximum possible separation (opposite corners on most maps)
    /// — fine for marathon economic games but it makes combat impractical
    /// because every aggression decision pays an enormous travel cost.
    /// Capping the separation keeps both bases reachable in a handful of
    /// turns so combat is a real alternative to gathering.
    /// </summary>
    List<int> TryPickSymmetricPair(List<int> landCells,
                                   int minSeparation, int maxSeparation)
    {
        var landSet = new HashSet<int>(landCells);
        HexCoordinates centre = HexCoordinates.FromOffsetCoordinates(
            grid.CellCountX / 2, grid.CellCountZ / 2);

        // Distance(a, mirror) = 2 · distance(a, centre), so to satisfy the
        // separation cap the first pick must sit within maxSeparation/2 of
        // the centre. We use this as an early filter on otherwise-valid
        // first cells so we don't burn attempts on far-from-centre picks
        // that can never produce an in-range pair.
        int maxDistFromCentre = maxSeparation / 2;

        for (int attempt = 0; attempt < 300; attempt++)
        {
            int firstIdx = landCells[Random.Range(0, landCells.Count)];
            HexCoordinates a = grid.CellData[firstIdx].coordinates;

            if (a.DistanceTo(centre) > maxDistFromCentre) continue;

            // Reflection through the centre in axial coords: m = 2c − a
            HexCoordinates mirror = new HexCoordinates(
                2 * centre.X - a.X, 2 * centre.Z - a.Z);

            if (!grid.TryGetCellIndex(mirror, out int mirrorIdx)) continue;
            if (mirrorIdx == firstIdx) continue;
            if (!landSet.Contains(mirrorIdx)) continue;

            int separation = a.DistanceTo(mirror);
            if (separation < minSeparation) continue;
            if (separation > maxSeparation) continue;

            // Randomise which team gets which end so that even within a
            // single episode the first-spawned team is not consistently
            // on one side of the map.
            var pair = new List<int> { firstIdx, mirrorIdx };
            if (Random.value < 0.5f) (pair[0], pair[1]) = (pair[1], pair[0]);
            return pair;
        }
        return null;
    }

    /// <summary>
    /// Original maximin placement, used for solo and 3+-team scenarios
    /// where symmetric reflection through the centre is not a good fit.
    /// </summary>
    List<int> PickMaximinSequence(List<int> landCells, int count)
    {
        var chosen = new List<int>(count);
        chosen.Add(landCells[Random.Range(0, landCells.Count)]);

        int[] minDist = new int[landCells.Count];
        for (int i = 0; i < minDist.Length; i++) minDist[i] = int.MaxValue;
        UpdateMinDist(landCells, minDist, chosen[0]);

        for (int h = 1; h < count; h++)
        {
            int best = -1, bestDist = -1;
            for (int i = 0; i < landCells.Count; i++)
            {
                if (IsChosen(chosen, landCells[i])) continue;
                if (minDist[i] > bestDist) { bestDist = minDist[i]; best = i; }
            }
            if (best < 0)
                for (int i = 0; i < landCells.Count; i++)
                    if (!IsChosen(chosen, landCells[i])) { best = i; break; }

            chosen.Add(landCells[best]);
            UpdateMinDist(landCells, minDist, landCells[best]);
        }
        return chosen;
    }

    void UpdateMinDist(List<int> cells, int[] minDist, int homeCellIdx)
    {
        HexCoordinates hc = grid.CellData[homeCellIdx].coordinates;
        for (int i = 0; i < cells.Count; i++)
        {
            int d = hc.DistanceTo(grid.CellData[cells[i]].coordinates);
            if (d < minDist[i]) minDist[i] = d;
        }
    }

    static bool IsChosen(List<int> chosen, int ci)
    { foreach (int c in chosen) if (c == ci) return true; return false; }

    // ======================================================================
    // Turn flow
    // ======================================================================

    void StartRound()
    {
        roundNumber++;
        currentTeamOrderIndex = 0;
        Debug.Log($"[TurnManager] === Round {roundNumber} ===");
        StartTeamTurn(teams[teamOrder[0]]);
    }

    void StartTeamTurn(Team team)
    {
        activeTeam = team;
        team.ApplyExploredToGrid(grid.CellData);
        foreach (HexUnit u in team.Units) u.ActivateTeamVisibility();

        currentUnitSlot = 0;
        Debug.Log($"[TurnManager] Team {team.TeamIndex} turn started.");
        AdvanceToNextLivingUnit();
    }

    void AdvanceToNextLivingUnit()
    {
        int[] order = activeTeam.FixedUnitOrder;
        int totalSlots = order.Length;

        while (currentUnitSlot < totalSlots)
        {
            int idx = order[currentUnitSlot];
            if (idx < activeTeam.Units.Count)
            {
                StartUnitTurn(activeTeam.Units[idx]);
                return;
            }
            currentUnitSlot++;
        }
        EndTeamTurn();
    }

    void StartUnitTurn(HexUnit unit)
    {
        activeUnit = unit;

        if (unit.IsDead)
        {
            // Dead unit's slot is skipped — make sure its DecisionRequester
            // stays off so it does not pick up a stray callback during
            // respawn frames.

            if (!unit.ReadyToRespawn)
            {
                unit.SetReadyToRespawn(true);
                Debug.Log($"[TurnManager] {unit.name} is dead — eligible to respawn next turn.");
                AdvanceUnitSlot();
                return;
            }

            HexCell homeCell = grid.GetCell(activeTeam.HomeCellIndex);
            if (homeCell.HasEnemyUnits(activeTeam))
            {
                Debug.Log($"[TurnManager] {unit.name} cannot respawn — enemy on home cell.");
                AdvanceUnitSlot();
                return;
            }

            unit.Respawn(homeCell);
            grid.RefreshCellUnitPositions(homeCell.Index);
            Debug.Log($"[TurnManager] {unit.name} respawned at home.");
            AdvanceUnitSlot();
            return;
        }

        unit.StartTurn();
        Debug.Log($"[TurnManager] Unit turn — Team {activeTeam.TeamIndex}, " +
                  $"HP {unit.CurrentHP}/{unit.MaxHP}, AP {unit.ActionPointsRemaining}");
        TurnManagerEvents.OnUnitTurnStarted?.Invoke(unit);
    }

    void AdvanceUnitSlot()
    {
        currentUnitSlot++;
        if (currentUnitSlot < activeTeam.FixedUnitOrder.Length)
            AdvanceToNextLivingUnit();
        else
            EndTeamTurn();
    }

    void EndTeamTurn()
    {
        activeTeam.SaveExploredFromGrid(grid.CellData);
        foreach (HexUnit u in activeTeam.Units) u.DeactivateTeamVisibility();
        Team.ClearExploredFromGrid(grid.CellData);

        currentTeamOrderIndex++;
        if (currentTeamOrderIndex < teams.Count)
            StartTeamTurn(teams[teamOrder[currentTeamOrderIndex % teams.Count]]);
        else
            StartRound();
    }

    // ======================================================================
    // Helpers
    // ======================================================================

    static int[] RandomOrder(int count)
    {
        int[] order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        for (int i = count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        return order;
    }

    static void Shuffle<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}