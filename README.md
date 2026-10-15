# YGO Card Crafter

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
5. Click **Install into Omega** (or **Export** to get the files in a folder).

## How effects work

| Column | Meaning | Examples |
|---|---|---|
| **WHEN** | What starts the effect. One per effect. | You activate it in the Main Phase · Quick Effect · If this card is Summoned · Activate this Spell/Trap · While this card is face-up |
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

**Lasting effects.** Pick the WHEN block "While this card is face-up" to make effects that are always on,
such as "Dragon monsters you control gain 500 ATK" or "This card cannot be destroyed by battle".

**Extra Deck monsters.** Fusion, Synchro, Xyz and Link monsters get their summoning procedure from the
Card tab: materials by passcode or by "what a material must be", and how many.

**Passcodes.** Every card needs a unique passcode. Click **Generate** and Card Crafter picks one that no card in your
pack or anywhere in Omega uses. Each generated passcode starts its own block of 10,000 numbers, so once-per-turn
limits never collide between cards. If a passcode is ever taken later, for example by a new official card, the card
shows a warning the next time Card Crafter scans Omega, and installing is refused until it is changed.

**Card face.** The Card tab shows a live preview drawn like a real card: frame colour, name, Attribute, Level, Rank or
Link Arrows, Pendulum scales, type line, card text and ATK/DEF. **Choose art…** sets the illustration inside the frame.
YGO Omega draws the frame itself, so only the art is exported, as `.jpg`.

**Archetypes and custom Types.** In the left panel, **Archetypes → Use one from Omega…** lists every archetype already
in the game, with names guessed from their cards, so your cards can join an existing archetype. **New archetype** picks a
code nothing in Omega uses. **Custom Types** adds monster Types beyond the standard ones; Types Omega already uses are
listed there to add with one click.

**Templates.** **+ From a template…** inserts effects modelled on well-known cards: Pot of Greed, Monster Reborn,
Mystical Space Typhoon, Elemental HERO Stratos, Cyber Dragon, and more. They are normal blocks you can change.

**Anything the blocks can't do yet.** On the Script tab, **Edit by hand…** copies the generated script into an
editor so you can finish it in Lua. Card Crafter still checks it for syntax errors and misspelled engine functions.

The full list of blocks and templates is in [docs/blocks.md](docs/blocks.md).

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

Omega is often installed under `C:\Program Files (x86)`, where Windows only lets administrators write. When that
happens Card Crafter offers to restart itself as administrator and finish the install.

> If **Install into Omega** stays greyed out, your Omega version keeps card data somewhere Card Crafter doesn't
> recognise. Use **Export** instead, and post the report from the Omega tab (**Copy report**) so support can be added.

## What gets written

**Export** writes the standard custom-card layout:

```
<pack name>.cdb        card database (SQLite: datas + texts tables)
script/c<passcode>.lua one script per card
pics/<passcode>.jpg    card artwork, if you added any
```

Packs are saved as `.ycc` files: readable JSON you can share, keep in git, or post on Discord.
**Import .cdb…** brings existing custom cards in, including any scripts in a `script` folder next to the database.

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

CI runs all of this on every push and builds the Windows exe. Pushing a tag such as `v0.1.0` publishes a release.

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

Yu-Gi-Oh! is a trademark of Konami. YGO Card Crafter is a fan project and is not affiliated with Konami or YGO Omega.
