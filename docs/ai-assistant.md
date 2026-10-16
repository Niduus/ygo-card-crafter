# The AI assistant

Card Crafter's **Assistant** tab is a chat with an AI model that knows how the app works. You bring your own API key, from
Anthropic (Claude), OpenAI, Google Gemini or another service, or you run a model on your own computer. The assistant can
then explain the app, design cards with you, and build or fix effects on your cards using the same blocks you would
place by hand.

## Setting it up

1. Pick an **AI service** on the right of the Assistant tab.
2. Make a key there: **Get a key** opens the service's key page.
3. Paste the key and tick **Remember on this computer** if you don't want to paste it every time. Each service keeps
   its own key.
4. Pick a **model**:
   - For Claude, choose from the list, and set an **Effort**.
   - For any other service, type the model's name, or press **Load models** to list what the service has and start
     typing to pick one.

If the key box is empty, Card Crafter uses the service's usual environment variable when one is set (for example
`ANTHROPIC_API_KEY`, `OPENAI_API_KEY`, `GEMINI_API_KEY`).

| Service | Key from | Notes |
|---|---|---|
| **Anthropic (Claude)**, recommended | [Claude Console](https://console.anthropic.com/settings/keys) | The guide and tools were built and tested with Claude. Opus 5.5 is the default; Sonnet 5.5 is faster and cheaper, Haiku 5.5 the fastest and cheapest, Fable 5.1 the most capable and most expensive. |
| OpenAI | [platform.openai.com](https://platform.openai.com/api-keys) | |
| Google Gemini | [Google AI Studio](https://aistudio.google.com/apikey) | Uses Gemini's OpenAI-compatible endpoint. |
| OpenRouter | [openrouter.ai](https://openrouter.ai/keys) | One key for models from many companies. |
| xAI (Grok), Mistral, DeepSeek, Groq | Each service's console | |
| Ollama, LM Studio | No key | Runs a model on your own computer, free and offline. Start the program and load a model that supports tools first. The address can be changed. |
| Other OpenAI-compatible service | That service | Enter its API address (usually ending in `/v1`), and its key if it needs one. |

**The model must support tools** (also called function calling), because that is how the assistant reads and changes
your cards. A model without them answers with an error saying so.

Larger models follow Card Crafter's rules much better than small local ones. If a small model keeps making mistakes, try
a bigger one.

## What it costs

Each message is billed to your account at the service's API prices for the model you picked. Models on your own
computer cost nothing. The line under the chat shows how many tokens this conversation has used so far.

Card Crafter's guide and tool list are the same on every request. Claude caches them, and many other services cache
repeated prompts too, so later requests in a chat cost less. **New chat** starts over with an empty conversation. That is cheaper than carrying a long one around, and your
cards stay as they are.

## Privacy: what is sent, and where the key is kept

- **The key** is stored apart from your settings, one per service, and never written into a pack or an export. On
  Windows it is encrypted for your Windows user; on other systems only your user can read the file. It is sent only to
  the service you picked. **Forget key** deletes it.
- **What the service sees:**
  - your messages;
  - Card Crafter's guide (how the app works, plus the block catalog), which is the same for everyone;
  - the cards the assistant reads with its tools. That is the selected card first, and other cards in the open pack
    only when it looks them up.

  Nothing else on your computer is read or sent.

## What the assistant can do

The assistant works on the open pack through a small set of tools. The tools use the same model the editor uses: card fields,
effects made of blocks, card pickers and the Summon procedure.

- **Look things up:** search the block catalog, read a block's options and allowed values, see the fields of a card, a
  card picker or a requirement, and list the templates.
- **Read your cards:** list the pack's cards, read the selected card as data, with its generated card text and every
  problem the app finds.
- **Change your cards:** change card fields (name, Attribute, Type, ATK…), add, replace or remove effects, set the Summon
  procedure, add a new card, select another card.
- **Work with scripts:**
  - **compile:** build the card's Lua script;
  - **translate:** read any script, such as an official card's, back into blocks, to see how the app expresses a
    wording;
  - **hand-written script:** as a last resort, give the card its own script, but only for what no block can do.

Every change appears in the editor at once, and each one is listed in the chat. **Undo the last changes** puts the
cards back as they were before the assistant's last message that changed them; you can press it again to go further back.
Nothing is saved until you save the pack yourself.

### Good things to ask

- "Make this card: *If this card is Normal Summoned: You can add 1 Level 4 or lower Warrior monster from your Deck to
  your hand. You can only use this effect of "Ember Knight" once per turn.*"
- "Why does this card show an error, and how do I fix it?"
- "Explain what the second effect does in a duel, step by step."
- "Which block do I use for *during your opponent's turn*?"
- "Design a Field Spell for my archetype that rewards Special Summoning, then build it."

## How it works (for developers)

The assistant lives in `src/CardCrafter.Core/Assistant`; the tab is `src/CardCrafter.App/Views/AssistantView.axaml`
with `AssistantViewModel`.

| File | What it does |
|---|---|
| `AssistantGuide.cs` | The system prompt. It explains cards, the event sheet, card pickers, compiling and translating, and how the model should work, followed by the whole block catalog built from `BlockRegistry`. It is generated from the app, so it always matches the blocks of that version. |
| `AssistantTools.cs` | The tools: their JSON schemas and the code that runs them on an `IAssistantWorkspace`. |
| `AssistantChat.cs` | `IAssistantChat`, one conversation, whichever service runs it. |
| `AssistantSession.cs` | A conversation with Claude, through the official Anthropic C# SDK (`Anthropic` NuGet package). |
| `OpenAiCompatibleSession.cs` | A conversation with any other service, through the OpenAI-compatible Chat Completions format with function calling (plain `HttpClient`). |
| `AssistantProvider.cs` | The services: each one's address, key page and environment variable. |
| `AssistantWorkspace.cs` | `IAssistantWorkspace`, what the tools act on. The app implements it in `MainViewModel.Assistant.cs`; `PackWorkspace` is an in-memory one for tests. |
| `ApiKeyStore.cs` | Saving and loading each service's key. |

### The tools

The tools are checked and self-correcting:

- **Effect format.** Effects are read and written as the same JSON the `.ycc` pack file uses (camelCase fields, enum
  names). Card pickers list only the fields that differ from the block's default picker.
- **Checking.** `ReadEffect` checks every block before anything changes: that the block exists, that it fits its slot
  (WHEN, IF, COST or DO), and that every option is one the block has with an allowed value. Options left out keep the
  block's defaults, just as when a user adds a block.
- **Errors.** A mistake comes back to the model as an error naming what is allowed, so it can correct itself. The card stays
  untouched.
- **Feedback.** After every change the tool answers with the card text the app generated and every compiler and script
  checker problem. The model reads the same verdict the user sees on the Effects tab.
- **Round trip.** `EveryTemplate_RoundTripsThroughTheEffectJson` checks that every template survives the JSON round trip
  and compiles to the same script.

### The request loop

For Claude, `AssistantSession.Send` adds the user's message to the conversation. It starts the message with a short note naming the
selected card. Then it calls the Messages API with:

- the guide as the system prompt;
- the tools;
- automatic prompt caching;
- the chosen model and effort;
- on Opus, Sonnet and Fable, the server-side refusal fallback (`fallbacks: "default"` with the
  `server-side-fallback-2026-07-01` beta). A request a safety classifier declines is then re-run on the model Anthropic
  recommends.

When Claude asks for tools, they run in order and their answers go back in one message. This repeats until Claude
replies, for at most 40 rounds per message.

The conversation only ever grows: the model's replies are sent back exactly as received, thinking blocks and signatures
included. That keeps the cache valid.

For any other service, `OpenAiCompatibleSession` sends the same guide as the system message and the same tools as
functions to `{address}/chat/completions`. It runs each tool call and answers with a `tool` message. Arguments that aren't
valid JSON get an error answer, so the model can try again.

If a message fails, is declined or is stopped, the conversation goes back to how it was before it. Changes already made
to cards stay, and **Undo** covers them.

### Adding a tool

1. Add an `AssistantTool` to `AssistantTools.All`: a name, a description that says when to use it, and its input schema
   properties.
2. Handle it in `AssistantTools.Run`. Return a `ToolOutcome`; set `Activity` when it changes something, so the chat lists
   it. Throw nothing: report bad input as an error outcome.
3. If it changes a card, call `workspace.CardChanged()` and answer with `Verdict(...)`, so the model sees the result.
4. Add a test in `tests/CardCrafter.Core.Tests/AssistantTests.cs`. `FakeApi` there plays canned API replies, so a whole
   conversation can be tested without a key.

New blocks need nothing extra. The guide's catalog and `describe_block` read `BlockRegistry`, so the assistant knows about a new
block as soon as it is registered.
