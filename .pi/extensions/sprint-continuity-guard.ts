import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";

let activeSprint: string | undefined;
let queuedContinuation = false;
let agentWasAborted = false;

function extractSprint(text: string): string | undefined {
  return text.match(/execution front door for\s+`?(\d+)`?/i)?.[1]
    ?? text.match(/^\/team-lead\s+(\d+)/i)?.[1];
}

export default function (pi: ExtensionAPI) {
  pi.on("input", (event) => {
    activeSprint = extractSprint(event.text) ?? activeSprint;
    return { action: "continue" };
  });

  pi.on("before_agent_start", (event) => {
    const sprint = extractSprint(event.prompt) ?? activeSprint;
    if (!sprint) return;
    activeSprint = sprint;
    agentWasAborted = false;
    ctx.signal?.addEventListener("abort", () => { agentWasAborted = true; }, { once: true });
    return {
      systemPrompt: `${event.systemPrompt}\n\nSPRINT CONTINUITY GUARD: Sprint #${sprint} is active. Do not end a response while its GitHub issue has unchecked task-board items unless its title begins ✋ and the exact blocker is recorded. Progress belongs in issue comments; continue tool execution immediately.`,
    };
  });

  pi.on("agent_settled", async (_event, ctx) => {
    if (!activeSprint || queuedContinuation || agentWasAborted) return;
    const result = await pi.exec("gh", ["issue", "view", activeSprint, "--json", "title,body", "--jq", "[.title, .body] | @json"], { timeout: 15000 });
    if (result.code !== 0) return;
    try {
      const [title, body] = JSON.parse(result.stdout) as [string, string];
      const taskBoard = body.match(/## 🧩 Task Board[\s\S]*?(?=\n## |$)/u)?.[0] ?? body.match(/## 🧩 [^\n]*[\s\S]*?(?=\n## |$)/u)?.[0] ?? body;
      const incomplete = /- \[ \] /u.test(taskBoard);
      const blocked = /^✋/u.test(title);
      if (!incomplete || blocked) return;
      queuedContinuation = true;
      pi.sendUserMessage(`Continue sprint #${activeSprint} immediately. Unchecked tasks remain and the issue is not blocked. Do not provide a progress response; execute the next required phase.`, { deliverAs: "followUp" });
      queuedContinuation = false;
    } catch {
      // A malformed issue response must never prevent normal Pi use.
    }
  });
}
