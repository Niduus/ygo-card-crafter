# Block reference

Every block you can place on the event sheet. Words in `{braces}` are blanks you fill in on the sheet.
Blocks that pick cards open a card picker: whose cards, which zones, card type, monster Type, Attribute, archetype,
Level, ATK, face-up, "other than this card", and "only cards this card points to" for Link monsters.

## WHEN: what starts the effect

Each effect has exactly one.

| Block | Reads as | Options |
|---|---|---|
| You activate it (Main Phase)<br><sub>An Ignition effect: the player activates it during their own Main Phase.</sub> | You activate it during your Main Phase, {zone} | **From**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished |
| Activate this Spell/Trap<br><sub>The effect a Spell or Trap card has when it is activated.</sub> | Activate this card {timing} | **Timing**: at any time, when an opponent's monster declares an attack, when your opponent Normal or Special Summons a monster, when any monster is Summoned, when your opponent activates a card or effect, when your opponent activates a monster effect, when your opponent activates a Spell/Trap Card |
| Quick Effect<br><sub>Can be activated during either player's turn, like a Quick-Play Spell.</sub> | (Quick Effect) You activate it {timing}, {zone} | **Timing**: at any time, when an opponent's monster declares an attack, when your opponent Normal or Special Summons a monster, when any monster is Summoned, when your opponent activates a card or effect, when your opponent activates a monster effect, when your opponent activates a Spell/Trap Card; **From**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished |
| This card is Summoned | If this card is {how} Summoned | **How**: Normal, Special, Normal or Special, Flip, Normal, Flip or Special; **optional ("You can")** on/off |
| This card is sent to the GY | If this card is sent to the GY{from} | **From**: from anywhere, from the field, from the hand, from the Deck, from the hand or field; **optional ("You can")** on/off |
| This card is destroyed | If this card is destroyed{by} | **By**: by anything, by battle, by a card effect, by an opponent's card; **optional ("You can")** on/off |
| This card destroys a monster by battle | If this card destroys an opponent's monster by battle | **optional ("You can")** on/off |
| FLIP (this card is flipped face-up)<br><sub>A FLIP effect. Tick the Flip ability on the Card tab too.</sub> | FLIP |  |
| During a phase | During {whose} {phase}, {zone} | **Whose**: your, your opponent's, each; **Phase**: Standby Phase, End Phase; **From**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished; **optional ("You can")** on/off |
| This card is used as material<br><sub>Fusion, Synchro and Link Materials go to the GY, so they can activate. Xyz Materials go under the Xyz monster and can't.</sub> | If this card is used as material for a {kind} Summon | **Summon**: Fusion, Synchro or Link, Fusion, Synchro, Link; **optional ("You can")** on/off |
| While this card is face-up (lasting effect)<br><sub>A lasting effect that is always on and never activates, like an ATK boost or protection.</sub> | While this card is face-up {zone} | **Where**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished |

## IF: what must be true

All of them must be true for the effect to be used.

| Block | Reads as | Options |
|---|---|---|
| There are cards somewhere<br><sub>For example: you control 2 or more Dragon monsters, or there are 3 or more Spells in your GY.</sub> | There are {count} or more {cards} | **How many**: 1–60; **Which cards**: card picker |
| There are no matching cards | There are no {cards} | **Which cards**: card picker |
| This card was Summoned this turn<br><sub>Pair it with a Quick Effect for "If this card was Summoned this turn (Quick Effect): ...".</sub> | This card was {how} Summoned this turn | **How**: Normal, Flip or Special, Normal, Special, Flip |
| This card is linked<br><sub>Linked: a Link Arrow points to it or it points to a monster. Co-linked: arrows point both ways.</sub> | This card {state} | **State**: is linked, is co-linked, is not linked |
| This card points to monsters<br><sub>Counts the monsters in the zones this Link monster's arrows point to.</sub> | This card points to {count} or more {cards} | **How many**: 1–8; **Which monsters**: card picker |
| This card has Xyz Materials | This card has {count} or more materials | **How many**: 1–10 |
| Your opponent controls more monsters | Your opponent controls more monsters than you do |  |
| Life Points compared to a number | {who} LP is {cmp} {amount} | **Whose**: Your, Your opponent's; **Compare**: at most, at least; **Amount**: 0–999999 |
| Whose turn it is | It is {whose} turn | **Whose**: your, your opponent's |
| It is a certain phase | It is the {phase} | **Phase**: Main Phase, Battle Phase, Standby Phase, End Phase |

## COST: paid when activating

Paid before the effect resolves. Lasting effects have no cost.

| Block | Reads as | Options |
|---|---|---|
| Discard cards | Discard {count} {cards} | **How many**: 1–10; **Which cards**: card picker |
| Pay Life Points | Pay {amount} LP | **Amount**: 1–99999 |
| Tribute, banish, send or discard this card | {how} this card | **How**: Tribute, Banish, Send to the GY, Discard |
| Tribute monsters | Tribute {count} {cards} | **How many**: 1–5; **Which monsters**: card picker |
| Banish or send cards to the GY | {how} {count} {cards} | **How**: Banish, Send to the GY; **How many**: 1–10; **Which cards**: card picker |
| Detach Xyz Materials | Detach {count} material(s) from this card | **How many**: 1–10 |

## DO: what happens

Run in order. Each action after the first says how it links to the one before: *and*, *and if you do*, *then*, or *also, after that*.

| Block | Reads as | Options |
|---|---|---|
| Draw cards | {who} draw(s) {count} card(s) | **Who**: You, Your opponent; **How many**: 1–10 |
| Add cards to your hand (search) | Add {count} {cards} to your hand {target} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off |
| Special Summon monsters | Special Summon {count} {cards} {target} {position} {zone} | **How many**: 1–5; **Which cards**: card picker; **(targeting)** on/off; **Position**: face-up, in Attack Position, in Defense Position; **Zone**: to any zone, to a zone this card points to |
| Special Summon this card | Special Summon this card {position} | **Position**: face-up, in Attack Position, in Defense Position |
| Destroy cards | Destroy {count} {cards} {target} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off |
| Banish cards | Banish {count} {cards} {target} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off |
| Send cards to the GY | Send {count} {cards} to the GY {target} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off |
| Return cards to the hand (bounce) | Return {count} {cards} to the hand {target} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off |
| Shuffle cards into the Deck | Shuffle {count} {cards} into the Deck {target} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off |
| Inflict damage | Inflict {amount} damage to {who} | **Amount**: 1–99999; **Who**: your opponent, you |
| Gain Life Points | {who} gain(s) {amount} LP | **Who**: You, Your opponent; **Amount**: 1–99999 |
| This card gains or loses ATK/DEF | This card {dir} {amount} {stat} {duration} | **Gains/loses**: gains, loses; **Amount**: 1–99999; **Stat**: ATK, DEF, ATK/DEF; **Until**: until the end of this turn, while face-up |
| Negate the activation or effect<br><sub>Use with a WHEN timing like "when your opponent activates a card or effect", like Ash Blossom or Solemn Judgment.</sub> | Negate {what} {and} | **What**: the activation, the effect; **And**: and nothing else, and if you do, destroy that card, and if you do, banish that card |
| Negate the effects of face-up cards<br><sub>Like Effect Veiler or Infinite Impermanence: the chosen face-up cards lose their effects.</sub> | Negate the effects of {count} {cards} {target} {duration} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off; **Until**: until the end of this turn, while face-up |
| Monsters gain or lose ATK/DEF | {count} {cards} {target} {dir} {amount} {stat} {duration} | **How many**: 1–10; **Which monsters**: card picker; **(targeting)** on/off; **Gain/lose**: gain(s), lose(s); **Amount**: 1–99999; **Stat**: ATK, DEF, ATK/DEF; **Until**: until the end of this turn, while face-up |
| Set Spells/Traps | Set {count} {cards} {target} | **How many**: 1–10; **Which cards**: card picker; **(targeting)** on/off |
| Send the top cards of a Deck to the GY | Send the top {count} card(s) of {whose} Deck to the GY | **How many**: 1–20; **Whose**: your, your opponent's |
| Destroy, banish or return this card | {how} this card | **How**: Return to the hand, Destroy, Banish, Send to the GY, Shuffle into the Deck |

## ALWAYS: lasting effects

Used with the "While this card is face-up" trigger. They apply all the time instead of activating.

| Block | Reads as | Options |
|---|---|---|
| Monsters gain or lose ATK/DEF<br><sub>For example: Dragon monsters you control gain 500 ATK.</sub> | {cards} {dir} {amount} {stat} | **Which monsters**: card picker; **Gain/lose**: gain, lose; **Amount**: 1–99999; **Stat**: ATK, DEF, ATK/DEF |
| This card gains or loses ATK/DEF | This card {dir} {amount} {stat} | **Gains/loses**: gains, loses; **Amount**: 1–99999; **Stat**: ATK, DEF, ATK/DEF |
| This card can't be destroyed | This card cannot be destroyed {by} | **By**: by battle, by card effects, by battle or card effects, by your opponent's card effects |
| Your opponent can't target this card | Your opponent cannot target this card with card effects |  |
| Battle ability (direct attack, piercing, extra attack) | This card {ability} | **Ability**: can attack directly, inflicts piercing battle damage, can make a second attack during each Battle Phase |
| Monsters can't be destroyed<br><sub>For example: monsters this card points to cannot be destroyed by battle.</sub> | {cards} cannot be destroyed {by} | **Which monsters**: card picker; **By**: by battle, by card effects, by battle or card effects |

## Templates

Ready-made effects modelled on well-known cards. They are inserted as normal blocks you can change.

| Template | Like | For | What it does |
|---|---|---|---|
| Draw 2 | Pot of Greed | Spell | Activate: draw 2 cards. |
| Revive a monster | Monster Reborn | Spell | Target 1 monster in either GY; Special Summon it. |
| Destroy a Spell/Trap | Mystical Space Typhoon | Spell | Target 1 Spell/Trap on the field; destroy it. Best on a Quick-Play Spell. |
| Burn 800 | Ookazi | Spell | Inflict 800 damage to your opponent. |
| Recycle and draw | Pot of Avarice | Spell | Shuffle 5 monsters from your GY into the Deck, then draw 2 cards. |
| Type boost field | Mountain | Spell | A Field Spell: monsters of one Type on the field gain 200 ATK. |
| Destroy an attacker | Mirror Force (single) | Trap | When an opponent's monster declares an attack: destroy 1 monster your opponent controls. |
| Banish on summon | Bottomless Trap Hole | Trap | When your opponent summons a monster: banish 1 monster they control. |
| Search on summon | Elemental HERO Stratos | Monster | If this card is Normal or Special Summoned: add 1 monster from your Deck to your hand. |
| Floater | Sangan / floaters | Monster | If this card is destroyed: Special Summon 1 Level 4 or lower monster from your Deck. |
| Free summon from hand | Cyber Dragon | Monster | If your opponent controls more monsters: Special Summon this card from your hand. |
| Revive itself from GY | GY self-revivers | Monster | Banish 1 other card from your GY; Special Summon this card from your GY. |
| Discard to search | hand-starter monsters | Monster | Discard this card; add 1 Spell from your Deck to your hand. |
| Quick banish from GY | Crossout-style disruption | Monster | (Quick Effect) Target 1 card in your opponent's GY; banish it. |
| Battle protection | Marshmallon | Monster | This card cannot be destroyed by battle. |
| Negate an activation | Ash Blossom & Joyous Spring | Monster | When your opponent activates a card or effect: discard this card; negate the activation, and if you do, destroy that card. |
| Negate a monster's effects | Effect Veiler | Monster | (Quick Effect) Discard this card, then target 1 face-up monster your opponent controls; negate its effects until the end of this turn. |
| Link support | Link Arrow boosters | Monster | A Link monster: monsters this card points to gain 500 ATK and can't be destroyed by battle. |
| Detach to destroy | Rank 4 Xyz removal | Monster | An Xyz monster: detach 1 material, then target 1 card your opponent controls; destroy it. |
| Standby heal | Solemn Wishes | Spell | During your Standby Phase: gain 500 LP. Put it on a Continuous Spell. |
