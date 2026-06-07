using System.Collections.Generic;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Actuators;
using Unity.MLAgents.Sensors;

/// <summary>
/// ML-Agents Agent component attached to each HexUnit.
///
/// ── Observation space (100 floats) ────────────────────────────
/// Self (5):                hp_norm, ap_norm, wood_carry_norm, stone_carry_norm, is_dead
/// Team (5):                wood_stored_norm, stone_stored_norm, home_level_norm,
///                          home_hp_norm, home_max_level_reached
/// Own home (4):            delta_x_norm, delta_z_norm,
///                          distance_norm, inventory_fullness_norm
/// 6 adjacent cells (48):   terrain/4, elevation/6, wood_res/max, stone_res/max,
///                          has_enemy, has_friendly, is_enemy_home, is_own_home
/// Home compass (6):        for each of 6 directions, signed delta of how moving
///                          there would change distance to home (-1, 0, or +1).
/// Resource compass (6):    same idea for the nearest harvestable cell. Zeroed
///                          when inventory is full.
/// Nearest ally unit (5):   delta_x, delta_z, distance_norm, hp_norm, carry_norm.
///                          Required for 2v2 cooperation. Zeroed in solo / 1v1
///                          where there is no living teammate.
/// Nearest wood cell (4):   delta_x, delta_z, amount_norm, distance_norm
/// Nearest stone cell (4):  delta_x, delta_z, amount_norm, distance_norm
/// Nearest enemy unit (4):  delta_x, delta_z, hp_norm, reserved
/// 2nd-nearest enemy (4):   delta_x, delta_z, hp_norm, reserved. Required for
///                          2v2 so the agent can model both opponents at once
///                          instead of only the closest. Zeroed in 1v0 / 1v1.
/// Nearest enemy home (4):  delta_x, delta_z, level_norm, hp_norm
/// Round (1):               round_norm
/// Total = 5+5+4+48+6+6+5+4+4+4+4+4+1 = 100
///
/// NOTE: obs size changed from 91 → 100 (added nearest-ally and 2nd-enemy).
/// Update Behavior Parameters > Vector Observation > Space Size to 100.
/// Start a new run-id (obs shape mismatch means --initialize-from won't work):
///   mlagents-learn Assets/Scripts/ML/config.yaml --run-id=combat2v2_I
/// </summary>
[RequireComponent(typeof(HexUnit))]
public class HexUnitAgent : Agent
{
    // ── Rewards — Combat ──────────────────────────────────────────────────
    // THIRD escalation. After 1M steps in 2v2 the agent still hasn't tried
    // combat — the value function has zero samples from any successful
    // combat trajectory, so the policy can't see that it's worth doing.
    // The only way out of that trap is to make combat so heavily rewarded
    // that even *partial* attempts (a few approach steps, one Raise Home,
    // one half-killed enemy) generate visibly higher local return than
    // pure-economy turns. We're past the point of subtle balancing — the
    // policy has to discover combat first, balance can come later.
    //
    // Reference per-trajectory totals at the new values:
    //
    //   Economic win              ≈ 30–45  (2v2 costs: 700 resources / team)
    //   Per Raise Home attack     ≈ +5.50  (0.50 flat + 10 × 0.50 from damage event)
    //   Military win (lv-1 home)  ≈ ~85    (10 attacks × 5.50 + level 10 + elim 15 + win 6)
    //   Military win (lv-3 home)  ≈ ~185   (30 attacks × 5.50 + 3 × 10 + 15 + 6)
    //
    // Combat now pays 2–4× economic across all variants. Per-turn shaping
    // along the combat path also exceeds the per-turn shaping of gathering
    // (0.48 vs 0.40 — see rewardApproachingEnemyHome below).
    [Header("Rewards — Combat")]
    [SerializeField] float rewardDealDamage = 0.10f;
    [SerializeField] float rewardKillUnit = 5.00f;
    [SerializeField] float rewardDamageEnemyHome = 0.50f;
    [SerializeField] float rewardReduceHomeLevel = 10.00f;
    [SerializeField] float rewardEliminateTeam = 15.00f;
    [SerializeField] float rewardWin = 6.00f;
    [SerializeField] float penaltyDie = -0.40f;
    [SerializeField] float penaltyLose = -5.00f;

    // ── Rewards — Economy ─────────────────────────────────────────────────
    [Header("Rewards — Economy")]
    [Tooltip("Base reward for a full gather (gatherAmount=10). Scaled by how " +
             "much was actually gathered, so partial gathers near depleted " +
             "cells give proportionally less. Encourages going to richer cells.")]
    [SerializeField] float rewardGatherResource = 0.10f;

    [Tooltip("Flat reward for any deposit action — pays for the act of returning.")]
    [SerializeField] float rewardDepositFlat = 0.20f;

    [Tooltip("Reward per resource unit deposited. With capacity 200 a full " +
             "deposit pays 200×0.005=1.0 on top of the flat reward, strongly " +
             "incentivising filling inventory before returning home.")]
    [SerializeField] float rewardDepositPerUnit = 0.005f;

    [SerializeField] float rewardUpgradeHome = 2.00f;

    // ── Rewards — Progress shaping (potential-based, no exploitation) ─────
    [Header("Rewards — Progress shaping")]
    [SerializeField] float rewardResourceProgress = 0.001f;

    [Tooltip("Per hex step CLOSER to home while carrying anything. Applied " +
             "SYMMETRICALLY — moving farther costs the same amount.")]
    [SerializeField] float rewardApproachingHome = 0.020f;

    [Tooltip("Same as rewardApproachingHome but applied when inventory is full. " +
             "Strong enough that a 15-hex return trip pays ~0.60 from approach " +
             "shaping alone — beating any plausible 'just gather more nearby' " +
             "alternative once the load is full.")]
    [SerializeField] float rewardApproachingHomeWhenFull = 0.040f;

    [Tooltip("Per hex step CLOSER to the nearest resource cell while the unit " +
             "is NOT carrying a full load. Symmetric — moving away costs the " +
             "same. Replaces the old per-step 'adjacent-to-resource' bonus " +
             "(which the agent farmed by camping next to a resource cell).")]
    [SerializeField] float rewardApproachingResource = 0.008f;

    [Tooltip("Extra one-off reward on a step that moves onto the home cell " +
             "while carrying resources. Big enough to be a clear 'mission " +
             "complete' signal that closes the round-trip loop, regardless " +
             "of how far away home was.")]
    [SerializeField] float rewardHomeArrivalWithCarry = 0.30f;

    [Tooltip("Extra one-off reward on a step that moves onto a cell with " +
             "harvestable resources while the unit is NOT full. Reinforces " +
             "the 'find resources' loop alongside the smooth approach gradient.")]
    [SerializeField] float rewardResourceArrival = 0.05f;

    [Tooltip("One-off reward on a step that moves onto an enemy home cell. " +
             "Bumped to 5.00 (≈ a full kill reward) — landing on the enemy " +
             "base is now a major event, not just a milestone. Designed to " +
             "make even one-off exploration trips toward enemy territory " +
             "produce a memorable spike in return so the value function " +
             "records it.")]
    [SerializeField] float rewardEnemyHomeArrival = 5.00f;

    [Tooltip("Per-hex shaping toward the nearest enemy home. Bumped to " +
             "0.12 — *larger* than the gather reward per turn (4 × 0.12 = " +
             "0.48 vs 4 × 0.10 = 0.40 from gathering). Every turn spent " +
             "approaching the enemy is now MORE rewarding than every turn " +
             "spent gathering, so the policy can't just locally-greedily " +
             "default to economy. Zero in solo.")]
    [SerializeField] float rewardApproachingEnemyHome = 0.12f;

    [Tooltip("Per-hex shaping toward the nearest enemy unit. Bumped to " +
             "0.06 — pulls hard enough that intercepting an enemy unit on " +
             "its way to our base is competitive with returning to deposit.")]
    [SerializeField] float rewardApproachingEnemyUnit = 0.06f;

    // ── Rewards — Team coordination (2v2 only) ────────────────────────────
    //
    // These rewards only meaningfully fire when there is a living teammate
    // AND a living enemy on the map. In solo / 1v1 they evaluate to zero
    // by construction, so leaving them at non-zero defaults is harmless
    // for the earlier scenarios.
    [Header("Rewards — Team coordination (2v2)")]

    [Tooltip("Per-step reward when a teammate is within coordinationRange " +
             "AND an enemy is within engagementRange. Bumped to 0.10 so " +
             "ganging up on an enemy is a major per-step incentive — pays " +
             "more per turn than gathering does (0.10/step ≈ 0.4 per turn).")]
    [SerializeField] float rewardCoordinatedAssault = 0.10f;

    [Tooltip("Per-step penalty when teammates are clustered AND no enemy " +
             "is anywhere near. Softened to -0.005 (half the previous) " +
             "so it nudges spread-out coverage in peacetime without " +
             "actively fighting against legitimate convergence for combat.")]
    [SerializeField] float penaltyAllyCluster = -0.005f;

    [Tooltip("Per-step reward when on/adjacent to own home AND enemy is " +
             "within enemyAtHomeRange of that home. Bumped to 0.10 so " +
             "garrisoning a threatened base pays similarly to attacking — " +
             "the policy needs strong defensive shaping to learn that " +
             "running back is the right call when the enemy is coming.")]
    [SerializeField] float rewardDefendHome = 0.10f;

    [Tooltip("One-off penalty for landing on a cell the teammate visited " +
             "in the last TeamMoveWindowSize moves. Kept at -0.02 but now " +
             "skipped when an enemy is within engagementRange of either " +
             "unit — mirror paths are bad in peacetime (duplicated work) " +
             "but fine during a converging assault.")]
    [SerializeField] float penaltyMirrorPath = -0.020f;

    [Tooltip("Distance (hexes) within which the teammate is considered " +
             "'cooperating' for both the coordinated-assault reward and " +
             "the ally-cluster penalty.")]
    [SerializeField] int coordinationRange = 2;

    [Tooltip("Distance (hexes) within which an enemy is considered " +
             "'engaged'. Used as the threshold that flips coordination " +
             "rewards on and ally-cluster penalty off.")]
    [SerializeField] int engagementRange = 4;

    [Tooltip("Distance (hexes) from own home within which an enemy is " +
             "considered 'threatening the base' — triggers rewardDefendHome.")]
    [SerializeField] int enemyAtHomeRange = 5;

    [Tooltip("Extra bonus paid on top of the per-unit deposit reward when " +
             "the deposit is near-full (≥80% of capacity). Discourages " +
             "wasteful micro-trips by making 'fill up first, then return' " +
             "strictly better than 'return half-empty'.")]
    [SerializeField] float rewardDepositFullBonus = 0.50f;
    [SerializeField, Range(0f, 1f)] float depositFullBonusThreshold = 0.8f;

    // ── Rewards — Penalties ───────────────────────────────────────────────
    [Header("Rewards — Penalties")]
    [SerializeField] float penaltyInvalidAction = -0.05f;
    [Tooltip("Per-decision penalty. Bumped up so doing-nothing-but-end-turn " +
             "is clearly net-negative, even without proximity-camping rewards.")]
    [SerializeField] float penaltyTimestep = -0.002f;
    [SerializeField] float penaltyRoamingWithLoad = -0.008f;
    [Tooltip("Penalty when revisiting a cell visited within the last " +
             "oscillationWindowSize moves. Strengthened to kill the " +
             "'orbiting a few cells' pattern more decisively.")]
    [SerializeField] float penaltyOscillation = -0.03f;
    [SerializeField] int oscillationWindowSize = 6;
    [Tooltip("Penalty per unused AP above the free threshold when ending turn. " +
             "Bumped 5× so giving up a turn early is meaningfully costly.")]
    [SerializeField] float penaltyEarlyEndTurn = -0.005f;
    [SerializeField] int endTurnFreeApThreshold = 5;

    // ── References ────────────────────────────────────────────────────────
    HexUnit unit;
    TurnManager tm;

    // ── Position history (anti-oscillation) ──────────────────────────────
    readonly Queue<int> recentCells = new();

    // ── Progress tracking ─────────────────────────────────────────────────
    float prevUpgradeProgress;

    // ── Per-episode counters ──────────────────────────────────────────────
    int episodeMoves, episodeAttacks, episodeGathers;
    int episodeDeposits, episodeDeaths, episodeKills, episodeUpgrades;
    float episodeTotalReward;
    int episodeSteps;

    // ── Last-action info (for CSV logging) ───────────────────────────────
    public int LastActionIndex { get; private set; } = -1;
    public string LastActionName { get; private set; } = "none";
    public float LastStepReward { get; private set; }

    static readonly string[] ActionNames =
    {
        "EndTurn",
        "MoveNE","MoveE","MoveSE","MoveSW","MoveW","MoveNW",
        "GatherWood","GatherStone","Deposit",
        "AttackNE","AttackE","AttackSE","AttackSW","AttackW","AttackNW",
        "UpgradeOrClaim","RaiseHome"
    };

    // ── Per-team turn-flow diagnostics (static, shared across all agents) ──
    // Counts everything we care about for spotting 1v1 turn asymmetry, and
    // dumps a summary to the console every ~5 seconds of game time. If the
    // two teams' active-action counts diverge by more than ~20%, the turn
    // flow is broken; if they stay roughly equal, any per-side visual
    // difference is just policy choice (one moving while the other gathers).
    const int kMaxDiagTeams = 4;
    static readonly int[] s_diagActiveActions   = new int[kMaxDiagTeams];
    static readonly int[] s_diagInactiveSkips   = new int[kMaxDiagTeams];
    static readonly int[] s_diagMoves           = new int[kMaxDiagTeams];
    static readonly int[] s_diagEndTurns        = new int[kMaxDiagTeams];
    static readonly int[] s_diagDeaths          = new int[kMaxDiagTeams];
    static readonly int[] s_diagTravelingSkips  = new int[kMaxDiagTeams];
    static float s_diagLastDumpTime;

    static void DiagBumpAndMaybeLog()
    {
        if (Time.realtimeSinceStartup - s_diagLastDumpTime < 5f) return;
        s_diagLastDumpTime = Time.realtimeSinceStartup;

        var sb = new System.Text.StringBuilder("[1v1 diag] ");
        for (int t = 0; t < kMaxDiagTeams; t++)
        {
            if (s_diagActiveActions[t] == 0 && s_diagInactiveSkips[t] == 0 &&
                s_diagMoves[t] == 0) continue;
            sb.Append($"T{t}: active={s_diagActiveActions[t]} ");
            sb.Append($"moves={s_diagMoves[t]} ");
            sb.Append($"end={s_diagEndTurns[t]} ");
            sb.Append($"deaths={s_diagDeaths[t]} ");
            sb.Append($"idle={s_diagInactiveSkips[t]} ");
            sb.Append($"travel={s_diagTravelingSkips[t]}  ");
        }
        Debug.Log(sb.ToString());

        for (int t = 0; t < kMaxDiagTeams; t++)
        {
            s_diagActiveActions[t] = 0;
            s_diagInactiveSkips[t] = 0;
            s_diagMoves[t] = 0;
            s_diagEndTurns[t] = 0;
            s_diagDeaths[t] = 0;
            s_diagTravelingSkips[t] = 0;
        }
    }

    // ======================================================================
    // Initialise / Episode
    // ======================================================================

    public override void Initialize()
    {
        unit = GetComponent<HexUnit>();
        tm = TurnManager.Instance;
    }

    public override void OnEpisodeBegin()
    {
        episodeMoves = episodeAttacks = episodeGathers = 0;
        episodeDeposits = episodeDeaths = episodeKills = episodeUpgrades = 0;
        episodeTotalReward = 0f;
        episodeSteps = 0;
        prevUpgradeProgress = 0f;
        recentCells.Clear();
        LastActionIndex = -1;
        LastActionName = "none";
        LastStepReward = 0f;
    }

    // ======================================================================
    // Observations (100 floats — see class summary for breakdown)
    // ======================================================================

    public override void CollectObservations(VectorSensor sensor)
    {
        if (unit == null || tm == null || !tm.Initialized)
        {
            for (int i = 0; i < 100; i++) sensor.AddObservation(0f);
            return;
        }

        Team team = unit.Team;

        // ── Self (5) ──────────────────────────────────────────────────────
        sensor.AddObservation(Norm(unit.CurrentHP, unit.MaxHP));
        sensor.AddObservation(Norm(unit.ActionPointsRemaining, 24f));
        sensor.AddObservation(Norm(unit.WoodCarried, tm.MaxCarryCapacity));
        sensor.AddObservation(Norm(unit.StoneCarried, tm.MaxCarryCapacity));
        sensor.AddObservation(unit.IsDead ? 1f : 0f);

        // ── Team / home (5) ───────────────────────────────────────────────
        sensor.AddObservation(Norm(team.WoodStored, 2000f));
        sensor.AddObservation(Norm(team.StoneStored, 2000f));
        sensor.AddObservation(Norm(team.HomeLevel, 3f));
        sensor.AddObservation(Norm(team.HomeHP, team.HomeLevel * 100f));
        sensor.AddObservation(team.HomeLevel >= 3 ? 1f : 0f);

        // ── Own home — expanded to 4 obs ─────────────────────────────────
        // Added: distance_norm and inventory_fullness_norm alongside the
        // bearing. This makes "how far is home" and "how full am I" both
        // explicitly present so the policy can directly learn the
        // carry→return correlation without inferring it from two separate obs.
        HexCoordinates homeCo = unit.Grid.CellData[team.HomeCellIndex].coordinates;
        HexCoordinates selfCo = unit.Location.Coordinates;
        int distToHome = selfCo.DistanceTo(homeCo);

        AddRelativePosition(sensor, homeCo);                          // dx, dz
        sensor.AddObservation(Norm(distToHome, 20f));         // distance
        sensor.AddObservation(Norm(unit.TotalCarried, tm.MaxCarryCapacity)); // fullness

        // ── 6 adjacent cells (8 each = 48) ───────────────────────────────
        HexCell loc = unit.Location;
        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
        {
            if (loc.TryGetNeighbor(d, out HexCell nb))
            {
                var v = nb.Values;
                sensor.AddObservation(Norm(v.TerrainTypeIndex, 4f));
                sensor.AddObservation(Norm(v.Elevation, 6f));
                sensor.AddObservation(Norm(unit.Grid.CellWoodResources[nb.Index], 300f));
                sensor.AddObservation(Norm(unit.Grid.CellStoneResources[nb.Index], 300f));
                sensor.AddObservation(nb.HasEnemyUnits(team) ? 1f : 0f);
                bool hasFriendly = false;
                foreach (HexUnit u in nb.Units)
                    if (u.Team == team) { hasFriendly = true; break; }
                sensor.AddObservation(hasFriendly ? 1f : 0f);
                bool isEnemyHome = false, isOwnHome = false;
                foreach (Team t in tm.Teams)
                    if (t.HomeCellIndex == nb.Index)
                    { if (t == team) isOwnHome = true; else isEnemyHome = true; }
                sensor.AddObservation(isEnemyHome ? 1f : 0f);
                sensor.AddObservation(isOwnHome ? 1f : 0f);
            }
            else
            {
                for (int i = 0; i < 8; i++) sensor.AddObservation(0f);
            }
        }

        // ── Home compass (6) ──────────────────────────────────────────────
        // For each of the 6 hex directions, a signed value indicating how
        // moving that way would change distance to home:
        //   −1.0 = that step gets us CLOSER  (the "go home" arrow)
        //    0.0 = same distance / no neighbor
        //   +1.0 = farther away
        //
        // This is the single most important obs for fixing the "lost when far
        // from base" failure. The existing delta_x/delta_z values saturate at
        // ±1 once the unit is 20+ cells from home and stop carrying gradient,
        // so on a 20×15 map the agent literally cannot read which direction
        // home is in. This compass works at any distance.
        AddDirectionalCompass(sensor, loc, homeCo, distToHome);

        // ── Resource compass (6) ──────────────────────────────────────────
        // Same idea, but pointing at the nearest harvestable cell. Zeroed
        // when inventory is full so the agent isn't pulled toward more
        // resources when it should be heading home.
        if (unit.TotalCarried >= tm.MaxCarryCapacity)
        {
            for (int i = 0; i < 6; i++) sensor.AddObservation(0f);
        }
        else
        {
            int nearestResIdx = FindNearestResourceCellIndex();
            if (nearestResIdx >= 0)
            {
                HexCoordinates resCo = unit.Grid.CellData[nearestResIdx].coordinates;
                int distToRes = selfCo.DistanceTo(resCo);
                AddDirectionalCompass(sensor, loc, resCo, distToRes);
            }
            else for (int i = 0; i < 6; i++) sensor.AddObservation(0f);
        }

        // ── Nearest ally unit (5) ─────────────────────────────────────────
        // The single most important obs for 2v2 cooperation: tells the
        // unit where its teammate is, how healthy it is, and whether it's
        // currently loaded with resources. Zeroed when there is no living
        // teammate (solo, 1v1, or 2v2 with the other ally already dead).
        HexUnit nearestAlly = FindNearestAlly();
        if (nearestAlly != null)
        {
            AddRelativePosition(sensor, nearestAlly.Location.Coordinates);
            int allyDist = selfCo.DistanceTo(nearestAlly.Location.Coordinates);
            sensor.AddObservation(Norm(allyDist, 20f));
            sensor.AddObservation(Norm(nearestAlly.CurrentHP, nearestAlly.MaxHP));
            sensor.AddObservation(Norm(nearestAlly.TotalCarried, tm.MaxCarryCapacity));
        }
        else for (int i = 0; i < 5; i++) sensor.AddObservation(0f);

        // ── Nearest wood cell (4) ─────────────────────────────────────────
        AddNearestResourceObservation(sensor, wood: true);

        // ── Nearest stone cell (4) ────────────────────────────────────────
        AddNearestResourceObservation(sensor, wood: false);

        // ── Nearest enemy unit (4) ────────────────────────────────────────
        HexUnit nearestEnemy = FindNearestEnemy();
        if (nearestEnemy != null)
        {
            AddRelativePosition(sensor, nearestEnemy.Location.Coordinates);
            sensor.AddObservation(Norm(nearestEnemy.CurrentHP, nearestEnemy.MaxHP));
            sensor.AddObservation(0f);
        }
        else for (int i = 0; i < 4; i++) sensor.AddObservation(0f);

        // ── Second-nearest enemy unit (4) ────────────────────────────────
        // In 2v2 the policy needs to model BOTH opponents to make sound
        // tactical choices (e.g. flanking, intercepting the one heading
        // for our home rather than the one near resources). Without this
        // obs, the agent only ever sees the closest opponent and is blind
        // to the other one's manoeuvres. Zeroed when there's only one
        // (or zero) living enemy on the map.
        HexUnit secondEnemy = FindSecondNearestEnemy(nearestEnemy);
        if (secondEnemy != null)
        {
            AddRelativePosition(sensor, secondEnemy.Location.Coordinates);
            sensor.AddObservation(Norm(secondEnemy.CurrentHP, secondEnemy.MaxHP));
            sensor.AddObservation(0f);
        }
        else for (int i = 0; i < 4; i++) sensor.AddObservation(0f);

        // ── Nearest enemy home (4) ────────────────────────────────────────
        Team nearestEnemyTeam = FindNearestEnemyTeam();
        if (nearestEnemyTeam != null)
        {
            AddRelativePosition(sensor,
                unit.Grid.CellData[nearestEnemyTeam.HomeCellIndex].coordinates);
            sensor.AddObservation(Norm(nearestEnemyTeam.HomeLevel, 3f));
            sensor.AddObservation(Norm(nearestEnemyTeam.HomeHP,
                nearestEnemyTeam.HomeLevel * 100f));
        }
        else for (int i = 0; i < 4; i++) sensor.AddObservation(0f);

        // ── Round (1) ─────────────────────────────────────────────────────
        sensor.AddObservation(Norm(tm.RoundNumber, 100f));
    }

    // ======================================================================
    // Action masking
    // ======================================================================

    public override void WriteDiscreteActionMask(IDiscreteActionMask actionMask)
    {
        if (!tm.IsActiveUnit(unit))
        {
            for (int a = 1; a < 18; a++)
                actionMask.SetActionEnabled(0, a, false);
            return;
        }

        HexCell loc = unit.IsDead ? default : unit.Location;

        for (int i = 0; i < 6; i++)
        {
            HexDirection d = (HexDirection)i;
            bool canMove = false, canAttack = false;
            if (!unit.IsDead && loc.TryGetNeighbor(d, out HexCell nb))
            {
                int moveCost = unit.GetAdjacentMoveCost(nb);
                canMove = unit.IsValidDestination(nb) &&
                            moveCost >= 0 &&
                            unit.ActionPointsRemaining >= moveCost;
                canAttack = nb.HasEnemyUnits(unit.Team) &&
                            moveCost >= 0 &&
                            unit.ActionPointsRemaining >= moveCost;
            }
            actionMask.SetActionEnabled(0, 1 + i, canMove);
            actionMask.SetActionEnabled(0, 10 + i, canAttack);
        }

        actionMask.SetActionEnabled(0, 7, tm.CanGatherWood());
        actionMask.SetActionEnabled(0, 8, tm.CanGatherStone());
        actionMask.SetActionEnabled(0, 9, tm.CanDeposit());
        actionMask.SetActionEnabled(0, 16, tm.CanUpgradeHome() || tm.CanWinByUpgrade());
        actionMask.SetActionEnabled(0, 17, tm.CanRaiseHome());
    }

    // ======================================================================
    // Actions
    // ======================================================================

    public override void OnActionReceived(ActionBuffers actions)
    {
        int diagTeam = unit?.Team?.TeamIndex ?? -1;

        if (!tm.IsActiveUnit(unit) || unit.IsTraveling)
        {
            if (diagTeam >= 0 && diagTeam < kMaxDiagTeams)
            {
                if (unit != null && unit.IsTraveling) s_diagTravelingSkips[diagTeam]++;
                else                                  s_diagInactiveSkips[diagTeam]++;
                if (unit != null && unit.IsDead)      s_diagDeaths[diagTeam]++;
            }
            DiagBumpAndMaybeLog();
            AddReward(penaltyTimestep);
            return;
        }

        int action = actions.DiscreteActions[0];
        LastActionIndex = action;
        LastActionName = action < ActionNames.Length ? ActionNames[action] : "unknown";
        float stepReward = penaltyTimestep;
        episodeSteps++;

        if (diagTeam >= 0 && diagTeam < kMaxDiagTeams)
        {
            s_diagActiveActions[diagTeam]++;
            if (action == 0)                       s_diagEndTurns[diagTeam]++;
            else if (action >= 1 && action <= 6)   s_diagMoves[diagTeam]++;
        }
        DiagBumpAndMaybeLog();

        bool enoughToWin = HasEnoughToWin();
        bool inventoryFullBefore = unit.TotalCarried >= tm.MaxCarryCapacity;
        int carriedBefore = unit.TotalCarried;

        // Snapshot pre-action distances for potential-based shaping.
        // Distance signals must be sampled BEFORE the action mutates state.
        HexCoordinates homeCo = unit.Grid.CellData[unit.Team.HomeCellIndex].coordinates;
        int distHomeBefore = unit.IsDead ? -1
            : unit.Location.Coordinates.DistanceTo(homeCo);
        int distResBefore = (unit.IsDead || inventoryFullBefore || enoughToWin)
            ? -1 : FindDistanceToNearestResource();
        int distEnemyHomeBefore = (unit.IsDead || rewardApproachingEnemyHome == 0f)
            ? -1 : FindDistanceToNearestEnemyHome();
        int distEnemyUnitBefore = (unit.IsDead || rewardApproachingEnemyUnit == 0f)
            ? -1 : FindDistanceToNearestEnemyUnit();

        if (!unit.IsDead) TrackPosition(unit.LocationCellIndex);

        switch (action)
        {
            // ── End Turn ──────────────────────────────────────────────────
            case 0:
                if (unit.ActionPointsRemaining > endTurnFreeApThreshold)
                    stepReward += penaltyEarlyEndTurn *
                                  (unit.ActionPointsRemaining - endTurnFreeApThreshold);
                recentCells.Clear();
                tm.EndCurrentUnitTurn();
                break;

            // ── Move ──────────────────────────────────────────────────────
            case int a when a >= 1 && a <= 6:
                {
                    HexDirection dir = (HexDirection)(a - 1);
                    if (!unit.IsDead && unit.Location.TryGetNeighbor(dir, out HexCell target))
                    {
                        bool isRevisit = recentCells.Contains(target.Index);
                        var path = new List<int> { unit.LocationCellIndex, target.Index };
                        if (tm.TryMoveActiveUnit(path))
                        {
                            episodeMoves++;
                            if (isRevisit) stepReward += penaltyOscillation;

                            // One-off arrival bonuses — fire only on the step
                            // that actually lands on the target cell, so the
                            // agent can't farm them by sitting adjacent.
                            if (!unit.IsDead)
                            {
                                int landedCell = unit.LocationCellIndex;

                                // Path-mirror penalty: did a teammate just
                                // walk through this same cell? Recorded on
                                // the team's sliding window in Team.cs.
                                // Only active in multi-unit-per-team
                                // scenarios; in solo / 1v1 the queue is
                                // empty or contains only this unit, so the
                                // check evaluates to false.
                                //
                                // SUPPRESSED when an enemy is within
                                // engagementRange of this unit — during a
                                // converging assault, walking the same
                                // cells as the teammate is the intended
                                // behaviour, and penalising it would
                                // discourage exactly the cooperation we
                                // want.
                                if (unit.Team != null &&
                                    unit.Team.DidOtherTeammateRecentlyVisit(
                                        unit, landedCell))
                                {
                                    HexUnit nearbyEnemy = FindNearestEnemy();
                                    int dToEnemy = nearbyEnemy != null
                                        ? unit.Location.Coordinates.DistanceTo(
                                            nearbyEnemy.Location.Coordinates)
                                        : int.MaxValue;
                                    if (dToEnemy > engagementRange)
                                        stepReward += penaltyMirrorPath;
                                }

                                unit.Team?.RecordTeamMove(unit, landedCell);

                                // Arrived at home with carry → reinforce the
                                // "come back" subgoal. Deposit gives the real
                                // payoff; this just nudges the final step.
                                if (carriedBefore > 0 &&
                                    landedCell == unit.Team.HomeCellIndex)
                                    stepReward += rewardHomeArrivalWithCarry;

                                // Arrived at a harvestable cell while not full
                                // → reinforce the "find resources" subgoal.
                                if (!inventoryFullBefore && !enoughToWin &&
                                    (unit.Grid.CellWoodResources[landedCell] > 0 ||
                                     unit.Grid.CellStoneResources[landedCell] > 0))
                                    stepReward += rewardResourceArrival;

                                // Arrived on an enemy home cell — RaiseHome
                                // is now available. Fires once per arrival;
                                // not exploitable by oscillation because the
                                // oscillation penalty catches revisits.
                                foreach (Team t in tm.Teams)
                                {
                                    if (t == unit.Team) continue;
                                    if (t.HomeCellIndex == landedCell)
                                    {
                                        stepReward += rewardEnemyHomeArrival;
                                        break;
                                    }
                                }
                            }
                        }
                        else stepReward += penaltyInvalidAction;
                    }
                    else stepReward += penaltyInvalidAction;
                    break;
                }

            // ── Gather Wood ───────────────────────────────────────────────
            case 7:
                {
                    int woodBefore = unit.WoodCarried;
                    if (tm.TryGatherWood())
                    {
                        int gained = unit.WoodCarried - woodBefore;
                        if (!enoughToWin)
                        {
                            // Scale by fraction of a full gather actually
                            // collected — partial gathers near depleted cells
                            // earn proportionally less.
                            float ratio = tm.GatherAmount > 0
                                ? gained / (float)tm.GatherAmount : 1f;
                            stepReward += rewardGatherResource * ratio;
                            episodeGathers++;
                        }
                        else stepReward += penaltyInvalidAction * 0.5f;
                    }
                    else stepReward += penaltyInvalidAction;
                    break;
                }

            // ── Gather Stone ──────────────────────────────────────────────
            case 8:
                {
                    int stoneBefore = unit.StoneCarried;
                    if (tm.TryGatherStone())
                    {
                        int gained = unit.StoneCarried - stoneBefore;
                        if (!enoughToWin)
                        {
                            float ratio = tm.GatherAmount > 0
                                ? gained / (float)tm.GatherAmount : 1f;
                            stepReward += rewardGatherResource * ratio;
                            episodeGathers++;
                        }
                        else stepReward += penaltyInvalidAction * 0.5f;
                    }
                    else stepReward += penaltyInvalidAction;
                    break;
                }

            // ── Deposit ───────────────────────────────────────────────────
            case 9:
                {
                    int totalBefore = unit.TotalCarried;
                    if (tm.TryDeposit())
                    {
                        // Flat + per-unit + full-load bonus. A full 200
                        // deposit pays 0.20 + 1.00 + 0.50 = 1.70 vs a 50-
                        // unit dribble paying just 0.20 + 0.25 = 0.45.
                        // Makes 'fill before returning' strictly better.
                        stepReward += rewardDepositFlat +
                                      totalBefore * rewardDepositPerUnit;
                        float depositFullness = tm.MaxCarryCapacity > 0
                            ? totalBefore / (float)tm.MaxCarryCapacity : 0f;
                        if (depositFullness >= depositFullBonusThreshold)
                            stepReward += rewardDepositFullBonus;

                        if (!enoughToWin)
                            stepReward += ComputeAndUpdateProgressReward();
                        else
                            prevUpgradeProgress = ComputeCurrentProgress();
                        episodeDeposits++;
                    }
                    else stepReward += penaltyInvalidAction;
                    break;
                }

            // ── Attack ────────────────────────────────────────────────────
            case int a when a >= 10 && a <= 15:
                {
                    HexDirection dir = (HexDirection)(a - 10);
                    if (!unit.IsDead && unit.Location.TryGetNeighbor(dir, out HexCell target))
                    {
                        if (tm.TryAttackUnits(target)) episodeAttacks++;
                        else stepReward += penaltyInvalidAction;
                    }
                    else stepReward += penaltyInvalidAction;
                    break;
                }

            // ── Upgrade / Claim Victory ───────────────────────────────────
            case 16:
                if (tm.TryWinByUpgrade())
                    stepReward += rewardWin;
                else if (tm.TryUpgradeHome())
                {
                    stepReward += rewardUpgradeHome;
                    prevUpgradeProgress = 0f;
                    episodeUpgrades++;
                }
                else stepReward += penaltyInvalidAction;
                break;

            // ── Raise Home ────────────────────────────────────────────────
            case 17:
                if (tm.TryRaiseHome()) stepReward += rewardDamageEnemyHome;
                else stepReward += penaltyInvalidAction;
                break;

            default:
                stepReward += penaltyInvalidAction;
                break;
        }

        // ── Symmetric approach shaping (potential-based) ───────────────────
        // Both home- and resource-approach rewards now apply BOTH directions:
        // moving closer adds reward, moving farther subtracts the same amount.
        // This is what stops the "drift away from base" failure mode — the
        // old one-sided reward let the agent farm tiny positives by oscillating
        // outward then briefly returning. Symmetric shaping is invariant to
        // cycles and matches the potential-based shaping theorem (Ng et al.).
        if (!unit.IsDead && distHomeBefore >= 0 && unit.TotalCarried > 0)
        {
            int distHomeAfter = unit.Location.Coordinates.DistanceTo(homeCo);
            int delta = distHomeBefore - distHomeAfter; // +closer / −farther
            float rate = inventoryFullBefore
                ? rewardApproachingHomeWhenFull
                : rewardApproachingHome;
            stepReward += delta * rate;
        }

        if (!unit.IsDead && distResBefore >= 0)
        {
            int distResAfter = FindDistanceToNearestResource();
            if (distResAfter >= 0)
            {
                int delta = distResBefore - distResAfter; // +closer / −farther
                // Scale by how empty we are — fully loaded units shouldn't be
                // pulled toward more resources, they should head home.
                float emptiness = 1f - Mathf.Clamp01(
                    (float)unit.TotalCarried / Mathf.Max(1, tm.MaxCarryCapacity));
                stepReward += delta * rewardApproachingResource * emptiness;
            }
        }

        // ── Enemy-home approach (multi-team scenarios only) ───────────────
        // Symmetric per-hex pull toward the nearest enemy home, weighted by
        // emptiness so an attacker isn't pulled away from depositing a full
        // load. Tuned to match the homeward gradient — committing to an
        // attack gives a coherent, usable signal toward the target.
        if (!unit.IsDead && distEnemyHomeBefore >= 0)
        {
            int distEnemyHomeAfter = FindDistanceToNearestEnemyHome();
            if (distEnemyHomeAfter >= 0)
            {
                int delta = distEnemyHomeBefore - distEnemyHomeAfter;
                float emptiness = 1f - Mathf.Clamp01(
                    (float)unit.TotalCarried / Mathf.Max(1, tm.MaxCarryCapacity));
                stepReward += delta * rewardApproachingEnemyHome * emptiness;
            }
        }

        // ── Enemy-unit approach (multi-team scenarios only) ───────────────
        // Same symmetric pattern, but pointing at the nearest enemy unit.
        // Lets the agent learn to intercept and harass — important for the
        // defensive case, where the enemy is moving toward our home and we
        // want to close the gap rather than turtle.
        if (!unit.IsDead && distEnemyUnitBefore >= 0)
        {
            int distEnemyUnitAfter = FindDistanceToNearestEnemyUnit();
            if (distEnemyUnitAfter >= 0)
            {
                int delta = distEnemyUnitBefore - distEnemyUnitAfter;
                float emptiness = 1f - Mathf.Clamp01(
                    (float)unit.TotalCarried / Mathf.Max(1, tm.MaxCarryCapacity));
                stepReward += delta * rewardApproachingEnemyUnit * emptiness;
            }
        }

        stepReward += ComputeRoamingPenalty();
        stepReward += ComputeTeamCoordinationReward();
        stepReward += ComputeHomeDefenseReward();

        LastStepReward = stepReward;
        episodeTotalReward += stepReward;
        AddReward(stepReward);
    }

    // ======================================================================
    // Anti-oscillation
    // ======================================================================

    void TrackPosition(int cellIndex)
    {
        recentCells.Enqueue(cellIndex);
        while (recentCells.Count > oscillationWindowSize)
            recentCells.Dequeue();
    }

    // ======================================================================
    // Reward helpers
    // ======================================================================

    bool HasEnoughToWin()
    {
        if (unit.Team == null || tm == null) return false;
        Team team = unit.Team;
        int woodNeeded = tm.GetTotalWoodToWin(team);
        int stoneNeeded = tm.GetTotalStoneToWin(team);
        return (team.WoodStored + unit.WoodCarried) >= woodNeeded &&
               (team.StoneStored + unit.StoneCarried) >= stoneNeeded;
    }

    float ComputeCurrentProgress()
    {
        if (unit.Team == null || tm == null) return 0f;
        float woodGoal = tm.NextUpgradeWoodCost > 0 ? tm.NextUpgradeWoodCost : 1f;
        float stoneGoal = tm.NextUpgradeStoneCost > 0 ? tm.NextUpgradeStoneCost : 1f;
        return (Mathf.Clamp01(unit.Team.WoodStored / woodGoal) +
                Mathf.Clamp01(unit.Team.StoneStored / stoneGoal)) * 0.5f;
    }

    float ComputeAndUpdateProgressReward()
    {
        float progress = ComputeCurrentProgress();
        float delta = progress - prevUpgradeProgress;
        prevUpgradeProgress = progress;
        return delta > 0f ? delta * rewardResourceProgress * 100f : 0f;
    }

    /// <summary>
    /// Hex-step distance from the unit's current cell to the nearest cell
    /// that still has any harvestable wood or stone. Returns −1 if the map
    /// has no resources left or the unit has no valid location.
    /// Used by the symmetric "approach resource" shaping term.
    /// </summary>
    int FindDistanceToNearestResource()
    {
        int idx = FindNearestResourceCellIndex();
        if (idx < 0) return -1;
        return unit.Location.Coordinates.DistanceTo(
            unit.Grid.CellData[idx].coordinates);
    }

    /// <summary>
    /// Hex-step distance to the closest still-living enemy UNIT (not its
    /// home). Returns −1 in solo mode or when the agent's own unit is dead.
    /// Used by the symmetric approach-enemy-unit shaping term for the
    /// defensive / interception case.
    /// </summary>
    int FindDistanceToNearestEnemyUnit()
    {
        if (unit == null || unit.IsDead || unit.Team == null || tm == null) return -1;
        HexCoordinates self = unit.Location.Coordinates;
        int best = int.MaxValue;
        foreach (Team t in tm.Teams)
        {
            if (t == unit.Team) continue;
            foreach (HexUnit u in t.Units)
            {
                if (u == null || u.IsDead) continue;
                int d = self.DistanceTo(u.Location.Coordinates);
                if (d < best) best = d;
            }
        }
        return best == int.MaxValue ? -1 : best;
    }

    /// <summary>
    /// Hex-step distance to the closest still-living enemy team's home cell.
    /// Returns −1 if no enemies remain (solo mode, or last enemy eliminated).
    /// Used by the symmetric approach-enemy-home shaping term in multi-team
    /// scenarios.
    /// </summary>
    int FindDistanceToNearestEnemyHome()
    {
        if (unit == null || unit.IsDead || unit.Team == null || tm == null) return -1;
        HexCoordinates self = unit.Location.Coordinates;
        int best = int.MaxValue;
        foreach (Team t in tm.Teams)
        {
            if (t == unit.Team || t.HomeCellIndex < 0) continue;
            int d = self.DistanceTo(
                unit.Grid.CellData[t.HomeCellIndex].coordinates);
            if (d < best) best = d;
        }
        return best == int.MaxValue ? -1 : best;
    }

    /// <summary>
    /// Cell index of the closest cell that still has harvestable wood or
    /// stone, or −1 if no resources remain on the map. Used by both the
    /// approach-resource shaping reward and the resource compass observation.
    /// </summary>
    int FindNearestResourceCellIndex()
    {
        if (unit == null || unit.IsDead || unit.Grid == null) return -1;
        HexCoordinates self = unit.Location.Coordinates;
        int[] wood = unit.Grid.CellWoodResources;
        int[] stone = unit.Grid.CellStoneResources;
        int bestIdx = -1, bestDist = int.MaxValue;
        for (int i = 0; i < wood.Length; i++)
        {
            if (wood[i] <= 0 && stone[i] <= 0) continue;
            int d = self.DistanceTo(unit.Grid.CellData[i].coordinates);
            if (d < bestDist) { bestDist = d; bestIdx = i; }
        }
        return bestIdx;
    }

    /// <summary>
    /// Write 6 floats (one per hex direction NE..NW) indicating whether
    /// moving in that direction would bring us closer to <paramref name="target"/>:
    ///   −1 = neighbour is closer to target (the "go this way" arrow)
    ///    0 = same distance, or no neighbour in that direction
    ///   +1 = neighbour is farther from target
    /// Sign convention is "delta distance" so the agent reads negatives as
    /// good directions when the goal is "minimise distance to target".
    /// </summary>
    void AddDirectionalCompass(VectorSensor sensor, HexCell loc,
                               HexCoordinates target, int currentDist)
    {
        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
        {
            if (loc.TryGetNeighbor(d, out HexCell nb))
            {
                int nbDist = nb.Coordinates.DistanceTo(target);
                int delta = nbDist - currentDist;
                sensor.AddObservation(Mathf.Clamp(delta, -1, 1));
            }
            else sensor.AddObservation(0f);
        }
    }

    float ComputeRoamingPenalty()
    {
        if (unit.IsDead || unit.Team == null || unit.TotalCarried <= 0) return 0f;
        HexCell loc = unit.Location;
        for (HexDirection d = HexDirection.NE; d <= HexDirection.NW; d++)
            if (loc.TryGetNeighbor(d, out HexCell nb) &&
                nb.Index == unit.Team.HomeCellIndex) return 0f;

        float fullness = (float)unit.TotalCarried / tm.MaxCarryCapacity;
        return penaltyRoamingWithLoad * fullness;
    }

    /// <summary>
    /// Team coordination shaping for 2v2 (and any future N-units-per-team
    /// scenarios). Two opposed signals:
    ///   • +rewardCoordinatedAssault when teammate is within coordination
    ///     range AND an enemy is within engagement range — sticking
    ///     together IS the right call when there's a fight to be had.
    ///   • +penaltyAllyCluster when teammate is within coordination range
    ///     but no enemy is anywhere near — wasted map coverage and likely
    ///     mirrored gathering. Pushes the units to spread out by default.
    /// Returns 0 in solo and 1v1 (no teammate) and in any state with no
    /// living enemy on the map.
    /// </summary>
    float ComputeTeamCoordinationReward()
    {
        if (unit == null || unit.IsDead) return 0f;
        HexUnit ally = FindNearestAlly();
        if (ally == null) return 0f;
        HexUnit enemy = FindNearestEnemy();
        if (enemy == null) return 0f;

        int allyDist = unit.Location.Coordinates.DistanceTo(
            ally.Location.Coordinates);
        int enemyDist = unit.Location.Coordinates.DistanceTo(
            enemy.Location.Coordinates);

        if (allyDist <= coordinationRange && enemyDist <= engagementRange)
            return rewardCoordinatedAssault;
        if (allyDist <= coordinationRange && enemyDist > engagementRange + 2)
            return penaltyAllyCluster;
        return 0f;
    }

    /// <summary>
    /// Home-defence shaping. Fires when an enemy unit is close to our
    /// home AND this unit is sitting on or next to that home — i.e. the
    /// unit is actively garrisoning a base under threat. Returns 0
    /// otherwise; in particular it's quiet when no enemy is around so
    /// it doesn't accidentally tax peacetime returns-to-deposit.
    /// </summary>
    float ComputeHomeDefenseReward()
    {
        if (unit == null || unit.IsDead || unit.Team == null) return 0f;
        if (unit.Team.HomeCellIndex < 0) return 0f;

        HexUnit enemy = FindNearestEnemy();
        if (enemy == null) return 0f;

        HexCoordinates homeCo = unit.Grid.CellData[
            unit.Team.HomeCellIndex].coordinates;
        int enemyToHome = enemy.Location.Coordinates.DistanceTo(homeCo);
        if (enemyToHome > enemyAtHomeRange) return 0f;

        int selfToHome = unit.Location.Coordinates.DistanceTo(homeCo);
        if (selfToHome <= 1) return rewardDefendHome;
        return 0f;
    }

    // ======================================================================
    // External reward hooks
    // ======================================================================

    public void OnDealtDamage(int damage) => AddAndTrack(damage * rewardDealDamage);
    public void OnKilledEnemy() { AddAndTrack(rewardKillUnit); episodeKills++; }
    public void OnDealtHomeDamage(int dmg) => AddAndTrack(dmg * rewardDamageEnemyHome);
    public void OnReducedHomeLevel() => AddAndTrack(rewardReduceHomeLevel);
    public void OnEliminatedTeam() => AddAndTrack(rewardEliminateTeam);
    public void OnDied() { AddAndTrack(penaltyDie); episodeDeaths++; }
    public void OnWin() { AddAndTrack(rewardWin); RecordEpisodeStats(); EndEpisode(); }
    public void OnLose() { AddAndTrack(penaltyLose); RecordEpisodeStats(); EndEpisode(); }

    // ======================================================================
    // Heuristic / Stats
    // ======================================================================

    public override void Heuristic(in ActionBuffers actionsOut)
        => actionsOut.DiscreteActions.Array[0] = 0;

    void RecordEpisodeStats()
    {
        var s = Academy.Instance.StatsRecorder;

        // Pooled metrics — what we already had.
        s.Add("HexUnit/Moves", episodeMoves, StatAggregationMethod.Sum);
        s.Add("HexUnit/Attacks", episodeAttacks, StatAggregationMethod.Sum);
        s.Add("HexUnit/Gathers", episodeGathers, StatAggregationMethod.Sum);
        s.Add("HexUnit/Deposits", episodeDeposits, StatAggregationMethod.Sum);
        s.Add("HexUnit/Deaths", episodeDeaths, StatAggregationMethod.Sum);
        s.Add("HexUnit/Kills", episodeKills, StatAggregationMethod.Sum);
        s.Add("HexUnit/Upgrades", episodeUpgrades, StatAggregationMethod.Sum);
        s.Add("HexUnit/TotalReward", episodeTotalReward, StatAggregationMethod.Average);
        s.Add("HexUnit/Steps", episodeSteps, StatAggregationMethod.Average);

        // Per-team breakdown — lets us see at a glance whether both sides
        // are actually playing in multi-team scenarios. If T0/Steps and
        // T1/Steps drift apart in TensorBoard, that's the smoking gun for
        // turn-flow asymmetry; if they stay roughly equal, the per-side
        // visual difference is just choice-of-action (one is moving, the
        // other gathering on the same cell).
        int t = unit?.Team?.TeamIndex ?? 0;
        string p = $"HexUnit/T{t}";
        s.Add($"{p}/Moves", episodeMoves, StatAggregationMethod.Sum);
        s.Add($"{p}/Gathers", episodeGathers, StatAggregationMethod.Sum);
        s.Add($"{p}/Deposits", episodeDeposits, StatAggregationMethod.Sum);
        s.Add($"{p}/Attacks", episodeAttacks, StatAggregationMethod.Sum);
        s.Add($"{p}/Kills", episodeKills, StatAggregationMethod.Sum);
        s.Add($"{p}/Deaths", episodeDeaths, StatAggregationMethod.Sum);
        s.Add($"{p}/Upgrades", episodeUpgrades, StatAggregationMethod.Sum);
        s.Add($"{p}/Steps", episodeSteps, StatAggregationMethod.Average);
        s.Add($"{p}/TotalReward", episodeTotalReward, StatAggregationMethod.Average);
    }

    // ======================================================================
    // Observation helpers
    // ======================================================================

    void AddAndTrack(float amount) { AddReward(amount); episodeTotalReward += amount; }
    static float Norm(float v, float max) => max > 0f ? Mathf.Clamp01(v / max) : 0f;

    void AddRelativePosition(VectorSensor sensor, HexCoordinates target)
    {
        HexCoordinates self = unit.Location.Coordinates;
        sensor.AddObservation(Mathf.Clamp((target.X - self.X) / 20f, -1f, 1f));
        sensor.AddObservation(Mathf.Clamp((target.Z - self.Z) / 20f, -1f, 1f));
    }

    void AddNearestResourceObservation(VectorSensor sensor, bool wood)
    {
        HexCoordinates self = unit.Location.Coordinates;
        int bestIdx = -1, bestDist = int.MaxValue, bestAmt = 0;
        int[] arr = wood ? unit.Grid.CellWoodResources : unit.Grid.CellStoneResources;

        for (int i = 0; i < arr.Length; i++)
        {
            if (arr[i] <= 0) continue;
            int d = self.DistanceTo(unit.Grid.CellData[i].coordinates);
            if (d < bestDist) { bestDist = d; bestIdx = i; bestAmt = arr[i]; }
        }

        if (bestIdx >= 0)
        {
            HexCoordinates rc = unit.Grid.CellData[bestIdx].coordinates;
            sensor.AddObservation(Mathf.Clamp((rc.X - self.X) / 20f, -1f, 1f));
            sensor.AddObservation(Mathf.Clamp((rc.Z - self.Z) / 20f, -1f, 1f));
            sensor.AddObservation(Norm(bestAmt, 300f));
            sensor.AddObservation(Norm(bestDist, 20f));
        }
        else for (int i = 0; i < 4; i++) sensor.AddObservation(0f);
    }

    HexUnit FindNearestEnemy()
    {
        HexUnit nearest = null; int best = int.MaxValue;
        HexCoordinates self = unit.Location.Coordinates;
        foreach (Team t in tm.Teams)
        {
            if (t == unit.Team) continue;
            foreach (HexUnit u in t.Units)
            {
                if (u.IsDead) continue;
                int d = self.DistanceTo(u.Location.Coordinates);
                if (d < best) { best = d; nearest = u; }
            }
        }
        return nearest;
    }

    /// <summary>
    /// Second-nearest living enemy unit, used for the 2v2 observation block
    /// so the policy can track both opponents at once. Pass the result of
    /// <see cref="FindNearestEnemy"/> as <paramref name="exclude"/> so we
    /// don't return the same unit twice.
    /// </summary>
    HexUnit FindSecondNearestEnemy(HexUnit exclude)
    {
        HexUnit nearest = null; int best = int.MaxValue;
        HexCoordinates self = unit.Location.Coordinates;
        foreach (Team t in tm.Teams)
        {
            if (t == unit.Team) continue;
            foreach (HexUnit u in t.Units)
            {
                if (u.IsDead || u == exclude) continue;
                int d = self.DistanceTo(u.Location.Coordinates);
                if (d < best) { best = d; nearest = u; }
            }
        }
        return nearest;
    }

    /// <summary>
    /// Closest living teammate (same team, different unit). Returns null
    /// in solo / 1v1 scenarios where there is no second team member, or
    /// in 2v2 if the other team member is currently dead/respawning.
    /// </summary>
    HexUnit FindNearestAlly()
    {
        if (unit == null || unit.Team == null) return null;
        HexUnit nearest = null; int best = int.MaxValue;
        HexCoordinates self = unit.Location.Coordinates;
        foreach (HexUnit u in unit.Team.Units)
        {
            if (u == null || u == unit || u.IsDead) continue;
            int d = self.DistanceTo(u.Location.Coordinates);
            if (d < best) { best = d; nearest = u; }
        }
        return nearest;
    }

    Team FindNearestEnemyTeam()
    {
        Team nearest = null; int best = int.MaxValue;
        HexCoordinates self = unit.Location.Coordinates;
        foreach (Team t in tm.Teams)
        {
            if (t == unit.Team) continue;
            int d = self.DistanceTo(unit.Grid.CellData[t.HomeCellIndex].coordinates);
            if (d < best) { best = d; nearest = t; }
        }
        return nearest;
    }
}