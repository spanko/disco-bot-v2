# Test Harness — Claude Code Build Instructions

## Context

We have a working JSX prototype (attached as `discovery-test-harness.jsx`) that demonstrates the UX for a Discovery Bot test harness. The prototype uses simulated conversations client-side. The goal is to build this into the disco-bot-v2 repo as a real test tool that runs live LLM-to-LLM conversations against the Discovery Bot API.

## What to Build

### 1. `web/test.html` — Test Harness UI

A new page in the existing web UI (alongside `index.html` and `admin.html`). Link it from the header nav as "Test". This is a vanilla HTML/CSS/JS page (no React — match the existing web UI pattern). Use the JSX prototype as the design reference for layout, tabs, and interaction flow.

Three tabs: **Configure**, **Watch**, **Results**.

### 2. Test Runner Architecture

The test runner orchestrates a conversation between two LLMs:

**Agent side:** The real Discovery Bot API (`POST /api/conversation`). Each test run creates a real conversation with a real contextId, so the agent behaves exactly as it would with a human respondent.

**Respondent side:** A separate LLM call (via the Foundry Responses API or a lightweight endpoint) that plays the role of a test respondent. The respondent LLM gets a system prompt that defines its persona, response mode, and the questionnaire it's being assessed on — so it can give realistic answers without knowing the "right" answers.

#### Respondent LLM System Prompt Template

```
You are simulating a survey respondent for a team health assessment.

**Your persona:** {persona.name}
- Communication style: {persona.style}
- Depth of answers: {persona.depth}
- Behavioral traits: {persona.traits}

**Response mode:** {mode.name}
- {mode.description}

**Rules:**
- Stay in character for the entire conversation.
- Never reveal you are an AI or a test respondent.
- Answer based on a fictional but plausible team scenario.
- If the agent asks a question you can't answer in character, say so naturally (e.g., "I'm not sure, I'd have to check with my manager").
- Match your response length to your persona — executives are brief, ICs give detail, skeptics push back.
- If in adversarial mode, give short answers, say "N/A" or "I don't know" frequently, and challenge the agent's questions.
- If in golden path mode, give thorough, cooperative answers with specific examples.
- If in randomized mode, vary your cooperation, length, and specificity randomly across responses.
```

#### Conversation Loop

```
1. POST /api/conversation { userId: "test-runner", contextId: selectedContextId, message: "Hi, I'm ready to start." }
2. Agent responds with opening questions
3. Send agent response to respondent LLM → get respondent reply
4. POST /api/conversation { userId: "test-runner", conversationId: id, message: respondentReply }
5. Agent responds
6. Repeat steps 3-5 until agent signals completion or maxTurns reached
7. Extract coverage data from the conversation's knowledge items
```

### 3. New API Endpoint: `POST /api/test/run`

Add a test orchestration endpoint that handles the loop server-side (avoids CORS issues and keeps the respondent LLM call on the backend). This also enables batch runs.

**Request:**
```json
{
  "contextId": "team-health-merck-001",
  "questionnaireId": "team-health-observability",
  "persona": {
    "name": "Skeptical veteran",
    "style": "guarded, tests the agent, challenges premises",
    "depth": "selective",
    "traits": ["pushes back on questions", "gives one-word answers initially"]
  },
  "responseMode": "adversarial",
  "maxTurns": 60,
  "streamUpdates": true
}
```

**Response (streamed via SSE if `streamUpdates: true`):**
Each event is a turn:
```json
{
  "type": "turn",
  "turnNumber": 5,
  "role": "agent|respondent",
  "content": "...",
  "extractedKnowledgeIds": ["..."],
  "coveredQuestionIds": ["sa-01", "sa-02"]
}
```

Final event:
```json
{
  "type": "complete",
  "totalTurns": 34,
  "coveredQuestionIds": ["sa-01", "sa-02", ...],
  "missedQuestionIds": ["sa-04", "cp-05"],
  "coveragePercent": 87,
  "conversationId": "..."
}
```

### 4. Coverage Detection

The agent already tags knowledge items with question IDs (e.g., `tags: ['maturity:yes', 'sa-01']`) per the agentInstructions. After each agent turn, query the knowledge items for the conversation and check which question IDs have been tagged. This gives real-time coverage tracking.

Add a helper: `GET /api/test/coverage/{conversationId}` that returns:
```json
{
  "questionnaireId": "team-health-observability",
  "coveredIds": ["sa-01", "sa-02", "eo-01"],
  "missedIds": ["sa-03", "sa-04", ...],
  "scores": {
    "sa-01": { "maturity": "yes", "evidence": "..." },
    "sa-02": { "maturity": "occasionally", "evidence": "..." }
  },
  "sectionSummaries": {
    "strategy-alignment": { "covered": 2, "total": 4, "percent": 50 }
  }
}
```

### 5. Batch Run + Analysis

`POST /api/test/batch` accepts:
```json
{
  "contextId": "team-health-merck-001",
  "questionnaireId": "team-health-observability",
  "runs": [
    { "persona": "exec", "responseMode": "realistic" },
    { "persona": "skeptic", "responseMode": "adversarial" },
    { "persona": "ic", "responseMode": "golden" }
  ],
  "maxTurns": 60
}
```

Runs execute sequentially (to avoid rate limits). Returns aggregate analysis:
- Per-question coverage frequency across runs
- Consistently missed questions (covered in < 50% of runs)
- Average conversation length
- Recommended agentInstructions changes (use a separate LLM call that takes the batch results + current agentInstructions and suggests improvements)

### 6. Stamp Targeting

The test UI should support three targets:

- **Local (current stamp):** Calls the same API the test page is hosted on. Default.
- **Dev stamp:** Configurable URL field. Calls a different stamp's API.
- **New stamp:** Calls the management plane's provisioning API to create a test stamp, waits for it to be ready, runs the test suite, then calls the management plane to tear it down. This requires the management plane to be operational. For now, just stub the UI with a "Coming soon — requires management plane" message.

### 7. Personas

Include these six built-in personas (stored client-side, selectable in the UI):

1. **Executive sponsor** — brief, strategic, time-pressured. Avoids details, speaks in outcomes, name-drops stakeholders, redirects to ROI.
2. **Mid-level manager** — cooperative, structured, wants to impress. Gives balanced answers, caveats often, references team dynamics.
3. **Individual contributor** — candid, sometimes frustrated, detail-oriented. Shares pain points freely, gives specific examples, may go on tangents.
4. **New hire (< 3 months)** — uncertain, deferential, limited context. Says "I think" often, references onboarding gaps, compares to previous employer.
5. **Skeptical veteran** — guarded, tests the agent, challenges premises. Pushes back, gives one-word answers initially, opens up if trusted.
6. **Enthusiastic champion** — verbose, optimistic, wants to help. Over-shares, rates everything positively, hard to get honest negatives from.

Also support custom personas via a JSON text field.

### 8. Response Modes

1. **Realistic mix** — Varied lengths, some tangents, occasional "I don't know"
2. **Adversarial** — Short answers, pushback, N/A-heavy, tests edge cases
3. **Golden path** — Cooperative, detailed answers that should produce full coverage
4. **Randomized** — Each response randomly varies length, relevance, and cooperation

## Implementation Notes

- The respondent LLM should use the same Foundry project and model deployment as the agent, but with a different agent definition (or just a direct Responses API call with no agent — just a system prompt and the conversation so far).
- Each test run should use a unique userId like `test-runner-{timestamp}` so test data doesn't pollute real user profiles.
- Add a `isTestRun: true` flag to the conversation metadata in Cosmos so test conversations can be filtered out of production dashboards.
- The SSE streaming for the watch tab should use the same pattern as the chat UI's streaming (if it exists) or a simple EventSource connection.
- Auth: The test endpoints should require the same API key as admin endpoints. In the future, gate behind `Platform.Operator` Entra role.

## Files to Create/Modify

- **Create:** `web/test.html` — Test harness UI
- **Create:** `src/DiscoveryAgent/Endpoints/TestEndpoints.cs` — Test API endpoints
- **Create:** `src/DiscoveryAgent/Services/TestRunnerService.cs` — Orchestrates respondent LLM + agent conversation loop
- **Create:** `src/DiscoveryAgent/Services/CoverageAnalyzer.cs` — Queries knowledge items, computes coverage
- **Modify:** `src/DiscoveryAgent/Program.cs` — Register test endpoints
- **Modify:** `web/index.html` — Add "Test" link to nav header
- **Modify:** `web/admin.html` — Add "Test" link to nav header

## Design Reference

Use `discovery-test-harness.jsx` as the visual reference. The prototype demonstrates:
- Tab layout (Configure / Watch / Results)
- Persona and response mode selection cards
- Live message streaming with agent/respondent bubbles
- Coverage bars per questionnaire section
- Metric cards for summary stats
- Analysis output with issues and recommendations
- "Send to Claude" button pattern for context refinement

Translate from React to vanilla HTML/JS matching the existing web UI style (the admin and chat pages use simple fetch + DOM manipulation, no framework).
