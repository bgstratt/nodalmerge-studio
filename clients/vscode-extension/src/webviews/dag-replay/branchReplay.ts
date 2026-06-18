export type ReplayMode = "live" | "playback";

export type ReplayNode = {
  nodeId: string;
  roomId: string;
  branchId: string;
  parentIds: string[];
  lamport: number | null;
  author: string | null;
  atIso: string;
  opSummary: string;
  payloadJson: string;
  payloadRef?: string;
};

export type BranchStream = {
  branchId: string;
  roomId: string;
  label: string;
  rootNodeId: string | null;
  headNodeId: string | null;
  basedOnBranchId: string | null;
  basedOnNodeId: string | null;
  isMain: boolean;
};

export type MergeRecord = {
  mergeNodeId: string;
  sourceBranchId: string;
  sourceNodeId: string;
  targetBranchId: string;
  targetPrevHeadNodeId: string;
  targetNextHeadNodeId: string;
  atIso: string;
};

export type ReplayCursor = {
  mode: ReplayMode;
  branchId: string;
  nodeIndex: number;
  nodeId: string | null;
};

export type ReplayState = {
  branches: Record<string, BranchStream>;
  nodesById: Record<string, ReplayNode>;
  branchNodeIds: Record<string, string[]>;
  merges: MergeRecord[];
  cursor: ReplayCursor;
  activeBranchId: string;
  sequence: number;
};

export type ReplayAction =
  | { type: "reset"; roomId: string; branchId?: string; branchLabel?: string }
  | { type: "set-mode"; mode: ReplayMode }
  | { type: "set-active-branch"; branchId: string }
  | { type: "set-cursor-index"; branchId: string; nodeIndex: number }
  | {
      type: "register-branch";
      branchId: string;
      roomId: string;
      label: string;
      basedOnBranchId: string | null;
      basedOnNodeId: string | null;
      isMain?: boolean;
    }
  | { type: "create-branch-from-cursor"; newBranchId: string; newRoomId: string; newLabel: string; reason: "manual" | "playback-edit" }
  | {
      type: "append-branch-node";
      branchId: string;
      roomId: string;
      opSummary: string;
      payloadJson: string;
      payloadRef?: string;
      replayOpId?: string;
      lamport?: number | null;
      author?: string | null;
      atIso?: string;
    }
  | { type: "merge-into-head"; sourceBranchId: string; sourceNodeId: string; targetBranchId: string };

function createMainBranch(roomId: string, branchId: string, branchLabel: string): BranchStream {
  return {
    branchId,
    roomId,
    label: branchLabel,
    rootNodeId: null,
    headNodeId: null,
    basedOnBranchId: null,
    basedOnNodeId: null,
    isMain: true
  };
}

export function createInitialReplayState(roomId: string, branchId = "main", branchLabel = "main"): ReplayState {
  const main = createMainBranch(roomId, branchId, branchLabel);
  return {
    branches: { [branchId]: main },
    nodesById: {},
    branchNodeIds: { [branchId]: [] },
    merges: [],
    cursor: { mode: "live", branchId, nodeIndex: 0, nodeId: null },
    activeBranchId: branchId,
    sequence: 0
  };
}

function compareNodeIds(a: ReplayNode, b: ReplayNode): number {
  const aLamport = a.lamport ?? Number.MIN_SAFE_INTEGER;
  const bLamport = b.lamport ?? Number.MIN_SAFE_INTEGER;
  if (aLamport !== bLamport) {
    return aLamport - bLamport;
  }
  const aAuthor = a.author ?? "";
  const bAuthor = b.author ?? "";
  if (aAuthor !== bAuthor) {
    return aAuthor.localeCompare(bAuthor);
  }
  return a.nodeId.localeCompare(b.nodeId);
}

function clampIndex(value: number, total: number): number {
  if (total <= 0) {
    return 0;
  }
  return Math.max(0, Math.min(value, total - 1));
}

function getCursorNodeId(branchNodeIds: string[], index: number): string | null {
  if (branchNodeIds.length === 0) {
    return null;
  }
  return branchNodeIds[clampIndex(index, branchNodeIds.length)] ?? null;
}

function appendNodeToBranch(state: ReplayState, branchId: string, node: ReplayNode): ReplayState {
  const branch = state.branches[branchId];
  if (!branch) {
    return state;
  }
  if (state.nodesById[node.nodeId]) {
    return state;
  }
  const nodesById = {
    ...state.nodesById,
    [node.nodeId]: node
  };
  const previousBranchIds = state.branchNodeIds[branchId] ?? [];
  const sortedBranchNodeIds = [...previousBranchIds, node.nodeId].sort((leftId, rightId) =>
    compareNodeIds(nodesById[leftId], nodesById[rightId])
  );
  const branches = {
    ...state.branches,
    [branchId]: {
      ...branch,
      rootNodeId: branch.rootNodeId ?? sortedBranchNodeIds[0] ?? null,
      headNodeId: sortedBranchNodeIds[sortedBranchNodeIds.length - 1] ?? null
    }
  };
  const branchNodeIds = {
    ...state.branchNodeIds,
    [branchId]: sortedBranchNodeIds
  };
  const shouldFollowHead = state.cursor.mode === "live" && state.cursor.branchId === branchId;
  const cursorNodeIndex = shouldFollowHead
    ? clampIndex(sortedBranchNodeIds.length - 1, sortedBranchNodeIds.length)
    : clampIndex(state.cursor.nodeIndex, sortedBranchNodeIds.length);
  return {
    ...state,
    nodesById,
    branches,
    branchNodeIds,
    cursor: {
      ...state.cursor,
      nodeIndex: cursorNodeIndex,
      nodeId: getCursorNodeId(sortedBranchNodeIds, cursorNodeIndex)
    }
  };
}

export function replayReducer(state: ReplayState, action: ReplayAction): ReplayState {
  if (action.type === "reset") {
    return createInitialReplayState(action.roomId, action.branchId ?? "main", action.branchLabel ?? "main");
  }

  if (action.type === "set-mode") {
    if (action.mode === "live") {
      // Switching to live should jump the cursor back to the active branch's current head,
      // not just flip the mode flag — otherwise "Live" only re-enables auto-follow for the
      // *next* incoming node and leaves the view stuck wherever playback left it.
      const branchIds = state.branchNodeIds[state.cursor.branchId] ?? [];
      const headIndex = clampIndex(branchIds.length - 1, branchIds.length);
      return {
        ...state,
        cursor: {
          ...state.cursor,
          mode: "live",
          nodeIndex: headIndex,
          nodeId: getCursorNodeId(branchIds, headIndex)
        }
      };
    }
    return {
      ...state,
      cursor: {
        ...state.cursor,
        mode: action.mode
      }
    };
  }

  if (action.type === "set-active-branch") {
    const branchIds = state.branchNodeIds[action.branchId] ?? [];
    const nextIndex = action.branchId === state.cursor.branchId ? state.cursor.nodeIndex : clampIndex(branchIds.length - 1, branchIds.length);
    return {
      ...state,
      activeBranchId: action.branchId,
      cursor: {
        ...state.cursor,
        branchId: action.branchId,
        nodeIndex: nextIndex,
        nodeId: getCursorNodeId(branchIds, nextIndex)
      }
    };
  }

  if (action.type === "set-cursor-index") {
    const branchIds = state.branchNodeIds[action.branchId] ?? [];
    const nextIndex = clampIndex(action.nodeIndex, branchIds.length);
    return {
      ...state,
      cursor: {
        ...state.cursor,
        mode: "playback",
        branchId: action.branchId,
        nodeIndex: nextIndex,
        nodeId: getCursorNodeId(branchIds, nextIndex)
      }
    };
  }

  if (action.type === "register-branch") {
    if (state.branches[action.branchId]) {
      return state;
    }
    const branch: BranchStream = {
      branchId: action.branchId,
      roomId: action.roomId,
      label: action.label,
      rootNodeId: action.basedOnNodeId,
      headNodeId: null,
      basedOnBranchId: action.basedOnBranchId,
      basedOnNodeId: action.basedOnNodeId,
      isMain: action.isMain ?? false
    };
    return {
      ...state,
      branches: {
        ...state.branches,
        [action.branchId]: branch
      },
      branchNodeIds: {
        ...state.branchNodeIds,
        [action.branchId]: []
      }
    };
  }

  if (action.type === "create-branch-from-cursor") {
    if (state.branches[action.newBranchId]) {
      return state;
    }
    const sourceBranchId = state.cursor.branchId;
    const sourceIds = state.branchNodeIds[sourceBranchId] ?? [];
    if (sourceIds.length === 0) {
      return state;
    }
    const sourceIndex = clampIndex(state.cursor.nodeIndex, sourceIds.length);
    const sourceNodeId = sourceIds[sourceIndex] ?? null;
    const newBranch: BranchStream = {
      branchId: action.newBranchId,
      roomId: action.newRoomId,
      label: action.newLabel,
      rootNodeId: sourceNodeId,
      headNodeId: null,
      basedOnBranchId: sourceBranchId,
      basedOnNodeId: sourceNodeId,
      isMain: false
    };
    return {
      ...state,
      branches: {
        ...state.branches,
        [action.newBranchId]: newBranch
      },
      branchNodeIds: {
        ...state.branchNodeIds,
        [action.newBranchId]: []
      },
      activeBranchId: action.newBranchId,
      cursor: {
        mode: "live",
        branchId: action.newBranchId,
        nodeIndex: 0,
        nodeId: null
      }
    };
  }

  if (action.type === "append-branch-node") {
    const branch = state.branches[action.branchId];
    if (!branch) {
      return state;
    }
    const sequence = state.sequence + 1;
    const stableNodeId = action.replayOpId ?? `${action.branchId}-${sequence.toString(36)}`;
    if (state.nodesById[stableNodeId]) {
      return { ...state, sequence };
    }
    const previousBranchIds = state.branchNodeIds[action.branchId] ?? [];
    const previousHeadNodeId = previousBranchIds.length > 0 ? previousBranchIds[previousBranchIds.length - 1] : null;
    const nextNode: ReplayNode = {
      nodeId: stableNodeId,
      roomId: action.roomId,
      branchId: action.branchId,
      parentIds: previousHeadNodeId ? [previousHeadNodeId] : [],
      lamport: action.lamport ?? null,
      author: action.author ?? null,
      atIso: action.atIso ?? new Date().toISOString(),
      opSummary: action.opSummary,
      payloadJson: action.payloadJson,
      payloadRef: action.payloadRef
    };
    return appendNodeToBranch({ ...state, sequence }, action.branchId, nextNode);
  }

  if (action.type === "merge-into-head") {
    const sourceBranch = state.branches[action.sourceBranchId];
    const targetBranch = state.branches[action.targetBranchId];
    if (!sourceBranch || !targetBranch) {
      return state;
    }
    const sourceNode = state.nodesById[action.sourceNodeId];
    if (!sourceNode) {
      return state;
    }
    const targetIds = state.branchNodeIds[action.targetBranchId] ?? [];
    const targetHeadNodeId = targetIds.length > 0 ? targetIds[targetIds.length - 1] : null;
    if (!targetHeadNodeId) {
      return state;
    }
    if (targetHeadNodeId === action.sourceNodeId) {
      return state;
    }
    const alreadyMerged = state.merges.some(
      (entry) =>
        entry.sourceBranchId === action.sourceBranchId &&
        entry.sourceNodeId === action.sourceNodeId &&
        entry.targetBranchId === action.targetBranchId
    );
    if (alreadyMerged) {
      return state;
    }
    const sequence = state.sequence + 1;
    const mergeNodeId = `${action.targetBranchId}-merge-${sequence.toString(36)}`;
    const mergeNode: ReplayNode = {
      nodeId: mergeNodeId,
      roomId: targetBranch.roomId,
      branchId: action.targetBranchId,
      parentIds: [targetHeadNodeId, action.sourceNodeId],
      lamport: null,
      author: "merge",
      atIso: new Date().toISOString(),
      opSummary: `merge ${action.sourceBranchId}:${action.sourceNodeId} -> ${action.targetBranchId}:${targetHeadNodeId}`,
      payloadJson: JSON.stringify({
        kind: "merge",
        sourceBranchId: action.sourceBranchId,
        sourceNodeId: action.sourceNodeId,
        targetBranchId: action.targetBranchId,
        targetHeadNodeId
      }),
      payloadRef: "merge"
    };
    const mergedState = appendNodeToBranch({ ...state, sequence }, action.targetBranchId, mergeNode);
    const nextMerge: MergeRecord = {
      mergeNodeId,
      sourceBranchId: action.sourceBranchId,
      sourceNodeId: action.sourceNodeId,
      targetBranchId: action.targetBranchId,
      targetPrevHeadNodeId: targetHeadNodeId,
      targetNextHeadNodeId: mergeNodeId,
      atIso: mergeNode.atIso
    };
    return {
      ...mergedState,
      merges: [nextMerge, ...mergedState.merges].slice(0, 64)
    };
  }

  return state;
}
