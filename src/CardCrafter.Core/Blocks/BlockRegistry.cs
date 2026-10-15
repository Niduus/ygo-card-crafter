namespace CardCrafter.Core.Blocks;

/// <summary>Every block the event sheet offers, looked up by id.</summary>
public static class BlockRegistry
{
    public static IReadOnlyList<BlockDefinition> All { get; } =
    [
        // WHEN
        new IgnitionTrigger(),
        new ActivateTrigger(),
        new QuickTrigger(),
        new SummonedTrigger(),
        new SentToGraveTrigger(),
        new DestroyedTrigger(),
        new DestroysByBattleTrigger(),
        new FlipTrigger(),
        new PhaseTrigger(),
        new WhileFaceUpTrigger(),
        // IF
        new CardsExistCondition(),
        new NoCardsCondition(),
        new SummonedThisTurnCondition(),
        new OpponentMoreMonstersCondition(),
        new LifePointsCondition(),
        new TurnCondition(),
        new PhaseCondition(),
        // COST
        new DiscardCost(),
        new PayLifePointsCost(),
        new SelfCost(),
        new TributeCost(),
        new MoveCardsCost(),
        // DO
        new DrawAction(),
        new AddToHandAction(),
        new SpecialSummonAction(),
        new SpecialSummonSelfAction(),
        new DestroyAction(),
        new BanishAction(),
        new SendToGraveAction(),
        new ReturnToHandAction(),
        new ShuffleIntoDeckAction(),
        new DamageAction(),
        new GainLifePointsAction(),
        new ChangeSelfStatsAction(),
        // DO (lasting)
        new BoostCardsPassive(),
        new SelfStatsPassive(),
        new IndestructiblePassive(),
        new UntargetablePassive(),
        new BattleAbilityPassive(),
    ];

    private static readonly Dictionary<string, BlockDefinition> ById = All.ToDictionary(b => b.Id);

    public static BlockDefinition? Find(string id) => ById.GetValueOrDefault(id);

    public static IEnumerable<BlockDefinition> OfKind(BlockKind kind) => All.Where(b => b.Kind == kind);
}
