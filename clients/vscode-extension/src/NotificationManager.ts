import * as vscode from 'vscode';
import type { MergeProposal } from './panels/MergeReviewPanel';

export class NotificationManager {
  // Track last known status per proposal so we only fire once per transition.
  private readonly seenStatuses = new Map<string, string>();
  private readonly onOpenReview: (proposalId: string) => void;

  constructor(onOpenReview: (proposalId: string) => void) {
    this.onOpenReview = onOpenReview;
  }

  update(proposals: MergeProposal[]): void {
    for (const p of proposals) {
      const prev = this.seenStatuses.get(p.proposalId);
      const curr = (p.status ?? '').toLowerCase();

      if (curr === 'readyforreview' && prev !== 'readyforreview') {
        void this.notifyReady(p);
      }

      this.seenStatuses.set(p.proposalId, curr);
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
}
