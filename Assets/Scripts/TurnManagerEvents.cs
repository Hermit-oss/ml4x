using System;
using UnityEngine;

/// <summary>
/// Static event bus for game events that ML-Agents needs to react to.
/// TurnManager fires these; HexMLEnvironment and HexUnitAgent subscribe.
/// Keeping events here avoids a hard compile dependency on ML-Agents
/// inside TurnManager the game runs fine with or without the ML package.
/// </summary>
public static class TurnManagerEvents
{
    public static Action<HexUnit> OnUnitTurnStarted;

    public static Action<HexUnit, int> OnUnitDealtDamage;
    public static Action<HexUnit, HexUnit> OnUnitKilled;
    public static Action<HexUnit> OnUnitDied;

    public static Action<HexUnit, Team, int> OnHomeDamageDealt;
    public static Action<HexUnit, Team> OnHomeLevelReduced;
    public static Action<HexUnit, Team> OnTeamEliminated;

    public static Action<Team> OnGameWon;
}
