# YGO Card Crafter

Created by [Niduus](https://github.com/Niduus). Main repository: [github.com/Niduus/ygo-card-crafter](https://github.com/Niduus/ygo-card-crafter).

**DISCLAIMER: This is vibe coded. Problems will occur and nothing is coded by my hands. I got bored and decided to make something that maybe helpful if released to the wild.**

Make custom Yu-Gi-Oh! cards **and their scripts** for YGO Omega without writing Lua.

Card Crafter is a desktop app with a GDevelop-style event sheet. Every effect is one row:
**WHEN** it starts and **IF** it's allowed on the left, the **COST** and what it **DO**es on the right.
You pick blocks, fill in the blanks, and Card Crafter writes the card database entry, the card script,
and even suggested card text. Then it finds your Omega folder and installs the cards.

![The event sheet](docs/screenshots/2-effects-tab.png)

![Live card-face previews](docs/screenshots/7-card-faces.png)

## Why

Ask "how do I script my custom card?" on Discord and the answer is usually "copy an official script
and change it". Card Crafter turns those copied patterns into blocks and templates, so people can
reuse existing card features on their own cards and see exactly what each part does.

## Getting started

1. Download `YGOCardCrafter.exe` from the [Releases](../../releases) page. It is a single file and needs nothing installed.
   Windows SmartScreen may warn about an unknown publisher because the exe is not code-signed yet; choose **More info → Run anyway**.
2. Run it. It looks for YGO Omega on its own and shows what it found in the top-right corner.
3. You start with **My First Card**, which already has a working effect. Open the **Effects** tab to see how it is built.
4. Change anything: click a blank to edit it, use **+ Add condition / cost / action**, or **+ From a template…**.
5. Click **Install into Omega** (or **Export** to get the files in a folder). Tick **Install on save** and every
   save puts the database, scripts and art into Omega's folders for you.

### Updates

Card Crafter checks GitHub for a newer release when it starts. When one exists, a banner offers **Update now**:
it downloads the new exe, checks its size and SHA-256 checksum, swaps it in for the running one, and restarts with
your pack open. The version button in the top-right corner checks on demand. Updates come from the public
[Releases](../../releases) of this repository, so the repository has to be public for updating to work.

## How effects work

| Column | Meaning | Examples |
|---|---|---|
| **WHEN** | What starts the effect, one event per block; **+ Or when...** adds more. | You activate it in the Main Phase · Quick Effect · If this card is Summoned · Activate this Spell/Trap · While this card is face-up |
| **IF** | What must be true. | You control 2 or more Dragon monsters · It is your opponent's turn · Your LP are 2000 or less |
| **COST** | What you pay to activate. | Discard 1 card · Pay 1000 LP · Banish this card |
| **DO** | What happens, in order. | Draw 2 cards · Add 1 monster from your Deck to your hand · Target 1 card; destroy it |

Actions after the first say how they connect to the one before, using card wording:
*and*, *and if you do*, *then*, or *also, after that*.

**Which cards?** Blocks that pick cards have a blue chip such as `monster from your Deck`. Click it to
choose whose cards, which zones, and what they must be: card type, monster Type, Attribute, archetype,
Level, ATK, face-up, or "other than this card".

**Once per turn.** Each effect can be unlimited, once per turn for each copy, or once per turn by card
name ("You can only use this effect of X once per turn"). Give effects the same *shared group* number
when they share one use.

**Link Arrows.** Link monsters can use their arrows in effects. Any card picker has "only cards this card points to",
so you can build "monsters this card points to gain 500 ATK" or "can't be destroyed". There are also IF blocks for
"this card is linked / co-linked / points to 2 or more monsters", and Special Summons "to a zone this card points to".
The card face draws the arrows, lit up where you set them.

**Negation and responses.** Quick Effects and Traps can respond "when your opponent activates a card or effect" (or only
monster effects, or only Spells/Traps). Pair that with **Negate the activation**, optionally destroying or banishing the
card, to build Ash Blossom- or Solemn-style cards. **Negate the effects of** face-up cards works like Effect Veiler.

**Lasting effects.** Pick the WHEN block "While this card is face-up" to make effects that are always on,
such as "Dragon monsters you control gain 500 ATK" or "This card cannot be destroyed by battle".

**Blocks from real cards.** The blocks were widened from a scan of every official script, adding what real cards use
most: "If this card is banished / leaves the field / is Tributed / is Link Summoned / inflicts battle damage", "When this
card declares an attack / is attacked", Damage Step and Battle Phase timings, "when your opponent would Summon" with
**Negate the Summon**, effects from the hand or GY (**Special Summon this card** says "from your hand"), and "Something
happens in the duel" triggers such as "If your opponent Special Summons a monster(s)". Actions include discarding,
changing positions, taking control, attaching material, Tokens (make the Token as a card with the Token frame), ATK
becoming half or 0, Level changes, attacking directly or twice, and "you cannot Special Summon except ...". Lasting
effects include immunity, "cannot attack / be attacked / be Tributed", no battle or effect damage and what an Equip
Spell gives the equipped monster. Equip Spells equip themselves to a monster you choose under **Equip to**.

Card pickers can pick **this card itself** ("Special Summon this card from your hand", not "1 copy of this card"), and
counts can be **exactly**, **up to** or **all** ("send up to 3 cards", "destroy all monsters your opponent controls").
COST blocks can send, banish, shuffle or return any cards you pick, like the new Ra Fusion's "send X cards to the GY",
and the amount can be **for each** card the cost used. Triggers can watch other cards: "If a Dragon monster(s) is
Special Summoned to your field", "If a "Blue-Eyes" monster(s) you control is destroyed", "If a monster(s) is sent to
your GY", Ritual/Fusion/Synchro/Xyz/Pendulum/Link Summons, and more activation timings ("when a Spell Card is
activated", "when your opponent activates a monster effect"). The Card tab also has "You can only control 1 X" and
"You can only Special Summon X once per turn".

**Floodgates and lock-outs.** Lasting effects can lock players: "Neither player can Special Summon monsters, except WIND
monsters" (Vanity's Emptiness, Barrier Statue), "You cannot Special Summon from the Extra Deck, except Rank 4 Xyz
Monsters" (Palm Ryzeal), "Neither player can activate monster effects" (Majesty's Fiend), effects of monsters in the
hand or GY (Mind Drain, Soul Drain), "Spell Cards, and their effects on the field, cannot be activated" (Spell
Canceller, Jinzo) and no adding cards from the Deck except by drawing (Mistake). **Any card sent to the GY is banished
instead** covers Macro Cosmos, Dimensional Fissure (monsters only), Kashtira Arise-Heart and Masked HERO Dark Law
(your opponent's GY), and a DO version lasts until the end of the next turn (Dimension Shifter). **Negate all** Spell,
Trap or monster effects on the field works like Imperial Order, Royal Decree and Skill Drain. The COST lock says "You
cannot Special Summon from the Extra Deck **the turn you activate this effect**, except Xyz Monsters" (checked against
what you already did this turn) or "for the rest of this turn after it is activated"; on a Special Summon procedure it
reads "If Summoned this way, ..." like the Ryzeal monsters. Locks can also stop activations or attacks ("you cannot
declare attacks, except with Rank 4 Xyz Monsters"), and a DO lock can be put on your opponent. The templates include
each of these cards.

**Branching inside an effect.** DO blocks connect with "and", "and if you do", "then", "also, after that" or
"and if you don't", and any DO block can be ticked **you can**, so the player decides as it resolves: "You can negate the
Summon, and if you do, you can Special Summon this card from your hand." The DO menu also offers every IF block under
**Continue only if...**: the DO blocks after it happen only if it holds ("draw 1 card, then if you control 2 or more
monsters, gain 1000 LP"). The **● or instead** link starts another option of **Activate 1 of these effects**: each
option has its own blocks, targets and checks, the player picks one that can be used, and the card text lists them as
● bullets.

**Archetype mechanics.** Blocks for what makes newer archetypes unusual:
- *Mitsurugi:* Tributing as an effect ("then Tribute 1 monster you control", "make your opponent Tribute 1 monster they
  control (their choice)"), Tributing from your hand or field as a cost, "If another Reptile monster(s) you control
  would be destroyed..., you can Tribute this card instead" (also detach, banish or send it), **Ritual Summon** as a DO
  block (from the hand, Deck or GY; Levels equal or equal-or-exceeding), "your opponent can discard 1 card, or that
  effect is negated", "reveal this card in your hand" and **once per Duel**.
- *Dominus:* Traps you can activate from your hand (with IF blocks such as "your opponent controls a card", "you control
  no cards" or "your opponent has activated a monster effect in the hand or GY this turn"), timings for "an effect that
  Special Summons / adds from the Deck to the hand / destroys", "then if you have a Trap in your GY, destroy that card",
  and locks "for the rest of this Duel" that only apply "if you activated this card from your hand".
- *Zones (Elfnote, Infinite Impermanence):* "Special Summon this card (from your hand) to your center Main Monster
  Zone", "this card is in the center Main Monster Zone", card pickers for "the monster in your center Main Monster
  Zone", moving a monster to another zone or swapping with the center one, and Infinite Impermanence's "all other
  Spell/Trap effects in this column are negated".
- *Forbidden Crown:* **Apply these effects** to a monster with each effect ticked on its own (negated, can't attack,
  can't be destroyed, unaffected by activated effects, can't be Tributed, can't be material), and "Neither player can
  activate monster effects in response to this card's activation".
- *Mind Scan:* "Your opponent must keep their hand revealed" and "declare 1 card name; negate the activated effects of
  cards with that original name until the end of this turn".
- "If this card battles a monster, neither can be destroyed by that battle", "ignoring its Summoning conditions",
  Main Phase 1 / Main Phase 2 (plain "Main Phase" means either) and Spell/Trap as a card type in every picker.

**One event per WHEN, combined with "+ Or when...".** Every WHEN dropdown holds single events: "If this card is
Normal Summoned", "Special Summoned", "Tributed", "When your opponent Special Summons a monster(s)". To start one
effect on several, add **+ Or when...**: the new WHEN starts like the first (optional or not, where this card is) on
the next event you haven't used. The card text joins them like a real card:
- "If this card is Normal or Special Summoned, or if this card is Tributed" (Mitsurugi)
- "If this card is sent to the GY or banished", "If this card is destroyed by battle or card effect"
- "If this card is Special Summoned from the hand or GY", "If your opponent Normal or Special Summons a monster(s)"
- "When your opponent Normal or Special Summons a monster(s) (Quick Effect)"
- "If this card is Normal Summoned, or if your opponent Special Summons a monster(s)"

Any triggers can be combined, and the effect keeps one once per turn. Triggers of the same kind share one effect,
registered once per event; when they check different things, the effect checks which event started it. Other triggers
become clones of the effect with their own type, zone and checks, as official scripts do. Fields that don't apply are
hidden, like "from the GY" on a Normal Summon. Packs from older versions, and their combined choices ("Normal or
Special", the old tick-several block), open already split into single WHENs.

**Extra Deck staples.** Blocks to make your own I:P Masquerena or S:P Little Knight:
- **Link/Synchro/Xyz Summon using this card**: "Link Summon 1 Link Monster using materials you control, including this
  card". With a Quick Effect "during the Main Phase" and "during your opponent's turn", it reads "During your opponent's
  Main Phase (Quick Effect)". A Tuner is the Synchro's Tuner; any other card is made a material the Summon must use.
- **A monster Summoned using this card as material gains...**: "A Link Monster that used this card as material cannot be
  destroyed by your opponent's card effects", or can't be targeted, is unaffected, gains ATK, pierces, attacks twice.
- **This card was Summoned using certain materials**: "If this card is Link Summoned using a Fusion, Synchro, Xyz, or
  Link Monster as material", or "using 3 or more monsters".
- **Banish ... until the End Phase**: the banished cards come back to the field in the End Phase.
- Templates for each, in the "Extra Deck" group.

**Red-Eyes Dark Dragoon, from its script.** The card browser now turns all of Dragoon into blocks, and the same
pieces work on any card:
- Fusion Materials where one slot takes either kind: ""Dark Magician" + "Red-Eyes Black Dragon" or 1 Dragon Effect
  Monster" (**+ or another kind** on a material in the Card tab).
- "This card cannot be targeted by card effects" (any player's) next to "cannot be destroyed by card effects".
- "Inflict damage to your opponent equal to that monster's original ATK" (also its ATK on the field, or its original
  DEF, and for gaining LP), read from the cards the DO block before destroyed or banished.
- "You can use this effect a number of times per turn up to the number of Normal Monsters used as material for this
  card" (any kind of material).

**Placing and Setting.** **Place cards face-up** puts Spells/Traps from your hand, Deck, GY or banishment face-up in
your Spell & Trap Zone, Field Zone or Pendulum Zone without activating them, or places a card (a monster too, like the
Crystal Beasts) "as a Continuous Spell/Trap". **Set Spells/Traps** has "it can be activated this turn".

**Menu shortcuts.** The add menus list blocks by what they do: COST → This card → **Discard this card**, **Tribute this
card**, **Banish this card**...; DO → This card → **Destroy this card**, **Discard this card**...; DO → Cards → **Set 1
Trap from your hand or Deck; it can be activated this turn**, **Place 1 Field Spell from your hand or Deck in your Field
Zone** and more. A discard cost that names this card ("discard 1 "Sundering - Inferno"") can discard this very card.

**More events.** Things that can happen to this card: Special Summoned from the hand, Deck, GY, banishment or Extra
Deck; banished face-down; leaves the field because of an opponent's card (effect); drawn; returned to the Deck or Extra
Deck; placed face-up in the Extra Deck; detached from an Xyz Monster; its battle position is changed; control of it
changes; equipped with an Equip Card; targeted by an opponent's card effect; you take battle damage from its battle;
declares a direct attack; sent to the GY by a card effect, by an opponent's card (effect), by battle or as a cost;
destroyed while face-down or in the Pendulum Zone; used as Ritual Material. In the duel: you or your opponent Flip
Summons; your opponent Sets a monster or Spell/Trap; monsters destroyed by battle on either side; cards discarded;
cards returned to the hand; you search or draw; your opponent takes damage or gains LP; you take effect damage; a
monster you control declares an attack; your opponent's monsters leave the field; control or battle position changes;
your cards are targeted; your opponent activates a Spell/Trap Card or effect, a Spell Card or a Trap Card; you
activate a card or effect. Matching cards can also be Flip Summoned, banished, Tributed, returned to the hand, destroyed
by a card effect or leave the field. Quick Effects can be "During the Main Phase" or "During the Standby Phase".

**Colour-coded card browser.** Every effect of a browsed card says what became of it, in colour, and quotes the
sentence of the card's text it stands for:
- 🟢 **Made into blocks**, with the blocks' text.
- 🟡 **Done another way**: a Card tab setting (summoning rule, materials, "You can only control 1", Equip to) or
  something Card Crafter writes itself, such as the card's activation.
- 🟠 **Not translated yet**: Card Crafter couldn't read the script, but blocks for that kind of effect exist, so it can
  most likely be built by hand from the quoted text.
- 🔴 **No block does this**, and why: for example a lasting effect no block gives yet, or a hidden rule that runs on an
  event.

**More blocks from the official scripts.** **Fusion Summon** (Polymerization: from the hand or field, only the field,
the hand, Deck too, banishing from the field or GY, or shuffling from the GY), "Once per turn, if this card would be
destroyed by battle, it is not destroyed" (and "the first time each ... monster would be destroyed each turn"), "Your
opponent cannot target other monsters you control for attacks", "You can Normal Summon this card without Tributing"
(with IF blocks for when), "This card's name becomes "X" while on the field or in the GY", "If this card is Special
Summoned by its own effect" (self Special Summons and procedures now carry SUMMON_VALUE_SELF like official cards),
"gains ATK for each material attached to it / of its Levels / monster it points to", more "unaffected by" choices
(Spell, Trap, your opponent's Spells/Traps or activated effects), and Special Summon conditions written out become the
Card tab's summoning rule.

**A certain card on the field.** The IF block **A certain card is on the field (or somewhere)** reads "if "Dark
Magician" is on the field", "while you control "Blue-Eyes White Dragon"", "if your opponent does not control ..." or
"while "Umi" is in a Field Zone", so a Quick Effect or Quick-Play Spell can Special Summon or activate only while that
card is there. Every card picker's **Only this card** box takes a name: type part of it and pick from your pack and
every card in YGO Omega's databases (or type a passcode). Card pickers can also take a second archetype
(""Dominators" or "Dominus Purge" Traps") and **face-down only** cards. Archetypes in YGO Omega's cards show by name
("Mitsurugi", not "0x1c3"): each name comes from what card texts put in quotes, such as "add 1 "Mitsurugi" monster".

**More from Dominus and Mitsurugi.** The browser now turns every Dominus card into blocks, including Dominusteer
Rosepamela ("reveal this card in your hand; add 1 card, then discard 1 card", "Set 1 Trap. It can be activated this
turn", "face-down cards in your Spell & Trap Zone cannot be destroyed"), the "your opponent can Special Summon 1 monster
from their hand/GY" step of Spark and Spiral (**Your opponent can Special Summon a monster**), and "If you activated
this card from your hand, you cannot activate the effects of LIGHT, EARTH, and WIND monsters for the rest of this
Duel", which applies whatever else the card did. Mitsurugi's "If this card is Normal or Special Summoned, or if this
card is Tributed" is one WHEN, Murakumo's "your opponent can discard 1 card, or that effect is negated" and Futsu no
Mitama's **once per Chain** translate, and so do Habakiri and Great Purification's negation.

**Card text.** The suggested text reads like a printed card: the effects run together in one paragraph, a card with
several once-per-turn effects ends with "You can only use each effect of "X" once per turn.", and the picked zones
change the wording ("3 monsters your opponent controls"). A card saved with no text of its own gets its suggested text.

**Summoning conditions.** Extra Deck monsters have a **Summoning conditions** section on the Card tab that shows the
materials line exactly as it will be printed and edits it: "2 Effect Monsters" (S:P Little Knight), "2 Level 12
monsters" (TY-PHON), "1 "Aleister" monster + 1 EARTH or LIGHT monster" (Invoked), "1 Tuner + 1+ non-Tuner monsters".
Fusion Materials are listed one by one, each a specific card or a filter; Xyz Monsters can use a material Level other
than their Rank; Link Monsters can require "including a ..." material. Filters can ask for Effect Monsters, Tuners,
non-Tuners and other kinds, and several Attributes at once. Other ways to Summon a monster, like TY-PHON's, are effects.

**Card filters.** Besides Type, Attribute, Level, ATK and archetype, a filter can ask for a position ("face-down
Defense Position"), a Spell/Trap kind ("Field Spell", "Counter Trap"), how it was Summoned ("Special Summoned from the
Extra Deck"), DEF ("with 0 ATK and 0 DEF"), two archetypes or several names (""Dark Magician" or "Dark Magician
Girl""), cards that mention a card, and cards in the hand that aren't revealed yet.

**Codes.** The Card tab and the card browser list the card's passcode, card type, Type, Attribute and every archetype
code (with its name), each with a **Copy** button, plus **Copy all codes**.

**Older script sets.** Some YGO Omega versions ship an older set of engine scripts. When a card's script uses a newer
constant or helper (like `SUMMON_VALUE_SELF` for "Summoned by its own effect", or `aux.SelectFromOptions` for
"Activate 1 of these effects"), the script defines it if the game doesn't have it.

**Strings (str1–str16).** Like DataEditorX's string section, the Card tab's **Strings** section shows what the database
gets for each `aux.Stringid`: each effect's own text, then the options of "Activate 1 of these effects". Type over any
of them; **Reset** goes back to the effect's text. The game shows these in its list of effects to activate, "you can"
questions and option choices. When your effect is the *only* one that can be activated, the engine itself asks "From
[Hand], activate [card]'s effect?" without any effect text (official cards too), so that prompt shows the location.

**Summoning rules.** "Cannot be Normal Summoned" is a rule about the card, not an ability, so it lives in the Card tab's
**Summoning** choice:

| Choice | On the card | What the engine does |
|---|---|---|
| Can be Normal Summoned | | Normal Summon and Set as usual |
| Can't be Normal Summoned: must first be Special Summoned | "Cannot be Normal Summoned/Set. Must first be Special Summoned…" | Can't be Normal Summoned, and until it is properly Summoned it can't be Special Summoned by other effects. After that it can be revived. |
| Can only be Special Summoned by its own procedure | "…Must be Special Summoned… and cannot be Special Summoned by other ways." | It can only ever be Summoned by its own procedure. |

Extra Deck and Ritual monsters can choose "Must be Fusion Summoned" (or Synchro, Xyz, Link, Ritual) instead.

How it is Special Summoned is an effect with the WHEN block **Special Summon procedure**, like Cyber Dragon or Chaos Sorcerer:
IF blocks say when it's allowed ("if your opponent controls more monsters") and COST blocks what you pay ("banish 2 monsters
from your GY", "Tribute 1 monster"). It doesn't start a chain, so it has no DO blocks, and "once per turn" reads
"You can only Special Summon *X* once per turn this way." Monsters that can be Normal Summoned can have one too.
The templates **Free summon from hand**, **Banish from GY to summon** and **Tribute to summon** are ready-made ones.

**Card pool and limitation (YGO Omega).** Omega's deck editor lists cards by **Card Pool** and **Limitation**, and a card
without them doesn't come up in its searches. New packs put their cards in the **Custom** card pool (the left panel
sets it for the pack, the Card tab for one card); OCG or TCG cards also get a release date, which Omega needs to list
them. **Limitation** (Unlimited, Semi-Limited, Limited, Forbidden) is written onto every banlist in Omega; Unlimited
cards are on none, like official ones. Custom databases are written in the same layout as Omega's own card database,
which the Omega tab's report shows. Packs made with older versions move from OCG/TCG to Custom when opened.

**Passcodes.** Every card needs a unique passcode. Click **Generate** and Card Crafter picks one that no card in your
pack or anywhere in Omega uses. Each generated passcode starts its own block of 10,000 numbers, so once-per-turn
limits never collide between cards. If a passcode is ever taken later, for example by a new official card, the card
shows a warning the next time Card Crafter scans Omega, and installing is refused until it is changed.

Passcodes stay at or under **134,217,727**. The game finds a card's effect texts (its prompts, "you can" questions
and effect lists) through `aux.Stringid(id,n)`, which is `id*16+n`, and YGO Omega reads it as a signed 32-bit number;
a larger passcode points nowhere, so the game shows the card's location ("hand") or "Unknown (-1968087296)" instead
of the effect. Cards made with older versions
can have such passcodes: the Card tab warns about them, and **Fix all passcodes in this pack** gives each a new one
and updates the cards and scripts that name them. Save again afterwards; decks that held the old passcodes need those
cards added again.

**Card face.** The Card tab shows a live preview laid out like a printed card: name, Attribute, Level/Rank stars or
Link Arrows, Pendulum scales, the set number under the art, "[Type/Effect]", card text, ATK/DEF, the passcode with its
edition, and the copyright line (left panel; it defaults to "©year author"). **Choose art…** sets the illustration.
YGO Omega draws the frame itself, so only the art is exported, and Omega only accepts art as a 624 × 624 `.jpg`: any
picture (JPEG, PNG, WebP...) is centre-cropped to a square, resized to 624 × 624 and saved as a JPEG when you export,
install or save into a database.

**Card textures.** Card Crafter includes no card artwork. The preview is assembled from card textures you provide:
**Textures… → Use card textures from a .zip…** (unpacked into `%AppData%\YGO Card Crafter\Textures`) or **from a folder…**.
It looks for YGO Omega's texture names, with or without an export number (`EffectFrame #2375.png` works):

| Part | Files |
|---|---|
| Frames (813 × 1185) | `NormalFrame`, `EffectFrame`, `RitualFrame`, `FusionFrame`, `SynchroFrame`, `XYZFrame`, `LinkFrame`, `TokenFrame`, `SpellCardFrame`, `TrapCardFrame` |
| Pendulum frames | `PendulumNormalFullFrame`, `PendulumEffectFullFrame`, `PendulumFusionFullFrame`, `PendulumSynchroFullFrame`, `PendulumXYZFullFrame`, `PendulumBottom` |
| Card edge | `DefaultBorder` |
| Attributes | `Dark`, `Light`, `Earth`, `Water`, `Fire`, `Wind`, `Divine`, `Spell`, `Trap` |
| Stars | `Level`, `Rank` |
| Spell/Trap icons | `QuickPlay`, `Continuous`, `Equipment`, `Field`, `Ritual`, `Counter` |
| Link Arrows | `TopOn`/`TopOff`, `BottomOn`/`BottomOff`, `LeftOn`/`LeftOff`, `RightOn`/`RightOff`, `TopLeftOn`/`TopLeftOff`, `TopRightOn`/`TopRightOff`, `BottomLeftOn`/`BottomLeftOff`, `BottomRightOn`/`BottomRightOff` |

When a frame comes in a standard and a full-art version, the standard one is used. Everything is placed at positions
measured from those textures. Without textures the preview is a plain placeholder.

When no textures are chosen, Card Crafter looks for these files among the loose files in your YGO Omega folder and
uses them if they are there. To have a copy of the app that always carries your textures, drag your texture zip onto
`tools\build-my-exe.cmd` (needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)): it builds
`publish-private\YGOCardCrafter.exe` with the zip inside. That exe is for you only; the zip stays in the git-ignored
`private\` folder and is never part of the public download.

**Card fonts.** Printed cards use Matrix Small Caps (names), ITC Stone Serif Small Caps Bold (type line, ATK/DEF),
Matrix Book (card text) and Stone Serif Italic (Normal monster text). These are commercial fonts, so they aren't
included: Card Crafter uses them when they are installed on your PC or are in the texture zip/folder (`.ttf`, `.otf`),
and otherwise the bundled free look-alikes Spectral SC and Crimson Text. The line under **Textures…** says which are used.

**Card images.** **Save image…** under the preview saves the card as you see it, as PNG, JPG or WebP, at a standard size
(from 177 pixels wide for ygopro pics up to 1626, print sizes at 300 and 600 DPI, or any width you type). **Save every card in the pack…** does them all at once.

**If something goes wrong.** Unexpected errors are shown in a message and saved to
`%AppData%\YGO Card Crafter\crash.log` instead of closing the app. Please attach that file when reporting a problem.

**Lists tab: archetypes, Types and banlists.** The **Lists** tab holds the pack's archetypes, custom Types and
banlists. **+ New banlist** makes a format of your own: search Omega's cards (or this pack's) by name and put each on
it as Forbidden, Limited or Semi-Limited. **Scan YGO Omega** (or **Scan a database file…**) lists the banlists that
already exist, with their cards, and **Copy into pack** makes an editable copy. Saving writes the banlists into the
database's banlist tables, where YGO Omega lists them next to its own, and Export also writes an `lflist.conf`.

**Archetypes and custom Types.** On the Lists tab, **Archetypes → Use one from Omega…** lists every archetype already
in the game, with names guessed from their cards, so your cards can join an existing archetype. **New archetype** picks a
code nothing in Omega uses. **Custom Types** adds monster Types beyond the standard ones; Types Omega already uses are
listed there to add with one click.

**Templates.** **+ From a template…** inserts one of 46 effects modelled on well-known cards: Pot of Greed, Upstart Goblin,
Reinforcement of the Army, Foolish Burial, Monster Reborn, Mystical Space Typhoon, Raigeki Break, Trap Hole, Compulsory
Evacuation Device, Sangan, Mystic Tomato, Exiled Force, Card Trooper, Man-Eater Bug, Cyber Dragon, Chaos Sorcerer,
Ash Blossom, Effect Veiler and more. They are normal blocks you can change, and every one is played in a real duel by
the tests.

**Card browser.** **Browse cards…** searches every card in YGO Omega (its own database and the custom ones) by name
and by card text: every word must appear, "quoted phrases" match exactly, so `negate` lists every card whose text says
negate. Pick a card to see its text and its script split into its effects, each with the functions it needs:
**Use as a new card** copies the whole card (stats, text, art, script) into your pack with a new passcode, and
**Add as script** puts one effect into the card you're making, renamed so nothing clashes.

Each effect is also read for blocks: when its trigger, limit, condition, cost, targets and actions match blocks exactly,
the browser says **Becomes blocks** with the effect's wording, and **Add as blocks** adds it to the Effects tab as
WHEN/IF/COST/DO blocks you can change. When every effect of a card has exact blocks, **Use as a new card** makes the
copy with blocks (the original script is kept on the Script tab). Effects without exact blocks say why and stay script,
so a translation never changes what a card does. A card on a hand-written script can try the same from the Effects
tab with **Turn the script into blocks**. Of the 13,571 official scripts, 818 become blocks completely and 11,737 of
their 35,901 effects do, including Synchro, Xyz, Link and Fusion material lines. When Omega's
folders have no script for a card, the official one can be fetched from
[ygopro-scripts](https://github.com/Fluorohydride/ygopro-scripts). **Browse a database file…** searches any `.db`/`.cdb`.

**Anything the blocks can't do yet.** On the Script tab, **Edit by hand…** copies the generated script into an
editor so you can finish it in Lua. Card Crafter still checks it for syntax errors and misspelled engine functions.

The full list of blocks and templates is in [docs/blocks.md](docs/blocks.md).

**Ask Claude.** The **Assistant** tab is a chat with Claude that knows how Card Crafter works. Paste your own Claude API
key (from the [Claude Console](https://console.anthropic.com/settings/keys)), then ask it to build an effect from card
text, explain a card, or find out why something shows an error. It changes your cards through the same blocks you use,
every change shows in the editor, and **Undo Claude's last changes** puts them back. Usage is billed to your own
Anthropic account; the key stays on your computer. See [docs/ai-assistant.md](docs/ai-assistant.md).

## Finding YGO Omega

Card Crafter looks in this order and stops at the first folder that actually contains Omega:

1. The folder you chose before.
2. The `YGO_OMEGA_PATH` environment variable.
3. The Windows "installed apps" registry.
4. Standard folders such as `C:\Program Files (x86)\YGO Omega`, your user folders, and `X:\YGO Omega` on each drive.
5. A quick two-level search of each drive.

If it still can't find Omega, use **Browse…** on the Omega tab.

### Installing into Omega

Card Crafter scans the Omega folder and installs where Omega keeps custom cards:

| What | Where in the Omega folder |
|---|---|
| Card database | `YGO Omega_Data\Files\Databases\<pack name>.db` |
| Scripts | `YGO Omega_Data\Files\Scripts\c<passcode>.lua` |
| Card art | `YGO Omega_Data\Files\Arts\<passcode>.jpg` |

It never writes next to Omega's own card database (`Files\Bundles\db`), never overwrites a database it didn't make,
and refuses to install cards whose passcodes are already used. Anything it replaces is backed up first to
`%AppData%\YGO Card Crafter\Backups`.

**Install on save** (next to **Install into Omega**) does this automatically every time you save.

### Opening and saving card databases

**Open…** opens a Card Crafter pack (`.ycc`) or any card database (`.db` or `.cdb`), and **Open from Omega** lists the
custom databases in Omega's `Databases` folder. A database opens as a pack with everything that goes with it:

| Database in | Scripts read from | Art read from |
|---|---|---|
| Omega's `Files\Databases` | `Files\Scripts` | `Files\Arts` |
| anywhere else | `script` next to it | `pics` next to it |

**Save** then writes back into that same database, and puts each card's script and art into those folders, backing up
every file it replaces. **Save as → Card database…** saves the pack as a new database the same way.

Card Crafter also stores the pack itself in the database, in a table of its own (`cardcrafter_pack`) that games ignore,
so a database it saved or installed opens again with its event sheets, archetype names and custom Types. Cards from
databases made elsewhere open with their scripts as hand-written Lua (Script tab), and keep every database value
Card Crafter has no setting for, so saving never loses anything. Omega's own card database is never written to.

Omega is often installed under `C:\Program Files (x86)`, where Windows only lets administrators write. When that
happens Card Crafter offers to restart itself as administrator and finish the install.

> If **Install into Omega** stays greyed out, your Omega version keeps card data somewhere Card Crafter doesn't
> recognise. Use **Export** instead, and post the report from the Omega tab (**Copy report**) so support can be added.

## What gets written

**Export** writes the standard custom-card layout:

```
<pack name>.cdb        card database (SQLite: datas + texts tables)
script/c<passcode>.lua one script per card
pics/<passcode>.jpg    card artwork (624 × 624), if you added any
```

Packs are saved as `.ycc` files: readable JSON you can share, keep in git, or post on Discord.
**Import cards…** adds the cards of another database to the current pack, with their scripts and art.

## Are the scripts correct?

Every generated script is checked three ways:

- **Lua syntax**, using the real Lua 5.4 parser.
- **Engine names**: every `Duel.*`, `Card.*`, `aux.*` function and every constant must exist in the engine.
- **Real duels**: the test suite builds the open-source ygopro engine
  ([ygopro-core](https://github.com/Fluorohydride/ygopro-core)), loads the generated cards with the official
  helper scripts, and plays short duels automatically. It checks each effect activates, resolves, and changes
  the board as expected: cards drawn, monsters summoned, LP changed, and once-per-turn limits enforced.
  Every block and every template is covered.

Omega runs its own build of the ygopro engine, and its exact script dialect has not been confirmed yet.
Card Crafter deliberately uses only long-established engine functions to keep the scripts portable.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```
dotnet test
dotnet run --project src/CardCrafter.App
```

Single-file Windows exe:

```
dotnet publish src/CardCrafter.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o publish
```

The real-engine duel tests run when the harness is built. They are skipped otherwise. On Linux:

```
sudo apt install liblua5.4-dev libsqlite3-dev
git clone https://github.com/Fluorohydride/ygopro-core engine/core
git clone https://github.com/Fluorohydride/ygopro-scripts engine/scripts
tools/engine-harness/build.sh engine/core engine/harness
CARDCRAFTER_HARNESS=$PWD/engine/harness CARDCRAFTER_ENGINE_SCRIPTS=$PWD/engine/scripts dotnet test
```

CI runs all of this on every push and builds the Windows exe. Running the **Release** workflow from the Actions tab with a
version such as `0.3.0` (or pushing a tag such as `v0.3.0`) publishes a release with `YGOCardCrafter.exe` attached;
that becomes the app's version, and installed copies offer it as an update.

### Layout

| Path | What it is |
|---|---|
| `src/CardCrafter.Core/Model` | Cards, effects, filters, `.ycc` pack files |
| `src/CardCrafter.Core/Blocks` | Every event-sheet block: its sentence, its blanks, and the Lua it produces |
| `src/CardCrafter.Core/Compiler` | Turns an event sheet into a card script |
| `src/CardCrafter.Core/Templates` | The template library |
| `src/CardCrafter.Core/Text` | Suggested card text from effects |
| `src/CardCrafter.Core/Database` | `.cdb` reading and writing |
| `src/CardCrafter.Core/Omega` | Finding, scanning, exporting to and installing into Omega |
| `src/CardCrafter.Core/Validation` | Lua syntax and engine-name checks |
| `src/CardCrafter.Core/Assistant` | The AI assistant: its guide, its tools, and the Claude conversation ([docs/ai-assistant.md](docs/ai-assistant.md)) |
| `src/CardCrafter.App` | The Avalonia desktop app |
| `tools/engine-harness` | Plays test duels in ygopro-core |

### Adding a block

1. Subclass `BlockDefinition`, or one of the helpers: `SelfTriggerBlock`, `ConditionBlock`, `CostBlock`, `ActionBlock`, `CardGroupAction`, `PassiveBlock`.
2. Give it an `Id`, a `Sentence` with `{blanks}`, its `Params`, and a `Compile` method that adds Lua through the
   `EffectBuilder`: categories, conditions, cost/target checks, and resolution steps.
3. Register it in `BlockRegistry`.

The tests compile every block with every option and play it in the engine, so a new block is covered automatically.

## Credits

Card scripting follows the conventions of the ygopro community. The engine-name list in
`src/CardCrafter.Core/Validation/KnownApi.txt` is generated from
[ygopro-core](https://github.com/Fluorohydride/ygopro-core) (MIT) and
[ygopro-scripts](https://github.com/Fluorohydride/ygopro-scripts) (GPL-2.0), which are also used, unmodified,
by the test suite. Neither is bundled with the app.

The bundled fonts are Spectral SC, Crimson Text and a nine-character subset of Noto Serif JP, all under the SIL Open
Font License (`src/CardCrafter.App/Assets/Fonts/LICENSES.txt`). No card textures or card fonts are included.

Yu-Gi-Oh! is a trademark of Konami. YGO Card Crafter is a fan project and is not affiliated with Konami or YGO Omega.
