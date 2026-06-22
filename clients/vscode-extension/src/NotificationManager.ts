import * as vscode from 'vscode';
import type { MergeProposal } from './panels/MergeReviewPanel';

interface WorkUnitBlockSignal {
  workUnitId: string;
  goal: string;
  fanOutInfo?: { blockedReason?: string | null } | null;
}

interface FindingSignal {
  findingId: string;
  title: string;
  status: string;
}

export class NotificationManager {
  // Track last known status per proposal so we only fire once per transition.
  private readonly seenStatuses = new Map<string, string>();
  // Track last known blocked-reason per work unit so we only fire once per transition.
  private readonly seenBlockedReasons = new Map<string, string | null>();
  // Track findings we've already notified about so a re-poll while still Open doesn't refire.
  private readonly seenFindingIds = new Set<string>();
  private readonly onOpenReview: (proposalId: string) => void;
  private readonly onOpenInsights: () => void;

  constructor(onOpenReview: (proposalId: string) => void, onOpenInsights: () => void = () => {}) {
    this.onOpenReview = onOpenReview;
    this.onOpenInsights = onOpenInsights;
  }

  update(proposals: MergeProposal[], workUnits: WorkUnitBlockSignal[] = [], findings: FindingSignal[] = []): void {
    for (const f of findings) {
      if (f.status === 'Open' && !this.seenFindingIds.has(f.findingId)) {
        void this.notifyFindingReady(f);
      }
      this.seenFindingIds.add(f.findingId);
    }

    for (const p of proposals) {
      const prev = this.seenStatuses.get(p.proposalId);
      const curr = (p.status ?? '').toLowerCase();

      if (curr === 'readyforreview' && prev !== 'readyforreview') {
        void this.notifyReady(p);
      } else if (curr === 'merged' && p.autoApplied && prev !== 'merged') {
        void this.notifyAutoApplied(p);
      } else if (curr === 'rejected' && prev !== 'rejected' && p.verificationResults) {
        void this.notifyAutoRejected(p);
      }

      this.seenStatuses.set(p.proposalId, curr);
    }

    for (const wu of workUnits) {
      const prev = this.seenBlockedReasons.get(wu.workUnitId) ?? null;
      const curr = wu.fanOutInfo?.blockedReason ?? null;

      if (curr && !prev) {
        void this.notifyFanOutBlocked(wu, curr);
      }

      this.seenBlockedReasons.set(wu.workUnitId, curr);
    }
  }

  private async notifyReady(p: MergeProposal): Promise<void> {
    const action = await vscode.window.showInformationMessage(
      'NodalMerge: "' + p.sourceBranch + '" is ready for review.',
      'Open Review',
      'Dismiss'
    );
    if (action === 'Open Review') {
      this.onOpenReview(p.proposalId);
    }
  }

  private async notifyAutoApplied(p: MergeProposal): Promise<void> {
    const action = await vscode.window.showInformationMessage(
      'NodalMerge: "' + p.sourceBranch + '" was auto-applied by the reviewer agent.',
      'Open',
      'Dismiss'
    );
    if (action === 'Open') {
      this.onOpenReview(p.proposalId);
    }
  }

  private async notifyAutoRejected(p: MergeProposal): Promise<void> {
    const firstLine = (p.verificationResults ?? '').split('\n')[0];
    const action = await vscode.window.showWarningMessage(
      'NodalMerge: reviewer agent rejected "' + p.sourceBranch + '" — ' + firstLine,
      'Open',
      'Dismiss'
    );
    if (action === 'Open') {
      this.onOpenReview(p.proposalId);
    }
  }

  private async notifyFanOutBlocked(wu: WorkUnitBlockSignal, blockedReason: string): Promise<void> {
    await vscode.window.showWarningMessage(
      'NodalMerge: "' + wu.goal + '" is blocked from starting — ' + blockedReason
    );
  }

  private async notifyFindingReady(f: FindingSignal): Promise<void> {
    const action = await vscode.window.showInformationMessage(
      'NodalMerge: new finding ready for review — "' + f.title + '"',
      'Open Insights',
      'Dismiss'
    );
    if (action === 'Open Insights') {
      this.onOpenInsights();
    }
  }
}
