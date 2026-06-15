import * as http from 'http';
import * as vscode from 'vscode';

// Minimal OpenAI-format types for the proxy surface
interface OaiMessage {
  role:          string;
  content?:      string | null;
  tool_call_id?: string;
  tool_calls?:   OaiToolCall[];
}

interface OaiToolCall {
  id:       string;
  type:     string;
  function: { name: string; arguments: string };
}

interface OaiTool {
  type:     string;
  function: { name: string; description?: string; parameters?: unknown };
}

interface OaiRequest {
  model?:    string;
  messages?: OaiMessage[];
  tools?:    OaiTool[];
}

/**
 * Local HTTP server that translates OpenAI /v1/chat/completions requests into
 * VS Code Language Model API calls, enabling Copilot and Cursor subscriptions
 * to drive agent loops without an external API key.
 *
 * The .NET host talks to this as an "openai" provider pointed at
 * http://127.0.0.1:{port} with no API key required.
 */
export class LmApiProxy implements vscode.Disposable {
  private server: http.Server | undefined;
  private _port = 0;

  get baseUrl(): string { return `http://127.0.0.1:${this._port}`; }

  async start(): Promise<void> {
    this.server = http.createServer((req, res) => { void this.handle(req, res); });
    await new Promise<void>((resolve, reject) => {
      this.server!.listen(0, '127.0.0.1', () => {
        const addr = this.server!.address() as { port: number };
        this._port = addr.port;
        resolve();
      });
      this.server!.on('error', reject);
    });
  }

  private async handle(req: http.IncomingMessage, res: http.ServerResponse): Promise<void> {
    if (req.method !== 'POST') {
      res.writeHead(405); res.end(); return;
    }

    try {
      const body = await readBody(req);
      const oaiReq = JSON.parse(body) as OaiRequest;
      const oaiRes = await this.dispatch(oaiReq);
      res.writeHead(200, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify(oaiRes));
    } catch (err) {
      res.writeHead(500, { 'Content-Type': 'application/json' });
      res.end(JSON.stringify({ error: { message: String(err), type: 'server_error' } }));
    }
  }

  private async dispatch(req: OaiRequest): Promise<unknown> {
    const modelHint = req.model ?? '';
    const models = await vscode.lm.selectChatModels(modelHint ? { family: modelHint } : undefined);
    if (!models.length) {
      throw new Error(
        'No VS Code language models available. ' +
        'Ensure GitHub Copilot (or another LM provider) is signed in.'
      );
    }
    const model = models[0];

    const { vsTools, origToSafe, safeToOrig } = toVsTools(req.tools ?? []);
    const vsMessages = toVsMessages(req.messages ?? [], origToSafe);
    const cts        = new vscode.CancellationTokenSource();

    const response = await model.sendRequest(vsMessages, { tools: vsTools }, cts.token);

    let textContent = '';
    const toolCalls: OaiToolCall[] = [];
    let callIndex   = 0;

    for await (const part of response.stream) {
      if (part instanceof vscode.LanguageModelTextPart) {
        textContent += part.value;
      } else if (part instanceof vscode.LanguageModelToolCallPart) {
        // Map back sanitized tool name to original name expected by the host
        const originalName = safeToOrig[part.name] ?? part.name;
        toolCalls.push({
          id:       part.callId,
          type:     'function',
          function: {
            name:      originalName,
            arguments: JSON.stringify(part.input)
          }
        });
        callIndex++;
      }
    }

    const finishReason = toolCalls.length > 0 ? 'tool_calls' : 'stop';
    const message: OaiMessage = {
      role:       'assistant',
      content:    textContent || null,
      tool_calls: toolCalls.length > 0 ? toolCalls : undefined,
    };

    return {
      id:      `chatcmpl-vscode-${Date.now()}`,
      object:  'chat.completion',
      created: Math.floor(Date.now() / 1000),
      model:   model.id,
      choices: [{ index: 0, message, finish_reason: finishReason }],
      usage:   { prompt_tokens: 0, completion_tokens: 0, total_tokens: 0 },
    };
  }

  dispose(): void {
    this.server?.close();
  }
}

// ── Conversion helpers ─────────────────────────────────────────────────────

function toVsMessages(msgs: OaiMessage[], origToSafe: Record<string, string> = {}): vscode.LanguageModelChatMessage[] {
  const out: vscode.LanguageModelChatMessage[] = [];
  for (const msg of msgs) {
    if (msg.role === 'system' || msg.role === 'user') {
      out.push(vscode.LanguageModelChatMessage.User(msg.content ?? ''));
    } else if (msg.role === 'assistant') {
      const parts: (vscode.LanguageModelTextPart | vscode.LanguageModelToolCallPart)[] = [];
      if (msg.content) parts.push(new vscode.LanguageModelTextPart(msg.content));
      for (const tc of msg.tool_calls ?? []) {
        let input: unknown = {};
        try { input = JSON.parse(tc.function.arguments || '{}'); } catch { /* use empty object */ }
        // Use sanitized tool name if available
        const safeName = origToSafe[tc.function.name] ?? tc.function.name;
        parts.push(new vscode.LanguageModelToolCallPart(tc.id, safeName, input));
      }
      out.push(vscode.LanguageModelChatMessage.Assistant(parts.length === 1 && parts[0] instanceof vscode.LanguageModelTextPart
        ? parts[0].value
        : parts));
    } else if (msg.role === 'tool') {
      out.push(vscode.LanguageModelChatMessage.User([
        new vscode.LanguageModelToolResultPart(
          msg.tool_call_id ?? '',
          [new vscode.LanguageModelTextPart(msg.content ?? '')]
        )
      ]));
    }
  }
  return out;
}

function toVsTools(tools: OaiTool[]): { vsTools: vscode.LanguageModelChatTool[]; origToSafe: Record<string,string>; safeToOrig: Record<string,string> } {
  const origToSafe: Record<string,string> = {};
  const safeToOrig: Record<string,string> = {};
  const vsTools = tools
    .filter(t => t.type === 'function')
    .map(t => {
      const orig = t.function.name;
      // sanitize to allowed characters (alphanumeric, hyphen, underscore)
      const safe = String(orig).replace(/[^A-Za-z0-9-_]/g, '_');
      origToSafe[orig] = safe;
      safeToOrig[safe] = orig;
      return {
        name:        safe,
        description: t.function.description ?? '',
        inputSchema: t.function.parameters as Record<string, unknown> | undefined,
      };
    });
  return { vsTools, origToSafe, safeToOrig };
}

function readBody(req: http.IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    const chunks: Buffer[] = [];
    req.on('data', (chunk: Buffer) => chunks.push(chunk));
    req.on('end',  () => resolve(Buffer.concat(chunks).toString('utf8')));
    req.on('error', reject);
  });
}
