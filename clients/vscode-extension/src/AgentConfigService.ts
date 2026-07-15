import * as vscode from 'vscode';

// 'claude-cli'/'codex-cli' are not HTTP APIs — each routes the role to the server's matching
// IHarnessExecutor (ClaudeCodeExecutor / CodexCliExecutor, spawning the local `claude`/`codex`
// binary). No baseUrl; API key optional (blank = the machine's ambient CLI login; stored key =
// injected as ANTHROPIC_API_KEY / OPENAI_API_KEY respectively for headless use).
export type LlmProvider = 'anthropic' | 'openai' | 'vscode-lm' | 'claude-cli' | 'codex-cli';

// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — the set of
// providers that route to a local CLI adapter rather than an HTTP API, kept as one array so a
// third CLI adapter (Copilot CLI, …) needs a one-line change here instead of hunting down every
// `=== 'claude-cli'` check across this file, ArtifactExplorerPanel.ts, and modelAgentStudio.js.
const CLI_PROVIDERS: readonly LlmProvider[] = ['claude-cli', 'codex-cli'];

export function isCliProvider(provider: string | undefined): boolean {
  return provider !== undefined && (CLI_PROVIDERS as readonly string[]).includes(provider);
}

/** Human-readable label for a CLI provider's own harness, for error messages. */
function cliDisplayName(provider: string | undefined): string {
  return provider === 'codex-cli' ? 'Codex CLI' : 'Claude Code CLI';
}

export type DeploymentMode = 'inline' | 'headless';

export interface AgentProfile {
  id:               string;
  label:            string;
  domain:           string;
  deploymentMode?:  DeploymentMode;   // defaults to 'inline'
  provider?:        LlmProvider;
  model?:           string;
  baseUrl?:         string;
  apiKeyRef?:       string;
  systemPrompt?:    string;
  tools?:           string[];         // MCP tool allowlist; empty = all permitted
  // 'strict' (default/undefined) honors only the structured tool_calls field. 'lenient' additionally
  // tolerates a model that emits its tool call as message text / a ```json fence — the failure mode
  // of small quantized local models (e.g. qwen2.5-coder:7b via Ollama). Opt-in per profile so normal
  // profiles, and any content-producing worker, are unaffected. See LlmClient.SendOpenAiAsync.
  toolCallParsing?: 'strict' | 'lenient';
  /** @deprecated Use systemPrompt */
  systemPromptHint?: string;
}

export interface TopologyTemplate {
  name:         string;
  // The goal's Default profile — displayed as "Default" everywhere since
  // plans/orchestrator-pure-service.md M3 (the orchestrator LLM role no longer exists; this is
  // purely the credential anchor every unset role inherits). The field name stays `orchestrator`
  // on the wire so stored templates keep round-tripping.
  orchestrator: string;
  // Optional per-stage credential profile overrides — when unset, that stage inherits the
  // Default profile. Profile ids reference entries from getProfiles().
  planner?:     string;
  worker?:      string;
  reviewer?:    string;
  // Profile used to spawn the reconciliation agent when a candidate-branch or task-level conflict
  // is Reconciled. Distinct from all three stage roles above: reconciliation work units aren't
  // part of any goal's own Plan/Execute/Review pipeline, so there's no natural "inherit" fallback
  // among them — falls back to the Default profile only if unset, same as the others.
  reconciler?:  string;
}

/** Runtime participant from GET /studio/participants — covers both in-process agents and room peers. */
export interface ParticipantStatus {
  id:              string;
  kind:            'agent' | 'peer';
  status:          string;
  workUnitId?:     string | null;
  currentActivity?: string | null;
  peerType?:       string | null;
}

/** LLM connection fields passed to POST /studio/agents/spawn. */
export interface SpawnLlmConfig {
  provider: string;
  model:    string;
  baseUrl:  string;
  apiKey:   string;
  // Opaque cache key the server uses to re-resolve these connection details after a restart,
  // instead of ever persisting apiKey itself — see IRuntimeCredentialCache's doc comment on the
  // server side. Never a secret; safe to log or display.
  credentialRef: string;
  // Mirrors the profile's toolCallParsing === 'lenient'. Rides the per-stage credential into the
  // server's StageCredentialDto → GoalDefaultCredentials.LenientToolParsing. Omitted when false.
  lenientToolParsing?: boolean;
}

// The out-of-the-box Default profile is vscode-lm on purpose: a fresh install can start a goal
// with zero credential setup inside VS Code (plans/orchestrator-pure-service.md, resolved
// question 2). The id stays 'orchestrator' for stored-config round-tripping.
const DEFAULT_PROFILES: AgentProfile[] = [
  { id: 'orchestrator', label: 'Default', domain: 'orchestration', provider: 'vscode-lm', model: '' },
  { id: 'worker',       label: 'Worker',  domain: 'general',       provider: 'vscode-lm', model: '' },
];

const DEFAULT_TEMPLATES: TopologyTemplate[] = [
  { name: 'Default', orchestrator: 'orchestrator', worker: 'worker' },
];

export class AgentConfigService {
  getProfiles(): AgentProfile[] {
    const cfg    = vscode.workspace.getConfiguration('nodalmerge');
    const stored = cfg.get<AgentProfile[]>('agentProfiles') ?? [];
    return stored.length > 0 ? stored : [...DEFAULT_PROFILES];
  }

  getTemplates(): TopologyTemplate[] {
    const cfg    = vscode.workspace.getConfiguration('nodalmerge');
    const stored = cfg.get<TopologyTemplate[]>('topologyTemplates') ?? [];
    return stored.length > 0 ? stored : [...DEFAULT_TEMPLATES];
  }

  getDefaultTopology(): string {
    return vscode.workspace.getConfiguration('nodalmerge').get<string>('defaultTopology') ?? '';
  }

  async saveProfiles(profiles: AgentProfile[]): Promise<void> {
    await vscode.workspace.getConfiguration('nodalmerge')
      .update('agentProfiles', profiles, vscode.ConfigurationTarget.Workspace);
  }

  async saveTemplates(templates: TopologyTemplate[]): Promise<void> {
    await vscode.workspace.getConfiguration('nodalmerge')
      .update('topologyTemplates', templates, vscode.ConfigurationTarget.Workspace);
  }

  async setDefaultTopology(name: string): Promise<void> {
    await vscode.workspace.getConfiguration('nodalmerge')
      .update('defaultTopology', name, vscode.ConfigurationTarget.Workspace);
  }

  // Split from a single defaultReviewPolicy setting: Task Review gates a worker proposal merging
  // into the agent session ("Automatically integrates worker proposals into the agent session"),
  // Workspace Review gates the session's own changes applying to the real workspace on disk
  // ("Controls whether session changes are automatically applied to your workspace"). These are
  // *seed* values only — consulted when Goal Workspace creates a new goal, not a live binding to
  // an already-created goal's own radio state.
  getDefaultTaskReviewPolicy(): string {
    return vscode.workspace.getConfiguration('nodalmerge').get<string>('defaultTaskReviewPolicy') ?? 'HumanRequired';
  }

  async saveDefaultTaskReviewPolicy(policy: string): Promise<void> {
    await vscode.workspace.getConfiguration('nodalmerge')
      .update('defaultTaskReviewPolicy', policy, vscode.ConfigurationTarget.Workspace);
  }

  getDefaultWorkspaceReviewPolicy(): string {
    return vscode.workspace.getConfiguration('nodalmerge').get<string>('defaultWorkspaceReviewPolicy') ?? 'HumanRequired';
  }

  async saveDefaultWorkspaceReviewPolicy(policy: string): Promise<void> {
    await vscode.workspace.getConfiguration('nodalmerge')
      .update('defaultWorkspaceReviewPolicy', policy, vscode.ConfigurationTarget.Workspace);
  }

  async resolveApiKey(profile: AgentProfile, secrets: vscode.SecretStorage): Promise<string | undefined> {
    if (!profile.apiKeyRef) { return undefined; }
    return secrets.get(profile.apiKeyRef);
  }

  /**
   * Distinguishes "never configured" from "configured, but the secret is gone" (e.g. after an
   * extension uninstall/reinstall cleared SecretStorage while nodalmerge.agentProfiles in
   * settings.json — which isn't extension-owned storage — kept the stale apiKeyRef around).
   */
  async getCredentialStatus(
    profile: AgentProfile,
    secrets: vscode.SecretStorage,
  ): Promise<'not-needed' | 'not-configured' | 'secret-missing' | 'ok'> {
    if (profile.provider === 'vscode-lm') { return 'not-needed'; }
    // CLI providers: no key = ambient CLI auth (fine); a stored ref should still verify resolvable.
    if (isCliProvider(profile.provider) && !profile.apiKeyRef) { return 'not-needed'; }
    if (!profile.apiKeyRef) { return 'not-configured'; }
    const stored = await secrets.get(profile.apiKeyRef);
    return stored ? 'ok' : 'secret-missing';
  }

  /** Human-readable reason resolveSpawnLlmConfig(profileId, ...) returned undefined. */
  async describeMissingCredentials(
    profileId: string,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
  ): Promise<string> {
    const p = this.getProfiles().find(pr => pr.id === profileId);
    if (!p) { return `no profile named "${profileId}" is configured`; }

    if (p.provider === 'vscode-lm') {
      return (!lmProxyBaseUrl || lmProxyBaseUrl.endsWith(':0'))
        ? 'the local VS Code LM proxy is not up yet — wait a moment and retry'
        : 'the VS Code LM proxy configuration is invalid';
    }

    if (isCliProvider(p.provider)) {
      // resolveSpawnLlmConfig never returns undefined for a CLI provider (no required fields), so
      // reaching here means something unexpected.
      return `the ${cliDisplayName(p.provider)} profile could not be resolved — this is unexpected; check the extension logs`;
    }

    const status = await this.getCredentialStatus(p, secrets);
    if (status === 'not-configured') {
      return 'no API key has ever been entered for this profile — open Model & Agent Studio and store one';
    }
    if (status === 'secret-missing') {
      return 'an API key was stored previously but is no longer in VS Code\'s secret storage ' +
        '(this happens after the extension is uninstalled and reinstalled) — re-enter it in Model & Agent Studio';
    }

    const baseUrl = p.provider === 'anthropic' ? (p.baseUrl?.trim() || 'https://api.anthropic.com') : p.baseUrl?.trim();
    if (!baseUrl) { return 'no base URL is set for this provider'; }
    return 'an unknown configuration error occurred';
  }

  /**
   * The opaque cache key sent to the server alongside live connection details — never the secret
   * itself. Real providers reuse the same apiKeyRef SecretStorage already keys on (so it's already
   * unique per profile, even when several profiles share a provider). vscode-lm profiles have no
   * apiKeyRef (no real secret — resolveSpawnLlmConfig always sends apiKey: ''), but the local LM
   * proxy's baseUrl is still session-ephemeral (a fresh OS-assigned port each activation, see
   * LmApiProxy.baseUrl) and must be re-resolved on resume the same way, so it gets a ref too.
   */
  resolveCredentialRef(profile: AgentProfile): string | undefined {
    if (profile.provider === 'vscode-lm') { return `vscode-lm:${profile.id}`; }
    // A CLI provider has no live connection details to re-resolve, but the server's scheduler still
    // keys parked-item credential re-warming on a ref, so give it a stable synthetic one when no
    // key is stored (same pattern as vscode-lm above).
    if (isCliProvider(profile.provider)) { return profile.apiKeyRef ?? `${profile.provider}:${profile.id}`; }
    return profile.apiKeyRef;
  }

  async storeApiKey(profile: AgentProfile, key: string, secrets: vscode.SecretStorage): Promise<void> {
    const ref = profile.apiKeyRef ?? `nodalmerge.apikey.${profile.id}`;
    await secrets.store(ref, key);
    if (!profile.apiKeyRef) {
      const profiles = this.getProfiles();
      const idx = profiles.findIndex(p => p.id === profile.id);
      if (idx >= 0) {
        profiles[idx] = { ...profiles[idx], apiKeyRef: ref };
        await this.saveProfiles(profiles);
      }
    }
  }

  /**
   * Removes a profile's stored API key: deletes the SecretStorage secret and clears apiKeyRef in
   * settings.json. Returns the removed ref (the server's credential-cache key) so the caller can
   * evict it from the running host — clearing settings alone leaves the key cached in the host's
   * memory until a restart, because Capture cannot express removal (a blank key is a no-op). See
   * IRuntimeCredentialCache.Evict. Returns undefined when nothing was stored.
   */
  async removeApiKey(profile: AgentProfile, secrets: vscode.SecretStorage): Promise<string | undefined> {
    const ref = profile.apiKeyRef;
    if (!ref) { return undefined; }
    await secrets.delete(ref);
    const profiles = this.getProfiles();
    const idx = profiles.findIndex(p => p.id === profile.id);
    if (idx >= 0) {
      const updated = { ...profiles[idx] };
      delete updated.apiKeyRef;
      profiles[idx] = updated;
      await this.saveProfiles(profiles);
    }
    return ref;
  }

  /**
   * Resolves LLM credentials for spawning an agent loop.
   * VS Code LM uses an empty apiKey (required by the host gate) and the local LM proxy URL.
   */
  async resolveSpawnLlmConfig(
    profileId: string,
    secrets: vscode.SecretStorage,
    lmProxyBaseUrl: string,
  ): Promise<SpawnLlmConfig | undefined> {
    const p = this.getProfiles().find(pr => pr.id === profileId);
    if (!p) { return undefined; }

    // Only meaningful on the openai HTTP path server-side; harmless (ignored) elsewhere. Omit when
    // false so strict profiles send no extra field.
    const lenient = p.toolCallParsing === 'lenient' ? { lenientToolParsing: true } : {};

    if (p.provider === 'vscode-lm') {
      if (!lmProxyBaseUrl || lmProxyBaseUrl.endsWith(':0')) {
        return undefined;
      }
      return {
        provider: 'openai',
        model:    p.model ?? '',
        baseUrl:  lmProxyBaseUrl,
        apiKey:   '',
        credentialRef: this.resolveCredentialRef(p) ?? '',
        ...lenient,
      };
    }

    if (isCliProvider(p.provider)) {
      // No HTTP connection — the server maps this provider to a local-binary IHarnessExecutor
      // (ClaudeCodeExecutor / CodexCliExecutor) with ambient auth. A stored key is optional and
      // means "inject as ANTHROPIC_API_KEY"/"OPENAI_API_KEY" server-side; blank model means the
      // CLI's own default.
      const cliKey = p.apiKeyRef ? await this.resolveApiKey(p, secrets) : undefined;
      return {
        provider: p.provider!,
        model:    p.model ?? '',
        baseUrl:  '',
        apiKey:   cliKey ?? '',
        credentialRef: this.resolveCredentialRef(p) ?? '',
        ...lenient,
      };
    }

    const apiKey = await this.resolveApiKey(p, secrets);
    const baseUrl = p.provider === 'anthropic'
      ? (p.baseUrl?.trim() || 'https://api.anthropic.com')
      : p.baseUrl?.trim();
    if (!baseUrl || apiKey === undefined) { return undefined; }

    return {
      provider: p.provider ?? 'anthropic',
      model:    p.model ?? '',
      baseUrl,
      apiKey,
      credentialRef: this.resolveCredentialRef(p) ?? '',
      ...lenient,
    };
  }

  /** Returns the effective system prompt, preferring the new field over the deprecated hint. */
  resolveSystemPrompt(profile: AgentProfile): string | undefined {
    return profile.systemPrompt || profile.systemPromptHint || undefined;
  }

  /** Returns 'inline' by default when deploymentMode is unset. */
  getEffectiveDeploymentMode(profile: AgentProfile): DeploymentMode {
    return profile.deploymentMode ?? 'inline';
  }

  async pickProfile(placeHolder = 'Select an agent profile'): Promise<AgentProfile | undefined> {
    const profiles = this.getProfiles();
    const items = profiles.map(p => ({
      label:       p.label,
      description: p.domain + (p.model ? ' · ' + p.model : ''),
      detail:      p.id,
      profile:     p,
    }));
    const picked = await vscode.window.showQuickPick(items, { placeHolder });
    return picked?.profile;
  }
}
