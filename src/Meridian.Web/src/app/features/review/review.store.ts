import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ConflictRecord } from '../../models';

import { FeatureStore } from '../../shared/feature-store';
import { ImportsStore } from '../imports/imports.store';

@Injectable({ providedIn: 'root' })
export class ReviewStore extends FeatureStore {
  private readonly importsStore = inject(ImportsStore);
  readonly conflicts = signal<ConflictRecord[]>([]);

  readonly reviewTab = signal<'conflicts' | 'extractions' | 'resolved'>('conflicts');
  readonly selectedReviewKind = signal<'conflict' | 'pdf'>('conflict');
  readonly resolvedConflicts = signal<ConflictRecord[]>([]);
  readonly selectedConflictId = signal<string | null>(null);

  readonly selectedConflict = computed(
    () =>
      [...this.conflicts(), ...this.resolvedConflicts()].find(
        (item) => item.id === this.selectedConflictId(),
      ) ?? null,
  );

  readonly decisionNote = signal('');

  readonly conflictVoteFilter = signal('');

  readonly conflictStatus = signal<'Pending' | 'Resolved'>('Pending');

  private selectedProposalIndex = 0;

  async loadConflicts(): Promise<void> {
    try {
      const [conflicts, resolved] = await Promise.all([
        firstValueFrom(this.api.conflicts(this.conflictVoteFilter(), 'Pending')),
        firstValueFrom(this.api.conflicts(this.conflictVoteFilter(), 'Resolved')),
      ]);
      this.resolvedConflicts.set(resolved);
      this.conflicts.set(conflicts);
      if (![...conflicts, ...resolved].some((item) => item.id === this.selectedConflictId()))
        this.selectedConflictId.set(conflicts[0]?.id ?? null);
    } catch (error) {
      this.showError(error);
    }
  }

  selectConflict(id: string): void {
    this.selectedConflictId.set(id);
    this.decisionNote.set('');
  }

  async searchConflicts(): Promise<void> {
    this.selectedConflictId.set(null);
    await this.loadConflicts();
  }

  async decide(
    conflict: ConflictRecord,
    action: 'Accept' | 'Reject',
    proposalIndex?: number,
  ): Promise<void> {
    this.selectedProposalIndex = proposalIndex ?? 0;
    await this.run(async () => {
      try {
        await firstValueFrom(
          this.api.decideConflict(
            conflict.id,
            action,
            action === 'Accept' ? this.selectedProposalIndex : undefined,
            this.decisionNote(),
          ),
        );
      } catch (error) {
        if (error instanceof HttpErrorResponse && error.status === 409) await this.loadConflicts();
        throw error;
      }
      this.notice.set(
        action === 'Accept' ? 'Proposal accepted and audited.' : 'Proposals rejected and audited.',
      );
      await Promise.all([this.loadConflicts(), this.importsStore.loadImports()]);
    });
  }

  conflictFields(
    targetType: string,
    value: Record<string, unknown>,
  ): { label: string; text: string }[] {
    if (targetType === 'VoteMetadata')
      return [
        { label: 'Session', text: this.displayValue(value['session']) },
        { label: 'Vote date', text: this.displayValue(value['voteDate']) },
        {
          label: 'Resolution',
          text: this.displayValue(value['title'] ?? value['resolutionTitle']),
        },
        { label: 'Published', text: this.displayValue(value['publishedAt']) },
      ].filter((field) => field.text !== '—');
    return [
      { label: 'Ballot', text: this.ballotLabel(value['ballot']) },
      { label: 'Country name', text: this.displayValue(value['countryName']) },
    ].filter((field) => field.text !== '—');
  }
}
