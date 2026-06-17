import type { ReplayAction } from './branchReplay';

export type WsStatus = 'connecting' | 'connected' | 'disconnected' | 'error';

interface PackNode {
  nodeId:    string;
  branchId:  string;
  lamport?:  number;
  author?:   string;
  atIso?:    string;
  opSummary?: string;
  [key: string]: unknown;
}

export class WsClient {
  private ws: WebSocket | null = null;

  constructor(
    private readonly port: number,
    private readonly roomId: string,
    private readonly onAction: (action: ReplayAction) => void,
    private readonly onStatus: (status: WsStatus) => void,
    // Slice 13h — same /ws/runtime room broker ArtifactExplorerPanel's inline script already
    // subscribes to for live stage badges (clients/vscode-extension/src/panels/
    // ArtifactExplorerPanel.ts's connectStageSocket). Routed separately from onAction: a
    // work-unit-stage-changed frame isn't a DAG op and must never fall through to the "else"
    // branch below, which would otherwise append it as a fake graph node.
    private readonly onStageChange?: (workUnitId: string, stage: string | null) => void,
  ) {}

  connect(): void {
    this.onStatus('connecting');
    const ws = new WebSocket('ws://127.0.0.1:' + this.port + '/ws/runtime');
    this.ws = ws;

    ws.onopen = () => {
      this.onStatus('connected');
      ws.send(JSON.stringify({
        type:     'hello',
        room:     this.roomId,
        pubkey:   'studio-ui',
        frontier: [],
      }));
    };

    ws.onmessage = (e: MessageEvent) => {
      try {
        const msg = JSON.parse(e.data as string) as Record<string, unknown>;

        if (msg.type === 'work-unit-stage-changed') {
          const workUnitId = typeof msg.workUnitId === 'string' ? msg.workUnitId : null;
          if (workUnitId) {
            this.onStageChange?.(workUnitId, typeof msg.stage === 'string' ? msg.stage : null);
          }
        } else if (msg.type === 'pack' && Array.isArray(msg.nodes)) {
          // Bulk historical nodes from request-server-pack
          for (const raw of msg.nodes as PackNode[]) {
            if (typeof raw.nodeId !== 'string' || typeof raw.branchId !== 'string') { continue; }
            this.onAction({
              type:        'append-branch-node',
              branchId:    raw.branchId,
              roomId:      this.roomId,
              opSummary:   raw.opSummary ?? 'historical',
              payloadJson: JSON.stringify(raw),
              replayOpId:  raw.nodeId,
              lamport:     typeof raw.lamport === 'number' ? raw.lamport : null,
              author:      typeof raw.author  === 'string' ? raw.author  : null,
              atIso:       typeof raw.atIso   === 'string' ? raw.atIso   : undefined,
            });
          }
        } else {
          // Live runtime event
          this.onAction({
            type:           'append-runtime-event',
            roomId:         this.roomId,
            payload:        msg,
            receivedAtIso:  new Date().toISOString(),
          });
        }
      } catch {
        // ignore malformed frames
      }
    };

    ws.onerror   = () => { this.onStatus('error'); };
    ws.onclose   = () => { this.ws = null; this.onStatus('disconnected'); };
  }

  requestPack(frontier: string[]): void {
    this.ws?.send(JSON.stringify({ type: 'request-server-pack', frontier }));
  }

  close(): void {
    this.ws?.close();
    this.ws = null;
  }
}
