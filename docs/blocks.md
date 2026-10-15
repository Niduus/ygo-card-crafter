# Block reference

Every block you can place on the event sheet. Words in `{braces}` are blanks you fill in on the sheet.
Blocks that pick cards open a card picker: whose cards, which zones, card type, monster Type, Attribute, archetype,
Level, ATK, face-up, "other than this card", and "only cards this card points to" for Link monsters.

## WHEN: what starts the effect

Each effect has exactly one.

| Block | Reads as | Options |
|---|---|---|
| You activate it (Main Phase)<br><sub>An Ignition effect: the player activates it during their own Main Phase.</sub> | You activate it during your Main Phase, {zone} | **From**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished, in the hand or GY |
| Special Summon procedure<br><sub>A built-in way to Special Summon this card, like Cyber Dragon or Chaos Sorcerer. It doesn't start a chain, so it can't be responded to or negated. Add IF blocks for when it's allowed and COST blocks for what you pay. It has no DO blocks: the Summon is what it does.</sub> | You can Special Summon this card {zone} (Summon procedure) | **From**: from your hand, from your GY, from your banishment |
| Activate this Spell/Trap<br><sub>The effect a Spell or Trap card has when it is activated.</sub> | Activate this card {timing} | **Timing**: at any time, when an opponent's monster declares an attack, when an opponent's monster declares a direct attack, when a monster declares an attack, when your opponent Normal or Special Summons a monster(s), when your opponent Normal Summons a monster, when your opponent Special Summons a monster(s), when your opponent Flip Summons a monster, when you Normal or Special Summon a monster(s), when any monster is Summoned, when your opponent activates a card or effect, when your opponent activates a monster effect, when your opponent activates a Spell/Trap Card, when your opponent activates a Spell Card, when your opponent activates a Trap Card, when a card or effect is activated, when a monster effect is activated, when a Spell/Trap Card is activated, when a Spell Card is activated, when a Trap Card is activated, when your opponent would Normal or Special Summon a monster(s), when your opponent would Special Summon a monster(s), during damage calculation, if your monster battles, during the Battle Phase, during the End Phase; **can be activated in the Damage Step** on/off |
| Quick Effect<br><sub>Can be activated during either player's turn, like a Quick-Play Spell.</sub> | (Quick Effect) You activate it {timing}, {zone} | **Timing**: at any time, when an opponent's monster declares an attack, when an opponent's monster declares a direct attack, when a monster declares an attack, when your opponent Normal or Special Summons a monster(s), when your opponent Normal Summons a monster, when your opponent Special Summons a monster(s), when your opponent Flip Summons a monster, when you Normal or Special Summon a monster(s), when any monster is Summoned, when your opponent activates a card or effect, when your opponent activates a monster effect, when your opponent activates a Spell/Trap Card, when your opponent activates a Spell Card, when your opponent activates a Trap Card, when a card or effect is activated, when a monster effect is activated, when a Spell/Trap Card is activated, when a Spell Card is activated, when a Trap Card is activated, when your opponent would Normal or Special Summon a monster(s), when your opponent would Special Summon a monster(s), during damage calculation, if your monster battles, during the Battle Phase, during the End Phase; **From**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished, in the hand or GY; **can be used in the Damage Step** on/off |
| This card is Summoned | If this card is {how} Summoned | **How**: Normal, Special, Normal or Special, Flip, Normal, Flip or Special, Link, Synchro, Xyz, Fusion, Ritual, Pendulum; **optional ("You can")** on/off |
| This card is sent to the GY | If this card is sent to the GY{from} | **From**: from anywhere, from the field, from the hand, from the Deck, from the hand or field; **optional ("You can")** on/off |
| This card is destroyed | If this card is destroyed{by} | **By**: by anything, by battle, by battle and sent to the GY, by a card effect, by an opponent's card; **optional ("You can")** on/off |
| This card destroys a monster by battle | If this card destroys an opponent's monster by battle{sent} | **And**: (that's all), and sends it to the GY; **optional ("You can")** on/off |
| Something happens to this card | {event} | **What happens**: If this card is banished, If this card leaves the field, If this card is Tributed, If this card is returned from the field to the hand, If this card is added to your hand, except by drawing it, If this card is discarded, If this card is flipped face-up, If this card is targeted by a card effect, If this card inflicts battle damage to your opponent, If this card inflicts battle damage to your opponent by a direct attack, When this card declares an attack, When this card is targeted for an attack, At the start of the Damage Step, if this card battles, After damage calculation, if this card battled, At the end of the Damage Step, if this card battled; **optional ("You can")** on/off |
| FLIP (this card is flipped face-up)<br><sub>A FLIP effect. Tick the Flip ability on the Card tab too.</sub> | FLIP | **optional ("You can")** on/off |
| Something happens in the duel (trigger)<br><sub>A trigger effect: it activates after the event, like "If your opponent Special Summons a monster(s)". For an effect used in response, during either player's turn, use "Quick Effect" instead.</sub> | If {event}, {zone} | **What happens**: your opponent Normal or Special Summons a monster(s), your opponent Normal Summons a monster, your opponent Special Summons a monster(s), you Normal or Special Summon a monster(s), you Special Summon a monster(s), a monster(s) is Normal or Special Summoned, a monster(s) you control is destroyed, a monster(s) your opponent controls is destroyed, a monster(s) you control is destroyed by battle, a card(s) is sent to your opponent's GY, a card(s) is sent to your GY, your opponent adds a card(s) from their Deck to their hand, your opponent draws a card(s), you take damage, you take battle damage, your opponent takes battle damage, an opponent's monster declares an attack, an opponent's monster declares a direct attack, a monster you control is targeted for an attack, a monster(s) you control leaves the field, a card(s) is banished, your opponent activates a card or effect, your opponent activates a monster effect, you gain LP; **This card is**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished, in the hand or GY; **optional ("You can")** on/off |
| Matching cards are Summoned, destroyed or sent to the GY<br><sub>For example: "If a Dragon monster(s) is Special Summoned to your field" or "If a "Blue-Eyes" monster(s) you control is destroyed".</sub> | If {cards} is {event}, {zone} | **Which cards**: card picker; **What happens to them**: Normal or Special Summoned, Normal Summoned, Special Summoned, Tribute Summoned, Ritual Summoned, Fusion Summoned, Synchro Summoned, Xyz Summoned, Pendulum Summoned, Link Summoned, destroyed, destroyed by battle, destroyed by battle or card effect, sent to the GY; **This card is**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished, in the hand or GY; **optional ("You can")** on/off |
| During a phase | During {whose} {phase}, {zone} | **Whose**: your, your opponent's, each; **Phase**: Standby Phase, End Phase, start of the Battle Phase, end of the Battle Phase, Draw Phase; **From**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished, in the hand or GY; **optional ("You can")** on/off |
| This card is used as material<br><sub>Fusion, Synchro and Link Materials go to the GY, so they can activate. Xyz Materials go under the Xyz monster and can't.</sub> | If this card is used as material for a {kind} Summon | **Summon**: Fusion, Synchro or Link, Fusion, Synchro, Link; **only if sent to the GY** on/off; **optional ("You can")** on/off |
| While this card is face-up (lasting effect)<br><sub>A lasting effect that is always on and never activates, like an ATK boost or protection.</sub> | While this card is face-up {zone} | **Where**: on the field, in the Monster Zone, in the hand, in the GY, in the Spell & Trap Zone, in the Field Zone, in the Pendulum Zone, while banished, in the hand or GY |

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
| It is a certain phase | It is the {phase} | **Phase**: Main Phase, Main Phase 1, Battle Phase, Standby Phase, End Phase |
| This card is in a position | This card is in {pos} | **Position**: Attack Position, Defense Position, face-up Defense Position |
| Number of cards in a hand | {who} {cmp} {count} cards in hand | **Who**: you have, your opponent has; **Compare**: or fewer, or more, exactly; **How many**: 0–20 |
| How this card was Summoned<br><sub>For example, an effect that only works if this card was Link Summoned, or Special Summoned from the hand.</sub> | This card was {how} | **How**: Link Summoned, Synchro Summoned, Xyz Summoned, Fusion Summoned, Ritual Summoned, Pendulum Summoned, Normal Summoned, Tribute Summoned, Special Summoned from the hand, Special Summoned from the GY, Special Summoned from the Extra Deck |
| This card wasn't sent to the GY this turn<br><sub>For GY effects that can't be used the turn the card got there.</sub> | This card was not sent to the GY this turn |  |
| The Battle Phase is still ahead<br><sub>Only activates before the Battle Phase, when there will be one. Card text doesn't say this, so it adds no words.</sub> | You can still enter the Battle Phase this turn |  |
| Your LP compared to your opponent's | You have {cmp} LP than your opponent | **Compare**: less, more |

## COST: paid when activating

Paid before the effect resolves. Lasting effects have no cost.

| Block | Reads as | Options |
|---|---|---|
| Discard cards | Discard {count} {cards} | **How many**: 1–10; **Which cards**: card picker |
| Pay Life Points | Pay {amount} LP | **Amount**: 1–99999 |
| Tribute, banish, send or discard this card | {how} this card | **How**: Tribute, Banish, Send to the GY, Discard |
| Tribute monsters | Tribute {count} {cards} | **How many**: 1–5; **Which monsters**: card picker |
| Banish, send or return cards (cost) | {how} {count} {cards} | **How**: Banish, Send to the GY:, Shuffle into the Deck:, Place on the bottom of the Deck:, Return to the hand:, Return to the Extra Deck:; **How many**: 1–10; **Exactly / up to**: exactly, up to; **Which cards**: card picker |
| Detach Xyz Materials | Detach {count} material(s) from this card | **How many**: 1–10 |
| You can't Special Summon this turn, except... (on activation)<br><sub>Starts when the effect is activated, so it still applies if the effect is negated. The card text puts it after the effect.</sub> | You cannot Special Summon monsters {from} the turn you activate this, except {cards} | **From**: from anywhere, from the Extra Deck; **Except**: card picker |

## DO: what happens

Run in order. Each action after the first says how it links to the one before: *and*, *and if you do*, *then*, or *also, after that*.

| Block | Reads as | Options |
|---|---|---|
| Draw cards | {who} draw(s) {count} card(s) | **Who**: You, Your opponent; **How many**: 1–10; **For each**: (once), for each card the action before affected, for each card used for the cost |
| Add cards to your hand (search) | Add {count} {cards} to your hand {target} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Special Summon monsters | Special Summon {count} {cards} {target} {position} {zone} | **How many**: 1–5; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off; **Position**: face-up, in Attack Position, in Defense Position; **Zone**: to any zone, to a zone this card points to |
| Special Summon this card<br><sub>Summons this card from wherever the effect is used: the WHEN block's "in the hand" or "in the GY".</sub> | Special Summon this card {position} | **Position**: face-up, in Attack Position, in Defense Position, in face-down Defense Position |
| Destroy cards | Destroy {count} {cards} {target} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Banish cards | Banish {count} {cards} {target} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Send cards to the GY | Send {count} {cards} to the GY {target} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Return cards to the hand (bounce) | Return {count} {cards} to the hand {target} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Shuffle cards into the Deck | Shuffle {count} {cards} into the Deck {target} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Inflict damage | Inflict {amount} damage to {who} | **Amount**: 1–99999; **Who**: your opponent, you; **For each**: (once), for each card the action before affected, for each card used for the cost |
| Gain Life Points | {who} gain(s) {amount} LP | **Who**: You, Your opponent; **Amount**: 1–99999; **For each**: (once), for each card the action before affected, for each card used for the cost |
| This card gains or loses ATK/DEF | This card {dir} {amount} {stat} {duration} | **Gains/loses**: gains, loses; **Amount**: 1–99999; **Stat**: ATK, DEF, ATK/DEF; **Until**: until the end of this turn, while face-up; **For each**: (once), for each card the action before affected, for each card used for the cost |
| Negate the activation or effect<br><sub>Use with a WHEN timing like "when your opponent activates a card or effect", like Ash Blossom or Solemn Judgment.</sub> | Negate {what} {and} | **What**: the activation, the effect; **And**: and nothing else, and if you do, destroy that card, and if you do, banish that card |
| Negate the effects of face-up cards<br><sub>Like Effect Veiler or Infinite Impermanence: the chosen face-up cards lose their effects.</sub> | Negate the effects of {count} {cards} {target} {duration} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off; **Until**: until the end of this turn, while face-up |
| Monsters gain or lose ATK/DEF | {count} {cards} {target} {dir} {amount} {stat} {duration} | **How many**: 1–10; **Which monsters**: card picker; **(targeting)** on/off; **Gain/lose**: gain(s), lose(s); **Amount**: 1–99999; **Stat**: ATK, DEF, ATK/DEF; **Until**: until the end of this turn, while face-up |
| Set Spells/Traps | Set {count} {cards} {target} | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Send the top cards of a Deck to the GY | Send the top {count} card(s) of {whose} Deck to the GY | **How many**: 1–20; **Whose**: your, your opponent's |
| Destroy, banish or return this card | {how} this card | **How**: Return to the hand, Destroy, Banish, Send to the GY, Shuffle into the Deck |
| Negate the Summon<br><sub>Like Solemn Strike: use with the WHEN timing "when your opponent would Normal or Special Summon a monster(s)".</sub> | Negate the Summon {and} | **And**: and if you do, destroy that monster(s), and if you do, banish that monster(s), and nothing else |
| Negate the attack<br><sub>Use with a WHEN that happens during an attack, such as "when an opponent's monster declares an attack".</sub> | Negate the attack {then} | **Then**: and nothing else, , then end the Battle Phase |
| Discard cards (as an effect) | {who} discard(s) {count} card(s) {how} | **Who**: Your opponent, You, Each player; **How many**: 1–10; **How**: of their choice, at random |
| Change battle positions | Change {count} {cards} {target} {to} | **How many**: 1–10; **Which monsters**: card picker; **(targeting)** on/off; **To**: to face-up Defense Position, to face-up Attack Position, to face-down Defense Position, to the other battle position |
| Take control of monsters | Take control of {count} {cards} {target} {duration} | **How many**: 1–5; **Which monsters**: card picker; **(targeting)** on/off; **Until**: until the End Phase, permanently |
| Attach cards to this card as material<br><sub>Xyz Monsters only: the cards go under this card as Xyz Materials.</sub> | Attach {count} {cards} {target} to this card as material | **How many**: 1–10; **Exactly / up to / all**: exactly, up to, all; **Which cards**: card picker; **(targeting)** on/off |
| Normal Summon a monster<br><sub>An extra Normal Summon right now, like Ultimate Offering. It doesn't use up the turn's Normal Summon.</sub> | Normal Summon {count} {cards} | **How many**: 1–1; **Which monster**: card picker |
| Special Summon Tokens<br><sub>Make the Token as its own card in this pack (Monster, frame "Token") and enter its passcode here; its Level, Type, Attribute, ATK and DEF come from that card.</sub> | Special Summon {count} Token(s) (passcode {token}) {position} {side} | **How many**: 1–5; **Token passcode**: 0–2147483647; **Position**: face-up, in Attack Position, in Defense Position; **Whose field**: to your field, to your opponent's field |
| Monsters' ATK becomes a value (halve, 0...) | {count} {cards} {target}: ATK becomes {mode} {amount} {duration} | **How many**: 1–10; **Which monsters**: card picker; **(targeting)** on/off; **Becomes**: half its current ATK, 0, a number:; **Number (for "a number")**: 0–99999; **Until**: until the end of this turn, while face-up |
| Change monsters' Levels | {count} {cards} {target}: Level {mode} {amount} {duration} | **How many**: 1–10; **Which monsters**: card picker; **(targeting)** on/off; **Level**: becomes, increases by, decreases by; **Amount**: 1–12; **Until**: until the end of this turn, while face-up |
| This card's Level changes | This card's Level {mode} {amount} {duration} | **Level**: becomes, increases by, decreases by; **Amount**: 1–12; **Until**: until the end of this turn, while face-up |
| Monsters can't attack / can attack directly / attack twice | {count} {cards} {target} {rule} this turn | **How many**: 1–10; **Which monsters**: card picker; **(targeting)** on/off; **What**: cannot attack, can attack directly, can make a second attack during each Battle Phase, inflicts piercing battle damage, cannot be targeted by your opponent's card effects |
| This card can attack directly / attack twice / pierce this turn | This card {rule} this turn | **What**: can attack directly, can make a second attack during each Battle Phase, can attack all monsters your opponent controls once each, inflicts piercing battle damage, cannot be destroyed by battle, is unaffected by your opponent's card effects |
| You can't Special Summon this turn, except...<br><sub>The common modern restriction, e.g. "you cannot Special Summon monsters from the Extra Deck, except Link Monsters". It starts when the effect resolves and lasts until the end of the turn.</sub> | For the rest of this turn, you cannot Special Summon monsters {from}, except {cards} | **From**: from anywhere, from the Extra Deck; **Except**: card picker |
| You can Normal Summon an extra monster this turn | This turn, you can Normal Summon 1 {cards} in addition to your Normal Summon/Set | **Which monster**: card picker |

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
| This card is unaffected by effects | This card is unaffected by {whose} | **By**: your opponent's card effects, other cards' effects, Spell/Trap effects, other monsters' effects, your opponent's monster effects |
| This card can't attack / be attacked / be Tributed... | {rule} | **Rule**: This card cannot attack, This card cannot attack directly, Your opponent cannot target this card for attacks, You take no battle damage from battles involving this card, This card cannot be Tributed, This card cannot be used as material for a Fusion, Synchro, Xyz or Link Summon, This card's battle position cannot be changed, This card can attack all monsters your opponent controls once each, This card must attack, if able |
| Monsters can't attack / are negated / can't be targeted... | {cards} {rule} | **Which monsters**: card picker; **Rule**: cannot attack, have their effects negated, cannot be targeted by your opponent's card effects, cannot be targeted for attacks, are unaffected by your opponent's card effects, inflict piercing battle damage |
| You take no damage / your opponent can't Special Summon... | {rule} | **Rule**: You take no battle damage, You take no effect damage, Your opponent cannot Special Summon monsters from the Extra Deck, Your opponent cannot Special Summon monsters, Your opponent cannot add cards from their Deck to their hand except by drawing them, Your opponent cannot activate cards or effects during the Battle Phase |
| The equipped monster gains ATK / is protected...<br><sub>For Equip Spells (Card tab: Spell kind "Equip"). Card Crafter writes the activation that equips the card.</sub> | The equipped monster {rule} {amount} | **Rule**: gains ATK, loses ATK, gains ATK/DEF, cannot be destroyed by battle, cannot be destroyed by card effects, cannot attack, has its effects negated, inflicts piercing battle damage, cannot be targeted by your opponent's card effects; **ATK amount**: 0–99999 |
| This card gains ATK/DEF for each card | This card {dir} {amount} {stat} for each {cards} | **Gains/loses**: gains, loses; **Amount**: 1–99999; **Stat**: ATK, DEF, ATK/DEF; **For each**: card picker |

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
| Free summon from hand | Cyber Dragon | Monster | If your opponent controls more monsters, you can Special Summon this card from your hand. It doesn't start a chain. |
| Banish from GY to summon | Chaos Sorcerer | Monster | Special Summon this card from your hand by banishing 2 monsters from your GY. Set the card to "Can't be Normal Summoned". |
| Tribute to summon | Gate Guardian | Monster | Special Summon this card from your hand by Tributing 1 monster, once per turn. |
| Revive itself from GY | GY self-revivers | Monster | Banish 1 other card from your GY; Special Summon this card from your GY. |
| Discard to search | hand-starter monsters | Monster | Discard this card; add 1 Spell from your Deck to your hand. |
| Quick banish from GY | Crossout-style disruption | Monster | (Quick Effect) Target 1 card in your opponent's GY; banish it. |
| Battle protection | Marshmallon | Monster | This card cannot be destroyed by battle. |
| Negate an activation | Ash Blossom & Joyous Spring | Monster | When your opponent activates a card or effect: discard this card; negate the activation, and if you do, destroy that card. |
| Negate a monster's effects | Effect Veiler | Monster | (Quick Effect) Discard this card, then target 1 face-up monster your opponent controls; negate its effects until the end of this turn. |
| Link support | Link Arrow boosters | Monster | A Link monster: monsters this card points to gain 500 ATK and can't be destroyed by battle. |
| Detach to destroy | Rank 4 Xyz removal | Monster | An Xyz monster: detach 1 material, then target 1 card your opponent controls; destroy it. |
| Standby heal | Solemn Wishes | Spell | During your Standby Phase: gain 500 LP. Put it on a Continuous Spell. |
| Heal 1000 | Dian Keto the Cure Master | Spell | Gain 1000 LP. |
| Draw 1, opponent gains LP | Upstart Goblin | Spell | Draw 1 card, then your opponent gains 1000 LP. |
| Search a Warrior | Reinforcement of the Army | Spell | Add 1 Level 4 or lower Warrior monster from your Deck to your hand. |
| Search a Spell | Terraforming-style searchers | Spell | Add 1 Spell from your Deck to your hand. |
| Send to GY from Deck | Foolish Burial | Spell | Send 1 monster from your Deck to the GY. |
| Weaken a monster | Forbidden Lance (ATK part) | Spell | Target 1 face-up monster your opponent controls; it loses 800 ATK until the end of this turn. Best on a Quick-Play Spell. |
| Destroy an opponent's card | single-target removal | Spell | Target 1 card your opponent controls; destroy it. |
| Mill 3 | Deck-to-GY engines | Spell | Send the top 3 cards of your Deck to the GY. |
| Destroy a summoned monster | Trap Hole | Trap | When your opponent summons a monster: destroy 1 monster with 1000 or more ATK they control. |
| Bounce a monster | Compulsory Evacuation Device | Trap | Target 1 monster on the field; return it to the hand. |
| Revive from your GY | Call of the Haunted (simplified) | Trap | Target 1 monster in your GY; Special Summon it in Attack Position. |
| Discard to destroy | Raigeki Break | Trap | Discard 1 card, then target 1 card on the field; destroy it. |
| Punish an attack | attack-burn Traps | Trap | When an opponent's monster declares an attack: inflict 1000 damage to your opponent. |
| Search when sent to the GY | Sangan | Monster | If this card is sent from the field to the GY: add 1 monster with 1500 or less ATK from your Deck to your hand. |
| Send to GY on summon | Armageddon Knight | Monster | If this card is Summoned: send 1 monster from your Deck to the GY. |
| Recruiter | Mystic Tomato | Monster | If this card is destroyed by battle: Special Summon 1 monster with 1500 or less ATK from your Deck. |
| Tribute to destroy | Exiled Force | Monster | Tribute this card, then target 1 monster on the field; destroy it. |
| Pay LP for ATK | LP-for-power monsters | Monster | Pay 1000 LP; this card gains 1000 ATK until the end of this turn. |
| Burn after battle | battle-burn monsters | Monster | If this card destroys an opponent's monster by battle: inflict 500 damage to your opponent. |
| Piercing damage | Spear Dragon | Monster | If this card attacks a Defense Position monster, it inflicts piercing battle damage. |
| Direct attacker | Jinzo #7 | Monster | This card can attack directly. |
| Untargetable | untargetable bosses | Monster | Your opponent cannot target this card with card effects. |
| FLIP: destroy | Man-Eater Bug | Monster | FLIP: Target 1 monster on the field; destroy it. Tick the Flip ability on the Card tab. |
| Mill for ATK | Card Trooper | Monster | Once per turn: send the top 3 cards of your Deck to the GY, and if you do, this card gains 1500 ATK until the end of this turn. |
