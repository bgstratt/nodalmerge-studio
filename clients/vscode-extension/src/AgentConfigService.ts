import * as vscode from 'vscode';

export type LlmProvider = 'anthropic' | 'openai' | 'vscode-lm';

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
  /** @deprecated Use systemPrompt */
  systemPromptHint?: string;
}

export interface TopologyTemplate {
  name:         string;
  orchestrator: string;
  // Optional per-stage credential profile overrides — when unset, that stage inherits the
  // orchestrator's profile (today's behavior). Profile ids reference entries from getProfiles().
  planner?:     string;
  worker?:      string;
  reviewer?:    string;
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
}

const DEFAULT_PROFILES: AgentProfile[] = [
  { id: 'orchestrator', label: 'Orchestrator', domain: 'orchestration', provider: 'vscode-lm', model: '' },
  { id: 'worker',       label: 'Worker',       domain: 'general',       provider: 'vscode-lm', model: '' },
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

  getDefaultReviewPolicy(): string {
    return vscode.workspace.getConfiguration('nodalmerge').get<string>('defaultReviewPolicy') ?? 'HumanRequired';
  }

  async saveDefaultReviewPolicy(policy: string): Promise<void> {
    await vscode.workspace.getConfiguration('nodalmerge')
      .update('defaultReviewPolicy', policy, vscode.ConfigurationTarget.Workspace);
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

    if (p.provider === 'vscode-lm') {
      if (!lmProxyBaseUrl || lmProxyBaseUrl.endsWith(':0')) {
        return undefined;
      }
      return {
        provider: 'openai',
        model:    p.model ?? '',
        baseUrl:  lmProxyBaseUrl,
        apiKey:   '',
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
