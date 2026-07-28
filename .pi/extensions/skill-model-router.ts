import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { ExtensionAPI, ExtensionContext } from "@earendil-works/pi-coding-agent";
import { CONFIG_DIR_NAME } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";

type ThinkingLevel = "off" | "minimal" | "low" | "medium" | "high" | "xhigh" | "max";
type ResourceKind = "prompt" | "skill";

interface ModelRoute {
	provider: string;
	model: string;
	thinking: ThinkingLevel;
}

interface RouteConfig {
	prompts: Record<string, ModelRoute>;
	skills: Record<string, ModelRoute>;
}

interface RouteResult {
	ok: boolean;
	message: string;
	route?: ModelRoute;
}

const emptyConfig = (): RouteConfig => ({ prompts: {}, skills: {} });

function isRoute(value: unknown): value is ModelRoute {
	if (!value || typeof value !== "object") return false;
	const route = value as Partial<ModelRoute>;
	return (
		typeof route.provider === "string" &&
		typeof route.model === "string" &&
		["off", "minimal", "low", "medium", "high", "xhigh", "max"].includes(route.thinking ?? "")
	);
}

function loadConfig(cwd: string): RouteConfig {
	const path = join(cwd, CONFIG_DIR_NAME, "skill-models.json");
	const parsed = JSON.parse(readFileSync(path, "utf8")) as Partial<RouteConfig>;
	const config = emptyConfig();

	for (const [name, route] of Object.entries(parsed.prompts ?? {})) {
		if (!isRoute(route)) throw new Error(`Invalid prompt model route: ${name}`);
		config.prompts[name] = route;
	}
	for (const [name, route] of Object.entries(parsed.skills ?? {})) {
		if (!isRoute(route)) throw new Error(`Invalid skill model route: ${name}`);
		config.skills[name] = route;
	}

	return config;
}

export default function skillModelRouter(pi: ExtensionAPI) {
	let config = emptyConfig();
	let configError: string | undefined;
	let activeResource: string | undefined;

	function refreshConfig(ctx: ExtensionContext): void {
		try {
			config = loadConfig(ctx.cwd);
			configError = undefined;
		} catch (error) {
			config = emptyConfig();
			configError = error instanceof Error ? error.message : String(error);
			ctx.ui.notify(`Model routing disabled: ${configError}`, "warning");
		}
	}

	function findRoute(name: string, kind?: ResourceKind): { kind: ResourceKind; route: ModelRoute } | undefined {
		if (kind !== "skill" && config.prompts[name]) return { kind: "prompt", route: config.prompts[name] };
		if (kind !== "prompt" && config.skills[name]) return { kind: "skill", route: config.skills[name] };
		return undefined;
	}

	async function applyRoute(name: string, ctx: ExtensionContext, kind?: ResourceKind): Promise<RouteResult> {
		if (configError) return { ok: false, message: `Model route configuration error: ${configError}` };

		const found = findRoute(name, kind);
		if (!found) return { ok: false, message: `No model route configured for ${kind ? `${kind} ` : ""}${name}` };

		const { route } = found;
		const model = ctx.modelRegistry.find(route.provider, route.model);
		if (!model) {
			return { ok: false, message: `Model not found: ${route.provider}/${route.model}`, route };
		}

		const changed = ctx.model?.provider !== route.provider || ctx.model?.id !== route.model;
		if (changed) {
			const success = await pi.setModel(model);
			if (!success) {
				return { ok: false, message: `No authentication available for ${route.provider}/${route.model}`, route };
			}
		}

		pi.setThinkingLevel(route.thinking);
		activeResource = `${found.kind}:${name}`;
		ctx.ui.setStatus("orchestration-route", `${name} · ${route.model} · ${route.thinking}`);

		return {
			ok: true,
			message: `Activated ${activeResource}: ${route.provider}/${route.model} (${route.thinking})`,
			route,
		};
	}

	pi.registerTool({
		name: "activate_orchestration_resource",
		label: "Activate Orchestration Resource",
		description:
			"Switch Pi to the configured provider, model, and thinking level for a gabCode front-door prompt or worker skill. Use before adopting a worker role and to restore pm-agent or team-lead coordination afterward.",
		parameters: Type.Object({
			resource: Type.String({
				description: "Configured resource name, for example team-lead, test-writer, or review-agent",
			}),
		}),
		async execute(_toolCallId, params, _signal, _onUpdate, ctx) {
			const result = await applyRoute(params.resource.trim(), ctx);
			if (!result.ok) throw new Error(result.message);
			return {
				content: [{ type: "text", text: result.message }],
				details: { resource: params.resource.trim(), activeResource, route: result.route },
			};
		},
	});

	pi.on("session_start", async (_event, ctx) => {
		refreshConfig(ctx);
	});

	pi.on("input", async (event, ctx) => {
		const text = event.text.trimStart();
		const skillMatch = text.match(/^\/skill:([a-z0-9-]+)(?:\s|$)/i);
		if (skillMatch && config.skills[skillMatch[1]]) {
			const result = await applyRoute(skillMatch[1], ctx, "skill");
			if (!result.ok) ctx.ui.notify(result.message, "warning");
			return { action: "continue" as const };
		}

		const promptMatch = text.match(/^\/([a-z0-9-]+)(?:\s|$)/i);
		if (promptMatch && config.prompts[promptMatch[1]]) {
			const result = await applyRoute(promptMatch[1], ctx, "prompt");
			if (!result.ok) ctx.ui.notify(result.message, "warning");
		}

		return { action: "continue" as const };
	});

	pi.on("tool_call", async (event, ctx) => {
		if (event.toolName !== "read") return;
		const path = (event.input as { path?: unknown }).path;
		if (typeof path !== "string") return;

		const normalized = path.replace(/^@/, "").replace(/\\/g, "/");
		const match = normalized.match(/(?:^|\/)\.(?:agents|pi)\/skills\/([^/]+)\/SKILL\.md$/i);
		if (!match || !config.skills[match[1]]) return;

		const result = await applyRoute(match[1], ctx, "skill");
		if (!result.ok) ctx.ui.notify(result.message, "warning");
	});
}
