import type { BranchStream } from "./branchReplay";

export function computeBranchOffsets(
  sortedBranchIds: string[],
  branches: Record<string, BranchStream>,
  branchNodeIds: Record<string, string[]>
): Record<string, number> {
  const offsets: Record<string, number> = {};
  const resolving = new Set<string>();

  const computeOffset = (branchId: string): number => {
    if (branchId in offsets) {
      return offsets[branchId];
    }
    if (resolving.has(branchId)) {
      return 0;
    }

    resolving.add(branchId);
    const branch = branches[branchId];
    if (!branch || !branch.basedOnBranchId || !branch.basedOnNodeId) {
      offsets[branchId] = 0;
      resolving.delete(branchId);
      return 0;
    }

    const parentIds = branchNodeIds[branch.basedOnBranchId] ?? [];
    const parentIndex = parentIds.indexOf(branch.basedOnNodeId);
    const parentOffset = computeOffset(branch.basedOnBranchId);
    offsets[branchId] = parentIndex >= 0 ? parentOffset + parentIndex + 1 : parentOffset;
    resolving.delete(branchId);
    return offsets[branchId];
  };

  for (const branchId of sortedBranchIds) {
    computeOffset(branchId);
  }

  return offsets;
}

export function computeMaxBranchColumn(
  sortedBranchIds: string[],
  branchOffsets: Record<string, number>,
  branchNodeIds: Record<string, string[]>
): number {
  let maxColumn = 0;
  for (const branchId of sortedBranchIds) {
    const offset = branchOffsets[branchId] ?? 0;
    const count = (branchNodeIds[branchId] ?? []).length;
    if (count <= 0) {
      continue;
    }
    maxColumn = Math.max(maxColumn, offset + count - 1);
  }
  return maxColumn;
}
