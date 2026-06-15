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

const DEFAULT_PROFILES: AgentProfile[] = [
  { id: 'orchestrator', label: 'Orchestrator', domain: 'orchestration', model: '' },
  { id: 'worker',       label: 'Worker',       domain: 'general',       model: '' },
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
