import * as vscode from 'vscode';

export type LlmProvider = 'anthropic' | 'openai' | 'vscode-lm';

export interface AgentProfile {
  id:               string;
  label:            string;
  domain:           string;
  provider?:        LlmProvider;
  model?:           string;
  baseUrl?:         string;
  apiKeyRef?:       string;
  systemPromptHint?: string;
}

export interface TopologyWorker {
  profile: string;
  branch?: string;
}

export interface TopologyTemplate {
  name:         string;
  orchestrator: string;
  workers?:     TopologyWorker[];
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
  { name: 'Default', orchestrator: 'orchestrator', workers: [{ profile: 'worker' }] },
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

  async resolveApiKey(profile: AgentProfile, secrets: vscode.SecretStorage): Promise<string | undefined> {
    if (!profile.apiKeyRef) { return undefined; }
    return secrets.get(profile.apiKeyRef);
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
