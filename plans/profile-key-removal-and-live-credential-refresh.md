# Remove-key button + live credential eviction on profile change

Fixes the workflow gap where removing/rotating a profile's API key doesn't take effect
until the host is restarted (observed live when swapping an api-key profile to a CLI
profile). Two parts, both required to fully close it.

## Why a restart was needed (root cause)

`IRuntimeCredentialCache` ([ServiceContracts.cs:1612](../src/NodalMerge.Studio.Core/Services/ServiceContracts.cs#L1612))
has only `Capture` and `TryGet`. `Capture` is deliberately a **no-op for a blank key** (a raw
key must never be persisted, and CLI ambient auth sends blank). So once a real key is captured
under a `credentialRef`, re-supplying that same ref with a blank key can't overwrite it — the
old key stays cached until the process dies. There is no eviction path at all. The extension
also has no way to *remove* a stored key: `AgentConfigService.storeApiKey` exists, but there's
no `removeApiKey`, and clearing it in settings.json only drops the `apiKeyRef`, leaving both the
SecretStorage secret and the host's cached credential in place.

Scope note: credential entry/storage lives only on the profile tab (`nodalmerge.agentProfiles`
→ settings.json + SecretStorage). Topology templates and host-side pipeline profiles reference
profiles by id and store no keys, so this change is profile-tab-only — no topology work needed.

## Part A — Remove-key button (extension)

1. **`AgentConfigService.removeApiKey(profile, secrets)`** (new): delete the SecretStorage secret
   at `profile.apiKeyRef` (if set), then clear `apiKeyRef` on that profile in settings.json
   (read-merge-write, mirroring `storeApiKey`'s settings update). Return the removed `apiKeyRef`
   so the caller can tell the host what to evict.
2. **`AgentConfigPanel.handleMessage` — new `removeApiKey` case** (mirror the `setApiKey` case,
   ~line 158): resolve the profile, call `removeApiKey`, then POST the host eviction (Part B),
   then `postMessage({ type: 'apiKeyRemoved', profileId })`.
3. **`modelAgentStudio.js`** — add a "Remove Key" button next to the existing "Store Key" button
   in the api-key row (~line 155-164), shown only when a key is stored (`p.apiKeyRef` truthy and
   provider is not `vscode-lm`). It posts `{ type: 'removeApiKey', profileId }`. Handle the
   `apiKeyRemoved` reply: update the row to the "No key stored" state and clear `apiKeyRef` in the
   in-memory `profiles` model (mirror the `apiKeySaved` handler ~line 620-629).

## Part B — Host eviction primitive + endpoint (host + extension call)

1. **`IRuntimeCredentialCache.Evict(string? credentialRef)`** (new) +
   [RuntimeCredentialCache.cs](../src/NodalMerge.Studio.Storage/RuntimeCredentialCache.cs) impl:
   remove the entry for `credentialRef` (no-op on null/empty or missing). This is the missing
   counterpart to `Capture` — the only way to force-clear a cached key without a restart.
2. **REST endpoint `POST /studio/credentials/evict`** in
   [StudioRestEndpoints.cs](../src/NodalMerge.Studio.Host/StudioRestEndpoints.cs): body
   `{ "credentialRef": "..." }` → resolve `IRuntimeCredentialCache` and call `Evict`. Returns 200
   even when nothing was cached (idempotent). This binds to loopback like every other studio
   endpoint — no new auth surface.
3. **Extension calls it** from the `removeApiKey` panel case (Part A step 2) only, passing the
   removed `apiKeyRef` as `credentialRef`. Eviction is deliberately scoped to explicit key
   *removal*: every other credential change (provider swap, model change, key rotation to a new
   value) is a non-blank `Capture` on the next spawn, which overwrites the cached entry already —
   removal is the sole case that `Capture` can't express (blank = no-op), so it's the only case
   that needs an explicit evict.

Note on live goal registrations: `_goalCredentialRegistrations` (per-goal, holds the key on the
hot path) are created at spawn and are naturally replaced on the next run with the profile's
current values. The shared `IRuntimeCredentialCache` (keyed by `credentialRef`, shared across
goals/parked items) is the one that persisted the stale key; evicting it is the fix. We are
deliberately *not* mutating in-flight goal registrations mid-run — a running goal keeps the
credentials it started with, which is correct.

## Tests / verification

- **Host:** unit test for `RuntimeCredentialCache`: Capture then Evict then TryGet → null; Evict
  of an unknown/blank ref is a safe no-op. Endpoint smoke: POST evict returns 200 and a
  subsequent resolve misses.
- **Extension:** `npm run compile` + `npm run typecheck` clean. If there are existing
  AgentConfigService tests, add a `removeApiKey` case (deletes secret, clears `apiKeyRef`).
- **Manual (the real repro):** with a key stored on a profile, click Remove Key, confirm the key
  row clears and a subsequent CLI run uses ambient auth **without a host restart**.

## Out of scope (per discussion)

- Key-type/provider mismatch validation (e.g. rejecting an OpenAI key injected as
  `ANTHROPIC_API_KEY`) — key formats aren't stable enough to detect reliably; left alone.
- Mutating credentials of an already-running goal mid-flight.
