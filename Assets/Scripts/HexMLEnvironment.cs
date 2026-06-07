using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using Unity.MLAgents;
using Unity.MLAgents.Policies;

/// <summary>
/// Coordinates the ML-Agents multi-agent training environment.
/// Attach to the same GameObject as TurnManager.
///
/// KEY DESIGN — everything deferred to LateUpdate:
///
/// ML-Agents locks the agent system during OnActionReceived. Any call to
/// RequestDecision() or EndEpisode() made while inside that lock (i.e.
/// originating from the same call stack as OnActionReceived) is silently
/// dropped or causes the episode to not restart.
///
/// Two things must never be called from inside OnActionReceived:
///   1. RequestDecision()  — buffered in pendingDecisionUnit, fired in LateUpdate.
///   2. EndEpisode()       — buffered in pendingEpisodeEnd, fired in LateUpdate.
///
/// Both HandleUnitTurnStarted and HandleGameWon (which are fired synchronously
/// from TurnManager methods that are themselves called from OnActionReceived)
/// only set flags. LateUpdate consumes them once per frame after the action
/// stack has fully unwound.
/// </summary>
public class HexMLEnvironment : MonoBehaviour
{
    // ======================================================================
    // Training scenarios
    // ======================================================================

    public enum TrainingScenario
    {
        /// <summary>1 team, 1 unit. Teaches economy before combat.</summary>
        Solo_1v0,
        /// <summary>2 teams, 1 unit each.</summary>
        Combat_1v1,
        /// <summary>2 teams, 2 units each.</summary>
        Combat_2v2,
    }

    // ======================================================================
    // Inspector
    // ======================================================================

    [Header("Scenario")]
    [SerializeField] TrainingScenario scenario = TrainingScenario.Solo_1v0;

    [Header("Training Settings")]
    [Tooltip("Episode timeout. Solo_1v0 win path needs ~3 round-trips at low " +
             "scenario costs; 80 rounds gives ~2× headroom for exploration.")]
    [SerializeField] int maxRounds = 80;

    [Tooltip("If true, agents that don't win by max rounds get OnLose() instead " +
             "of a neutral EndEpisode(). Critical for solo training so the agent " +
             "feels real time pressure — without it, doing nothing is locally " +
             "optimal because there's no penalty for failing to win.")]
    [SerializeField] bool timeoutCountsAsLoss = true;

    [Tooltip("Period (in Academy steps) at which we trigger RequestDecision " +
             "for the currently-active unit. Replaces the per-unit " +
             "DecisionRequester component, which subscribes to Academy in " +
             "Awake() and therefore can't be cleanly silenced when the unit " +
             "is off-turn. With a single pump gated on TurnManager.ActiveUnit, " +
             "the inactive side gets ZERO RequestAction / OnActionReceived " +
             "callbacks — only the unit whose turn is actually running " +
             "produces experience for the trainer.")]
    [SerializeField] int decisionPumpPeriod = 20;
    [SerializeField] bool regenerateMapEachEpisode = true;
    [SerializeField] bool enableCsvLogging = true;
    [SerializeField] string logFolder = "MLLogs";

    [Header("References")]
    [SerializeField] TurnManager turnManager;
    [SerializeField] HexMapGenerator mapGenerator;
    [SerializeField] HexGrid grid;

    // ======================================================================
    // Runtime state
    // ======================================================================

    int episodeCount;
    bool episodeEnded;
    List<HexUnitAgent> allAgents = new();

    // ── Deferred decision request ─────────────────────────────────────────
    // Set by HandleUnitTurnStarted; consumed by LateUpdate.
    HexUnit pendingDecisionUnit;

    // ── Deferred episode end ──────────────────────────────────────────────
    // Set by HandleGameWon / Update (max rounds); consumed by LateUpdate.
    // Using a flag + winner reference instead of calling EndEpisodeForAll
    // directly from event handlers ensures we are never inside OnActionReceived
    // when EndEpisode() is called on the agents.
    bool pendingEpisodeEnd;
    Team pendingEpisodeWinner;   // null = timeout/draw

    // CSV
    StreamWriter csvWriter;
    int stepCount;

    // ======================================================================
    // Unity lifecycle
    // ======================================================================

    void Awake()
    {
        TurnManagerEvents.OnUnitTurnStarted += HandleUnitTurnStarted;
        TurnManagerEvents.OnUnitDealtDamage += HandleUnitDealtDamage;
        TurnManagerEvents.OnUnitKilled += HandleUnitKilled;
        TurnManagerEvents.OnUnitDied += HandleUnitDied;
        TurnManagerEvents.OnHomeDamageDealt += HandleHomeDamageDealt;
        TurnManagerEvents.OnHomeLevelReduced += HandleHomeLevelReduced;
        TurnManagerEvents.OnTeamEliminated += HandleTeamEliminated;
        TurnManagerEvents.OnGameWon += HandleGameWon;

        // Central decision pump. Subscribe ONCE at scene start; the pump
        // itself decides every Academy step whether to call RequestDecision,
        // and on which unit. Replaces per-unit DecisionRequester components,
        // which can't be selectively silenced (they hook AgentPreStep in
        // their own Awake() and stay subscribed for the lifetime of the
        // GameObject regardless of `.enabled`).
        Academy.Instance.AgentPreStep += OnAcademyPreStep;
    }

    void OnDestroy()
    {
        TurnManagerEvents.OnUnitTurnStarted -= HandleUnitTurnStarted;
        TurnManagerEvents.OnUnitDealtDamage -= HandleUnitDealtDamage;
        TurnManagerEvents.OnUnitKilled -= HandleUnitKilled;
        TurnManagerEvents.OnUnitDied -= HandleUnitDied;
        TurnManagerEvents.OnHomeDamageDealt -= HandleHomeDamageDealt;
        TurnManagerEvents.OnHomeLevelReduced -= HandleHomeLevelReduced;
        TurnManagerEvents.OnTeamEliminated -= HandleTeamEliminated;
        TurnManagerEvents.OnGameWon -= HandleGameWon;
        if (Academy.IsInitialized)
            Academy.Instance.AgentPreStep -= OnAcademyPreStep;
        CloseCsv();
    }

    // ──────────────────────────────────────────────────────────────────────
    // Central decision pump
    //
    // Fires RequestDecision on the active unit every `decisionPumpPeriod`
    // Academy steps, and only on the active unit. Off-turn units, dead
    // units, and traveling units are skipped — none of them generate any
    // RequestAction, so none of them receive OnActionReceived callbacks.
    //
    // This is the only place in the project that periodically calls
    // RequestDecision. The pendingDecisionUnit / LateUpdate path still
    // exists alongside it to fire ONE immediate decision the frame a turn
    // starts (so we don't have to wait up to `decisionPumpPeriod` steps
    // for the first action of every turn).
    // ──────────────────────────────────────────────────────────────────────
    void OnAcademyPreStep(int academyStepCount)
    {
        if (academyStepCount % decisionPumpPeriod != 0) return;
        if (turnManager == null || !turnManager.Initialized || turnManager.GameOver) return;
        if (pendingEpisodeEnd) return;

        HexUnit active = turnManager.ActiveUnit;
        if (active == null || active.IsDead || active.IsTraveling) return;

        var agent = active.GetComponent<HexUnitAgent>();
        if (agent != null) agent.RequestDecision();
    }

    void Start()
    {
        if (enableCsvLogging) OpenCsv();
        BeginEpisode();
    }

    void Update()
    {
        // Only flag the episode end here — never call EndEpisodeForAll directly.
        if (episodeEnded || pendingEpisodeEnd) return;
        if (turnManager.Initialized && turnManager.RoundNumber > maxRounds)
        {
            Debug.Log($"[HexMLEnvironment] Max rounds ({maxRounds}) reached.");
            pendingEpisodeEnd = true;
            pendingEpisodeWinner = null;  // timeout = draw
        }
    }

    /// <summary>
    /// LateUpdate runs after all Update() and OnActionReceived() calls have
    /// fully returned for the current frame. This is the only place we call
    /// RequestDecision() or EndEpisode(), guaranteeing we are never inside
    /// the ML-Agents action processing lock.
    /// </summary>
    void LateUpdate()
    {
        // Episode end takes priority over a new decision request.
        if (pendingEpisodeEnd)
        {
            pendingEpisodeEnd = false;
            pendingDecisionUnit = null;
            EndEpisodeForAll(pendingEpisodeWinner);
            return;
        }

        if (pendingDecisionUnit == null) return;

        HexUnit unit = pendingDecisionUnit;
        pendingDecisionUnit = null;

        var agent = unit.GetComponent<HexUnitAgent>();
        if (agent != null)
        {
            agent.RequestDecision();
            LogStep(unit, "DecisionRequested");
        }
    }

    // ======================================================================
    // Scenario configuration
    // ======================================================================

    void ApplyScenario()
    {
        switch (scenario)
        {
            case TrainingScenario.Solo_1v0:
                // Achievable economy: ~190 wood + ~190 stone total needed
                // (30 + 60 upgrades + 100 win). With 200 carry capacity and
                // 10/gather, that's ~2 full round-trips — comfortably winnable
                // within an 80-round episode budget.
                turnManager.SetScenario(
                    teamCount: 1,
                    unitsPerTeam: new[] { 1 },
                    upgWoodCost: new[] { 30, 60 },
                    upgStoneCost: new[] { 30, 60 },
                    winWoodCost: 100,
                    winStoneCost: 100);
                Debug.Log("[HexMLEnvironment] Scenario: Solo 1v0 " +
                          "(upgrades 30/60, win 100/100 — winnable economy)");
                break;

            case TrainingScenario.Combat_1v1:
                // Contested economy: ~360 wood + ~360 stone total needed
                // (50 + 100 upgrades + 200 win), roughly 4 full round-trips.
                // Deliberately longer than Solo so a rushing opponent can
                // realistically destroy the enemy home before the economy
                // race completes — creates a real trade-off between
                // turtling and aggression. Set lower than the original
                // 1000/1000 Inspector default which was unwinnable in the
                // 250-round budget once an opponent is present.
                turnManager.SetScenario(
                    teamCount: 2,
                    unitsPerTeam: new[] { 1, 1 },
                    upgWoodCost: new[] { 50, 100 },
                    upgStoneCost: new[] { 50, 100 },
                    winWoodCost: 200,
                    winStoneCost: 200);
                Debug.Log("[HexMLEnvironment] Scenario: Combat 1v1 " +
                          "(upgrades 50/100, win 200/200 — economy contested by combat)");
                break;

            case TrainingScenario.Combat_2v2:
                // Two units per team means the team-level resource
                // gathering rate is ~2× the 1v1 rate. To keep the
                // economic ↔ military balance intact we scale the
                // upgrade and victory costs roughly in proportion:
                //   1v1: upgrades 50/100, win 200/200 → 350 each
                //   2v2: upgrades 100/200, win 400/400 → 700 each
                // Combined with the new coordinated-assault, home-defence,
                // anti-mirror and ally-cluster shaping (HexUnitAgent), the
                // policy has a clean per-step incentive to split roles
                // (one gathers, one harasses), to converge when contact
                // is imminent, and to garrison the base under threat.
                turnManager.SetScenario(
                    teamCount: 2,
                    unitsPerTeam: new[] { 2, 2 },
                    upgWoodCost: new[] { 100, 200 },
                    upgStoneCost: new[] { 100, 200 },
                    winWoodCost: 400,
                    winStoneCost: 400);
                Debug.Log("[HexMLEnvironment] Scenario: Combat 2v2 " +
                          "(upgrades 100/200, win 400/400 — pricier economy " +
                          "to leave room for emergent role specialisation)");
                break;
        }
    }

    // ======================================================================
    // Episode management
    // ======================================================================

    void BeginEpisode()
    {
        episodeEnded = false;
        pendingEpisodeEnd = false;
        pendingEpisodeWinner = null;
        pendingDecisionUnit = null;
        stepCount = 0;
        episodeCount++;

        ApplyScenario();

        if (regenerateMapEachEpisode)
        {
            int x = grid.CellCountX > 0 ? grid.CellCountX : 20;
            int z = grid.CellCountZ > 0 ? grid.CellCountZ : 15;
            mapGenerator.GenerateMap(x, z, grid.Wrapping);
        }

        // Initialize fires OnUnitTurnStarted synchronously for the first unit.
        // That sets pendingDecisionUnit, which LateUpdate will service next frame.
        turnManager.Initialize();

        // Collect fresh agent list from the newly spawned units.
        allAgents.Clear();
        foreach (Team team in turnManager.Teams)
            foreach (HexUnit unit in team.Units)
            {
                var agent = unit.GetComponent<HexUnitAgent>();
                if (agent != null) allAgents.Add(agent);
            }

        Debug.Log($"[HexMLEnvironment] Episode {episodeCount} started — " +
                  $"{allAgents.Count} agent(s), scenario: {scenario}");
    }

    /// <summary>
    /// End the episode for every agent. Always called from LateUpdate so
    /// we are guaranteed to be outside any OnActionReceived call stack.
    /// </summary>
    void EndEpisodeForAll(Team winner)
    {
        if (episodeEnded) return;
        episodeEnded = true;

        foreach (HexUnitAgent agent in allAgents)
        {
            if (agent == null) continue;
            HexUnit unit = agent.GetComponent<HexUnit>();
            bool agentWon = winner != null && unit != null && unit.Team == winner;

            if (agentWon) agent.OnWin();
            else if (winner != null) agent.OnLose();
            else if (timeoutCountsAsLoss) agent.OnLose();  // timeout = failed to win
            else agent.EndEpisode();   // draw / timeout (neutral)
        }

        LogEpisodeSummary(winner);
        StartCoroutine(RestartNextFrame());
    }

    IEnumerator RestartNextFrame()
    {
        yield return null;
        BeginEpisode();
    }

    // ======================================================================
    // Event handlers — SET FLAGS ONLY, never call EndEpisode/RequestDecision
    // ======================================================================

    void HandleUnitTurnStarted(HexUnit unit)
    {
        // Buffer the unit; LateUpdate will call RequestDecision.
        pendingDecisionUnit = unit;
    }

    /// <summary>
    /// Buffer the episode end. LateUpdate will call EndEpisodeForAll once
    /// the current OnActionReceived call stack has fully returned.
    /// </summary>
    void HandleGameWon(Team winner)
    {
        if (pendingEpisodeEnd) return;   // already flagged (e.g. max rounds same frame)
        Debug.Log($"[HexMLEnvironment] Game won by Team {winner.TeamIndex} — " +
                  $"ending episode next LateUpdate.");
        pendingEpisodeEnd = true;
        pendingEpisodeWinner = winner;
    }

    // Combat event handlers — these are safe to call immediately since they
    // only add rewards, never call EndEpisode or RequestDecision.
    void HandleUnitDealtDamage(HexUnit attacker, int damage)
    {
        attacker.GetComponent<HexUnitAgent>()?.OnDealtDamage(damage);
        LogStep(attacker, $"DamageDealt:{damage}");
    }

    void HandleUnitKilled(HexUnit killer, HexUnit victim)
    {
        killer.GetComponent<HexUnitAgent>()?.OnKilledEnemy();
        victim.GetComponent<HexUnitAgent>()?.OnDied();
        LogStep(killer, $"KilledUnit:{victim.name}");
    }

    void HandleUnitDied(HexUnit victim)
    {
        victim.GetComponent<HexUnitAgent>()?.OnDied();
        LogStep(victim, "Died");
    }

    void HandleHomeDamageDealt(HexUnit attacker, Team targetTeam, int damage)
    {
        attacker.GetComponent<HexUnitAgent>()?.OnDealtHomeDamage(damage);
        LogStep(attacker, $"HomeDmg:{damage}→Team{targetTeam.TeamIndex}");
    }

    void HandleHomeLevelReduced(HexUnit attacker, Team targetTeam)
    {
        attacker.GetComponent<HexUnitAgent>()?.OnReducedHomeLevel();
        LogStep(attacker, $"ReducedHomeLevel→Team{targetTeam.TeamIndex}");
    }

    void HandleTeamEliminated(HexUnit eliminator, Team eliminated)
    {
        eliminator?.GetComponent<HexUnitAgent>()?.OnEliminatedTeam();
        LogStep(eliminator, $"EliminatedTeam{eliminated.TeamIndex}");
        // Note: OnGameWon fires separately from TurnManager when the last
        // team is eliminated, so we don't set pendingEpisodeEnd here.
    }

    // ======================================================================
    // CSV logging
    // ======================================================================

    void OpenCsv()
    {
        Directory.CreateDirectory(logFolder);
        string path = Path.Combine(logFolder,
            $"hex_ml_{System.DateTime.Now:yyyyMMdd_HHmmss}_{scenario}.csv");
        csvWriter = new StreamWriter(path, false, Encoding.UTF8);
        csvWriter.WriteLine(
            "episode,step,scenario,team,unit,action_index,action_name," +
            "hp,ap,wood_carry,stone_carry,wood_stored,stone_stored," +
            "home_level,home_hp,step_reward,event");
        Debug.Log($"[HexMLEnvironment] CSV log: {path}");
    }

    void CloseCsv()
    {
        csvWriter?.Flush();
        csvWriter?.Close();
        csvWriter = null;
    }

    void LogStep(HexUnit unit, string eventLabel = "")
    {
        if (!enableCsvLogging || csvWriter == null || unit == null) return;
        var agent = unit.GetComponent<HexUnitAgent>();
        Team team = unit.Team;
        if (team == null) return;

        stepCount++;
        csvWriter.WriteLine(
            $"{episodeCount},{stepCount},{scenario}," +
            $"{team.TeamIndex},{unit.name}," +
            $"{(agent != null ? agent.LastActionIndex : -1)}," +
            $"{(agent != null ? agent.LastActionName : "none")}," +
            $"{unit.CurrentHP},{unit.ActionPointsRemaining}," +
            $"{unit.WoodCarried},{unit.StoneCarried}," +
            $"{team.WoodStored},{team.StoneStored}," +
            $"{team.HomeLevel},{team.HomeHP}," +
            $"{(agent != null ? agent.LastStepReward.ToString("F4") : "0")}," +
            $"{eventLabel}");

        if (stepCount % 100 == 0) csvWriter.Flush();
    }

    void LogEpisodeSummary(Team winner)
    {
        if (!enableCsvLogging || csvWriter == null) return;
        string winStr = winner != null ? $"Team{winner.TeamIndex}" : "Draw";
        csvWriter.WriteLine(
            $"{episodeCount},EPISODE_END,{scenario},,," +
            $"-1,EpisodeEnd,,,,,,,,,0," +
            $"Winner:{winStr} Rounds:{turnManager.RoundNumber}");
        csvWriter.Flush();
    }
}