using System;
using UnityEngine;

/// <summary>
/// Static event bus for game events that ML-Agents needs to react to.
/// TurnManager fires these; HexMLEnvironment and HexUnitAgent subscribe.
/// Keeping events here avoids a hard compile dependency on ML-Agents
/// inside TurnManager — the game runs fine with or without the ML package.
/// </summary>
public static class TurnManagerEvents
{
    // Fired when a unit starts its turn (triggers agent RequestDecision).
    public static Action<HexUnit> OnUnitTurnStarted;

    // Combat.
    public static Action<HexUnit, int> OnUnitDealtDamage;   // attacker, damage
    public static Action<HexUnit, HexUnit> OnUnitKilled;        // killer, victim
    public static Action<HexUnit> OnUnitDied;          // victim

    // Home.
    public static Action<HexUnit, Team, int> OnHomeDamageDealt;   // attacker, target team, damage
    public static Action<HexUnit, Team> OnHomeLevelReduced;  // attacker, target team
    public static Action<HexUnit, Team> OnTeamEliminated;    // eliminator (may be null), eliminated team

    // Game end.
    public static Action<Team> OnGameWon;           // winning team
}