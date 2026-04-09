import { useState, useRef, useEffect, useCallback } from "react";

const PERSONAS = [
  { id: "exec", name: "Executive sponsor", style: "brief, strategic, time-pressured", depth: "high-level", traits: ["avoids details", "speaks in outcomes", "name-drops stakeholders", "redirects to ROI"] },
  { id: "mgr", name: "Mid-level manager", style: "cooperative, structured, wants to impress", depth: "moderate detail", traits: ["gives balanced answers", "caveats often", "references team dynamics", "mentions process"] },
  { id: "ic", name: "Individual contributor", style: "candid, sometimes frustrated, detail-oriented", depth: "deep technical", traits: ["shares pain points freely", "gives specific examples", "may go on tangents", "mentions tooling gaps"] },
  { id: "new", name: "New hire (< 3 months)", style: "uncertain, deferential, limited context", depth: "surface only", traits: ["says 'I think' or 'I'm not sure' often", "references onboarding gaps", "compares to previous employer", "asks clarifying questions back"] },
  { id: "skeptic", name: "Skeptical veteran", style: "guarded, tests the agent, challenges premises", depth: "selective", traits: ["pushes back on questions", "gives one-word answers initially", "opens up if trusted", "flags past failed initiatives"] },
  { id: "eager", name: "Enthusiastic champion", style: "verbose, optimistic, wants to help", depth: "exhaustive", traits: ["over-shares", "rates everything positively", "hard to get honest negatives from", "volunteers for follow-ups"] },
];

const RESPONSE_MODES = [
  { id: "realistic", name: "Realistic mix", desc: "Varied lengths, some tangents, occasional 'I don't know'" },
  { id: "adversarial", name: "Adversarial", desc: "Short answers, pushback, N/A-heavy, tests edge cases" },
  { id: "golden", name: "Golden path", desc: "Cooperative, detailed answers that should produce full coverage" },
  { id: "random", name: "Randomized", desc: "Each response randomly varies length, relevance, and cooperation" },
];

function StatusBadge({ status }) {
  const map = {
    idle: { bg: "var(--color-background-tertiary)", color: "var(--color-text-secondary)", label: "Ready" },
    running: { bg: "var(--color-background-info)", color: "var(--color-text-info)", label: "Running" },
    complete: { bg: "var(--color-background-success)", color: "var(--color-text-success)", label: "Complete" },
    error: { bg: "var(--color-background-danger)", color: "var(--color-text-danger)", label: "Error" },
    analyzing: { bg: "var(--color-background-warning)", color: "var(--color-text-warning)", label: "Analyzing" },
  };
  const s = map[status] || map.idle;
  return <span style={{ fontSize: 12, padding: "3px 10px", borderRadius: "var(--border-radius-md)", background: s.bg, color: s.color, fontWeight: 500 }}>{s.label}</span>;
}

function MetricCard({ label, value, sub }) {
  return (
    <div style={{ background: "var(--color-background-secondary)", borderRadius: "var(--border-radius-md)", padding: "0.75rem 1rem", minWidth: 0 }}>
      <div style={{ fontSize: 13, color: "var(--color-text-secondary)", marginBottom: 4 }}>{label}</div>
      <div style={{ fontSize: 22, fontWeight: 500, color: "var(--color-text-primary)" }}>{value}</div>
      {sub && <div style={{ fontSize: 12, color: "var(--color-text-tertiary)", marginTop: 2 }}>{sub}</div>}
    </div>
  );
}

function MessageBubble({ role, content, turn }) {
  const isAgent = role === "agent";
  return (
    <div style={{ display: "flex", gap: 10, marginBottom: 12, flexDirection: isAgent ? "row" : "row-reverse" }}>
      <div style={{
        width: 28, height: 28, borderRadius: "50%", flexShrink: 0, marginTop: 2,
        background: isAgent ? "var(--color-background-info)" : "var(--color-background-warning)",
        color: isAgent ? "var(--color-text-info)" : "var(--color-text-warning)",
        display: "flex", alignItems: "center", justifyContent: "center", fontSize: 11, fontWeight: 500,
      }}>
        {isAgent ? "A" : "R"}
      </div>
      <div style={{
        maxWidth: "78%", padding: "8px 12px", borderRadius: "var(--border-radius-md)",
        border: "0.5px solid var(--color-border-tertiary)",
        background: isAgent ? "var(--color-background-primary)" : "var(--color-background-secondary)",
        fontSize: 13, lineHeight: 1.6, color: "var(--color-text-primary)",
      }}>
        <div style={{ fontSize: 11, color: "var(--color-text-tertiary)", marginBottom: 4 }}>
          {isAgent ? "Discovery agent" : "Test respondent"} · turn {turn}
        </div>
        {content}
      </div>
    </div>
  );
}

function CoverageBar({ covered, total, label }) {
  const pct = total > 0 ? Math.round((covered / total) * 100) : 0;
  return (
    <div style={{ marginBottom: 8 }}>
      <div style={{ display: "flex", justifyContent: "space-between", fontSize: 12, marginBottom: 3 }}>
        <span style={{ color: "var(--color-text-secondary)" }}>{label}</span>
        <span style={{ fontWeight: 500, color: "var(--color-text-primary)" }}>{covered}/{total} ({pct}%)</span>
      </div>
      <div style={{ height: 6, borderRadius: 3, background: "var(--color-background-tertiary)", overflow: "hidden" }}>
        <div style={{ height: "100%", width: `${pct}%`, borderRadius: 3, background: pct === 100 ? "#1D9E75" : pct >= 50 ? "#BA7517" : "#E24B4A", transition: "width 0.4s ease" }} />
      </div>
    </div>
  );
}

const QUESTION_IDS = ["sa-01","sa-02","sa-03","sa-04","eo-01","eo-02","eo-03","eo-04","cp-01","cp-02","cp-03","cp-04","cp-05","cp-06","cm-01","cm-02"];
const SECTIONS = { "strategy-alignment": [0,1,2,3], "execution-operations": [4,5,6,7], "culture-people": [8,9,10,11,12,13], "change-management": [14,15] };
const SECTION_NAMES = { "strategy-alignment": "Strategy & alignment", "execution-operations": "Execution & operations", "culture-people": "Culture & people", "change-management": "Change management" };

function generateSimulatedConversation(persona, mode, questionnaireLength) {
  const messages = [];
  const covered = new Set();
  let turn = 0;

  const agentOpeners = [
    "Thanks for taking the time to chat. I'd love to understand how your team operates day-to-day. Let's start with strategy — when leadership talks about brand strategy, how does that actually reach your team?",
    "Appreciate you being here. I want to get a real picture of how things work — not the org chart version. Let's start with how your team thinks about priorities and strategy.",
    "Hey, thanks for doing this. I'll keep it conversational — no checkboxes. Let me start by asking: when someone says 'brand strategy' in your org, what does that actually mean at the team level?",
  ];

  messages.push({ role: "agent", content: agentOpeners[Math.floor(Math.random() * agentOpeners.length)] });
  turn++;

  const sectionOrder = Object.keys(SECTIONS);
  for (const section of sectionOrder) {
    const qIds = SECTIONS[section];
    const sectionName = SECTION_NAMES[section];

    if (turn > 1) {
      const transitions = [
        `That's really helpful context. Let me shift gears to ${sectionName.toLowerCase()} — `,
        `Great, I've got a good picture there. Moving on to ${sectionName.toLowerCase()}: `,
        `Noted. I want to explore ${sectionName.toLowerCase()} next. `,
      ];
      const transitionProbes = {
        "strategy-alignment": "How does the team connect what it's doing day-to-day to the bigger brand goals?",
        "execution-operations": "Walk me through how work actually flows — from 'someone has an idea' to 'it's done and approved.'",
        "culture-people": "Let's talk about the people side. How clear are roles on your team — does everyone know what's theirs to own?",
        "change-management": "Last area — when leadership decides something needs to change, how does that land with the team?",
      };
      messages.push({ role: "agent", content: transitions[Math.floor(Math.random() * transitions.length)] + transitionProbes[section] });
      turn++;
    }

    for (let i = 0; i < qIds.length; i++) {
      const qIdx = qIds[i];
      const qId = QUESTION_IDS[qIdx];
      let response = "";

      if (mode === "adversarial") {
        const adversarial = ["Not really.", "I'd have to think about that.", "That's not really applicable to us.", "I guess? Sometimes?", "Why do you need to know that?", "Hmm, I don't think I'm the right person to answer that."];
        response = adversarial[Math.floor(Math.random() * adversarial.length)];
        if (Math.random() > 0.4) covered.add(qId);
      } else if (mode === "golden") {
        const golden = [
          "Yes, absolutely. We have a weekly sync where the brand lead walks through priorities and connects them to what we're working on. It's been consistent for about 6 months now.",
          "We've gotten much better at this. Our OKRs are set quarterly and we review them bi-weekly. Not perfect, but the team knows what we're aiming for.",
          "Honestly, this is one of our strengths. Everyone has a clear RACI and we revisit it when projects shift. New people get a walkthrough in their first week.",
          "It happens but inconsistently. Some quarters we're great at it, other times it falls off when things get busy. I'd say occasionally.",
          "We've tried a few times but it never sticks. The backlog exists in theory but nobody really looks at it week to week.",
          "That's a great question. Yes — we use Tableau dashboards and the leadership team reviews them monthly. Decisions get documented in Confluence with the data that supported them.",
        ];
        response = golden[Math.floor(Math.random() * golden.length)];
        covered.add(qId);
      } else if (mode === "random") {
        if (Math.random() > 0.3) {
          response = persona.traits[Math.floor(Math.random() * persona.traits.length)] + ". " + (Math.random() > 0.5 ? "I think we do that sometimes but it's not consistent." : "Yeah, we're pretty good at that actually.");
          covered.add(qId);
        } else {
          response = "I'm not sure about that one.";
        }
      } else {
        const realistic = [
          `As a ${persona.name.toLowerCase()}, ${persona.traits[0]}. I'd say we do this occasionally — it depends on the quarter and who's driving it.`,
          "Yeah, I think so. We have the structures in place but execution varies. Some teams are great, others less so.",
          "That's actually something I've been thinking about. We recently changed how we do this and it's still settling in. I'd say we're in transition.",
          "Honestly? No. I wish we were better at this. It comes up in retros but nothing changes.",
          "This is one of our bright spots. The team lead is really intentional about it and it shows.",
        ];
        response = realistic[Math.floor(Math.random() * realistic.length)];
        if (Math.random() > 0.2) covered.add(qId);
      }

      messages.push({ role: "respondent", content: response });
      turn++;

      if (i < qIds.length - 1 && Math.random() > 0.3) {
        const probes = [
          "Can you give me a specific example of that?",
          "Interesting — and when it doesn't work, what usually goes wrong?",
          "How long has it been that way? Was there a moment when it shifted?",
          "And does the rest of the team see it the same way, or is that your take?",
          "Got it. And how does that connect to how decisions get made?",
        ];
        messages.push({ role: "agent", content: probes[Math.floor(Math.random() * probes.length)] });
        turn++;
      }
    }

    messages.push({
      role: "agent",
      content: `[Section complete: ${sectionName}] I've got a good picture of this area. Let me capture what I've heard before we move on.`,
    });
    turn++;
  }

  messages.push({
    role: "agent",
    content: "Thanks so much — that was incredibly helpful. Let me give you a quick summary of what I heard: your team has real strengths around " +
      (covered.size > 12 ? "several areas" : "a few areas") +
      " but there are some gaps worth addressing, particularly around consistency. Does that resonate, or did I miss anything?",
  });

  return { messages, covered, totalQuestions: QUESTION_IDS.length };
}

function generateAnalysis(runs) {
  const issues = [];
  const suggestions = [];

  const avgCoverage = runs.reduce((s, r) => s + r.covered.size, 0) / runs.length;
  const avgPct = Math.round((avgCoverage / QUESTION_IDS.length) * 100);

  if (avgPct < 70) {
    issues.push(`Average coverage is only ${avgPct}% across ${runs.length} runs. The agent is consistently missing questions.`);
    suggestions.push("Add explicit instructions in agentInstructions to track which question IDs have been addressed and flag uncovered ones before wrapping a section.");
  }

  const perQuestion = {};
  QUESTION_IDS.forEach(q => { perQuestion[q] = 0; });
  runs.forEach(r => r.covered.forEach(q => { perQuestion[q]++; }));
  const weakQuestions = Object.entries(perQuestion).filter(([, c]) => c < runs.length * 0.5);
  if (weakQuestions.length > 0) {
    issues.push(`${weakQuestions.length} questions were covered in fewer than half of runs: ${weakQuestions.map(([q]) => q).join(", ")}.`);
    suggestions.push("For consistently missed questions, add explicit probing prompts in the agentInstructions keyed to those question IDs. Example: 'If sa-04 (OGSTMA translation) has not been addressed, ask specifically about how strategic frameworks connect to daily work.'");
  }

  const adversarialRuns = runs.filter(r => r.mode === "adversarial");
  if (adversarialRuns.length > 0) {
    const advAvg = Math.round(adversarialRuns.reduce((s, r) => s + r.covered.size, 0) / adversarialRuns.length);
    if (advAvg < QUESTION_IDS.length * 0.6) {
      issues.push(`Adversarial runs averaged only ${advAvg}/${QUESTION_IDS.length} coverage. The agent may not be probing enough when respondents give short or dismissive answers.`);
      suggestions.push("Add instructions for handling resistant respondents: 'If a respondent gives a one-word answer or says N/A, probe once with a reframed question before accepting. Example: If they say capacity planning doesn't apply, ask how the team decides who works on what when multiple deadlines compete.'");
    }
  }

  const avgTurns = Math.round(runs.reduce((s, r) => s + r.messages.length, 0) / runs.length);
  if (avgTurns < 20) {
    issues.push(`Average conversation length is only ${avgTurns} turns. The agent may be rushing through sections.`);
    suggestions.push("Increase the minimum turn guidance in agentInstructions from '5-7 turns per theme' to '7-10 turns per theme' and add: 'Do not call complete_questionnaire_section until you have at least one follow-up exchange per question in the section.'");
  } else if (avgTurns > 60) {
    issues.push(`Average conversation length is ${avgTurns} turns, which may cause respondent fatigue.`);
    suggestions.push("Add pacing guidance: 'If the conversation exceeds 50 turns, begin consolidating remaining questions into broader prompts that cover multiple statements at once.'");
  }

  if (issues.length === 0) {
    issues.push("No significant issues detected across test runs. Coverage is strong and consistent.");
    suggestions.push("Consider running adversarial-mode tests to stress-test edge cases.");
  }

  return { issues, suggestions, avgPct, avgTurns, weakQuestions: weakQuestions.map(([q]) => q) };
}

export default function DiscoveryTestHarness() {
  const [tab, setTab] = useState("config");
  const [persona, setPersona] = useState(PERSONAS[0]);
  const [mode, setMode] = useState(RESPONSE_MODES[0]);
  const [numRuns, setNumRuns] = useState(3);
  const [status, setStatus] = useState("idle");
  const [runs, setRuns] = useState([]);
  const [activeRun, setActiveRun] = useState(null);
  const [visibleMessages, setVisibleMessages] = useState(0);
  const [analysis, setAnalysis] = useState(null);
  const [stampTarget, setStampTarget] = useState("local");
  const scrollRef = useRef(null);
  const timerRef = useRef(null);

  const startTest = useCallback(() => {
    setStatus("running");
    setTab("watch");
    setRuns([]);
    setAnalysis(null);
    setActiveRun(null);
    setVisibleMessages(0);

    const allRuns = [];
    for (let i = 0; i < numRuns; i++) {
      const p = numRuns > 1 ? PERSONAS[i % PERSONAS.length] : persona;
      const m = numRuns > 1 && mode.id === "random" ? RESPONSE_MODES[Math.floor(Math.random() * RESPONSE_MODES.length)] : mode;
      const result = generateSimulatedConversation(p, m.id, QUESTION_IDS.length);
      allRuns.push({ ...result, persona: p, mode: m.id, runIndex: i });
    }

    setRuns(allRuns);
    setActiveRun(0);
    setVisibleMessages(0);

    let msgIdx = 0;
    const totalMsgs = allRuns[0].messages.length;
    timerRef.current = setInterval(() => {
      msgIdx++;
      setVisibleMessages(msgIdx);
      if (msgIdx >= totalMsgs) {
        clearInterval(timerRef.current);
        if (allRuns.length > 1) {
          setTimeout(() => {
            setStatus("analyzing");
            setTimeout(() => {
              setAnalysis(generateAnalysis(allRuns));
              setStatus("complete");
              setTab("results");
            }, 800);
          }, 600);
        } else {
          setStatus("analyzing");
          setTimeout(() => {
            setAnalysis(generateAnalysis(allRuns));
            setStatus("complete");
            setTab("results");
          }, 800);
        }
      }
    }, 220);
  }, [persona, mode, numRuns]);

  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [visibleMessages]);

  useEffect(() => {
    return () => { if (timerRef.current) clearInterval(timerRef.current); };
  }, []);

  const currentRun = activeRun !== null && runs[activeRun] ? runs[activeRun] : null;

  return (
    <div style={{ fontFamily: "var(--font-sans)", color: "var(--color-text-primary)" }}>
      <div style={{ display: "flex", gap: 2, marginBottom: 16, borderBottom: "0.5px solid var(--color-border-tertiary)", paddingBottom: 8 }}>
        {[
          { id: "config", label: "Configure" },
          { id: "watch", label: "Watch" },
          { id: "results", label: "Results" },
        ].map(t => (
          <button key={t.id} onClick={() => setTab(t.id)} style={{
            padding: "6px 16px", fontSize: 13, fontWeight: tab === t.id ? 500 : 400, cursor: "pointer",
            background: tab === t.id ? "var(--color-background-secondary)" : "transparent",
            border: "none", borderRadius: "var(--border-radius-md)",
            color: tab === t.id ? "var(--color-text-primary)" : "var(--color-text-secondary)",
          }}>
            {t.label}
          </button>
        ))}
        <div style={{ marginLeft: "auto", display: "flex", alignItems: "center", gap: 8 }}>
          <StatusBadge status={status} />
        </div>
      </div>

      {tab === "config" && (
        <div>
          <div style={{ marginBottom: 20 }}>
            <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 8 }}>Test persona</div>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))", gap: 8 }}>
              {PERSONAS.map(p => (
                <button key={p.id} onClick={() => setPersona(p)} style={{
                  textAlign: "left", padding: "10px 12px", cursor: "pointer",
                  border: persona.id === p.id ? "2px solid var(--color-border-info)" : "0.5px solid var(--color-border-tertiary)",
                  borderRadius: "var(--border-radius-md)", background: "var(--color-background-primary)",
                }}>
                  <div style={{ fontSize: 13, fontWeight: 500, color: "var(--color-text-primary)" }}>{p.name}</div>
                  <div style={{ fontSize: 11, color: "var(--color-text-secondary)", marginTop: 2 }}>{p.style}</div>
                </button>
              ))}
            </div>
          </div>

          <div style={{ marginBottom: 20 }}>
            <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 8 }}>Response mode</div>
            <div style={{ display: "grid", gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))", gap: 8 }}>
              {RESPONSE_MODES.map(m => (
                <button key={m.id} onClick={() => setMode(m)} style={{
                  textAlign: "left", padding: "10px 12px", cursor: "pointer",
                  border: mode.id === m.id ? "2px solid var(--color-border-info)" : "0.5px solid var(--color-border-tertiary)",
                  borderRadius: "var(--border-radius-md)", background: "var(--color-background-primary)",
                }}>
                  <div style={{ fontSize: 13, fontWeight: 500, color: "var(--color-text-primary)" }}>{m.name}</div>
                  <div style={{ fontSize: 11, color: "var(--color-text-secondary)", marginTop: 2 }}>{m.desc}</div>
                </button>
              ))}
            </div>
          </div>

          <div style={{ marginBottom: 20 }}>
            <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 8 }}>Target stamp</div>
            <div style={{ display: "flex", gap: 8 }}>
              {[
                { id: "local", label: "Local (simulated)", desc: "No API calls — fast, for validating coverage" },
                { id: "dev", label: "Dev stamp", desc: "Calls your dev Discovery Bot API" },
                { id: "new", label: "New stamp", desc: "Provision a test stamp, run, tear down" },
              ].map(s => (
                <button key={s.id} onClick={() => setStampTarget(s.id)} style={{
                  flex: 1, textAlign: "left", padding: "10px 12px", cursor: "pointer",
                  border: stampTarget === s.id ? "2px solid var(--color-border-info)" : "0.5px solid var(--color-border-tertiary)",
                  borderRadius: "var(--border-radius-md)", background: "var(--color-background-primary)",
                }}>
                  <div style={{ fontSize: 13, fontWeight: 500, color: "var(--color-text-primary)" }}>{s.label}</div>
                  <div style={{ fontSize: 11, color: "var(--color-text-secondary)", marginTop: 2 }}>{s.desc}</div>
                </button>
              ))}
            </div>
          </div>

          <div style={{ marginBottom: 20 }}>
            <div style={{ display: "flex", alignItems: "center", gap: 12 }}>
              <label style={{ fontSize: 14, fontWeight: 500 }}>Number of runs</label>
              <input type="range" min="1" max="10" value={numRuns} onChange={e => setNumRuns(parseInt(e.target.value))} style={{ flex: 1, maxWidth: 200 }} />
              <span style={{ fontSize: 14, fontWeight: 500, minWidth: 24 }}>{numRuns}</span>
            </div>
            <div style={{ fontSize: 12, color: "var(--color-text-tertiary)", marginTop: 4 }}>
              Multiple runs cycle through personas automatically for broader coverage testing
            </div>
          </div>

          <button onClick={startTest} style={{
            padding: "10px 24px", fontSize: 14, fontWeight: 500, cursor: "pointer",
            background: "var(--color-background-info)", color: "var(--color-text-info)",
            border: "none", borderRadius: "var(--border-radius-md)",
          }}>
            Run test{numRuns > 1 ? `s (${numRuns} runs)` : ""} ↗
          </button>
        </div>
      )}

      {tab === "watch" && (
        <div>
          {currentRun && (
            <>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 8, marginBottom: 16 }}>
                <MetricCard label="Persona" value={currentRun.persona.name} />
                <MetricCard label="Mode" value={currentRun.mode} />
                <MetricCard label="Turns" value={Math.min(visibleMessages, currentRun.messages.length)} sub={`of ${currentRun.messages.length}`} />
                <MetricCard label="Coverage" value={`${currentRun.covered.size}/${currentRun.totalQuestions}`} sub={`${Math.round(currentRun.covered.size / currentRun.totalQuestions * 100)}%`} />
              </div>

              <div style={{ marginBottom: 12 }}>
                {Object.entries(SECTIONS).map(([sec, qIds]) => {
                  const coveredInSec = qIds.filter(i => currentRun.covered.has(QUESTION_IDS[i])).length;
                  return <CoverageBar key={sec} label={SECTION_NAMES[sec]} covered={coveredInSec} total={qIds.length} />;
                })}
              </div>

              <div ref={scrollRef} style={{
                maxHeight: 400, overflowY: "auto", padding: 12,
                border: "0.5px solid var(--color-border-tertiary)", borderRadius: "var(--border-radius-lg)",
                background: "var(--color-background-secondary)",
              }}>
                {currentRun.messages.slice(0, visibleMessages).map((m, i) => (
                  <MessageBubble key={i} role={m.role} content={m.content} turn={i + 1} />
                ))}
                {status === "running" && visibleMessages < currentRun.messages.length && (
                  <div style={{ textAlign: "center", padding: 8, fontSize: 12, color: "var(--color-text-tertiary)" }}>
                    Agent is thinking...
                  </div>
                )}
              </div>

              {runs.length > 1 && (
                <div style={{ display: "flex", gap: 4, marginTop: 12, flexWrap: "wrap" }}>
                  {runs.map((r, i) => (
                    <button key={i} onClick={() => { setActiveRun(i); setVisibleMessages(r.messages.length); }} style={{
                      padding: "4px 12px", fontSize: 12, cursor: "pointer",
                      border: activeRun === i ? "2px solid var(--color-border-info)" : "0.5px solid var(--color-border-tertiary)",
                      borderRadius: "var(--border-radius-md)", background: "var(--color-background-primary)",
                      color: "var(--color-text-primary)",
                    }}>
                      Run {i + 1}: {r.persona.name.split(" ")[0]}
                    </button>
                  ))}
                </div>
              )}
            </>
          )}
          {!currentRun && (
            <div style={{ textAlign: "center", padding: 40, color: "var(--color-text-tertiary)", fontSize: 14 }}>
              Configure and start a test to watch the conversation flow
            </div>
          )}
        </div>
      )}

      {tab === "results" && (
        <div>
          {analysis ? (
            <>
              <div style={{ display: "grid", gridTemplateColumns: "repeat(4, 1fr)", gap: 8, marginBottom: 20 }}>
                <MetricCard label="Runs completed" value={runs.length} />
                <MetricCard label="Avg. coverage" value={`${analysis.avgPct}%`} sub={analysis.avgPct >= 90 ? "Strong" : analysis.avgPct >= 70 ? "Acceptable" : "Needs work"} />
                <MetricCard label="Avg. turns" value={analysis.avgTurns} sub={analysis.avgTurns > 60 ? "May cause fatigue" : "Good pacing"} />
                <MetricCard label="Weak questions" value={analysis.weakQuestions.length} sub={analysis.weakQuestions.length === 0 ? "None found" : "See below"} />
              </div>

              <div style={{ marginBottom: 20 }}>
                <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 8 }}>Coverage by run</div>
                {runs.map((r, i) => (
                  <CoverageBar key={i} label={`Run ${i + 1}: ${r.persona.name} (${r.mode})`} covered={r.covered.size} total={r.totalQuestions} />
                ))}
              </div>

              <div style={{
                padding: 16, borderRadius: "var(--border-radius-lg)",
                border: "0.5px solid var(--color-border-tertiary)", background: "var(--color-background-primary)",
                marginBottom: 16,
              }}>
                <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 12, color: "var(--color-text-primary)" }}>Issues detected</div>
                {analysis.issues.map((issue, i) => (
                  <div key={i} style={{
                    padding: "8px 12px", marginBottom: 6, borderRadius: "var(--border-radius-md)",
                    background: "var(--color-background-danger)", color: "var(--color-text-danger)", fontSize: 13,
                    border: "0.5px solid var(--color-border-tertiary)",
                  }}>
                    {issue}
                  </div>
                ))}
              </div>

              <div style={{
                padding: 16, borderRadius: "var(--border-radius-lg)",
                border: "0.5px solid var(--color-border-tertiary)", background: "var(--color-background-primary)",
                marginBottom: 16,
              }}>
                <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 12, color: "var(--color-text-primary)" }}>
                  Recommended agentInstructions changes
                </div>
                {analysis.suggestions.map((s, i) => (
                  <div key={i} style={{
                    padding: "8px 12px", marginBottom: 6, borderRadius: "var(--border-radius-md)",
                    background: "var(--color-background-success)", color: "var(--color-text-success)", fontSize: 13,
                    border: "0.5px solid var(--color-border-tertiary)",
                  }}>
                    {s}
                  </div>
                ))}
              </div>

              {analysis.weakQuestions.length > 0 && (
                <div style={{
                  padding: 16, borderRadius: "var(--border-radius-lg)",
                  border: "0.5px solid var(--color-border-tertiary)", background: "var(--color-background-primary)",
                }}>
                  <div style={{ fontSize: 14, fontWeight: 500, marginBottom: 8 }}>Consistently missed questions</div>
                  <div style={{ display: "flex", gap: 6, flexWrap: "wrap" }}>
                    {analysis.weakQuestions.map(q => (
                      <span key={q} style={{
                        fontSize: 12, padding: "4px 10px", borderRadius: "var(--border-radius-md)",
                        background: "var(--color-background-warning)", color: "var(--color-text-warning)", fontWeight: 500,
                      }}>
                        {q}
                      </span>
                    ))}
                  </div>
                </div>
              )}

              <button onClick={() => {
                const report = {
                  timestamp: new Date().toISOString(),
                  runs: runs.map(r => ({ persona: r.persona.name, mode: r.mode, coverage: r.covered.size, totalQuestions: r.totalQuestions, coveredIds: [...r.covered] })),
                  analysis,
                };
                sendPrompt(`Here are the test harness results. Please review and suggest specific changes to the agentInstructions in context-team-health-merck-001.json:\n\n${JSON.stringify(report, null, 2)}`);
              }} style={{
                marginTop: 16, padding: "10px 20px", fontSize: 13, fontWeight: 500, cursor: "pointer",
                background: "transparent", border: "0.5px solid var(--color-border-secondary)",
                borderRadius: "var(--border-radius-md)", color: "var(--color-text-primary)",
              }}>
                Send results to Claude for context refinement ↗
              </button>
            </>
          ) : (
            <div style={{ textAlign: "center", padding: 40, color: "var(--color-text-tertiary)", fontSize: 14 }}>
              {status === "analyzing" ? "Analyzing runs..." : "Run a test first to see results"}
            </div>
          )}
        </div>
      )}
    </div>
  );
}
