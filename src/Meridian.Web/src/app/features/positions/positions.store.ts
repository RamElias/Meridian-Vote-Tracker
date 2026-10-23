import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { VoteDetails, VoteSummary } from '../../models';

import { FeatureStore } from '../../shared/feature-store';

@Injectable({ providedIn: 'root' })
export class PositionsStore extends FeatureStore {
  readonly votes = signal<VoteSummary[]>([]);

  readonly votesHasMore = signal(false);

  readonly selectedVote = signal<VoteDetails | null>(null);

  readonly search = signal('');

  readonly positionStance = signal<'Y' | 'N'>('Y');

  readonly positionWeight = signal(3);

  readonly positionNote = signal('');

  async searchVotes(): Promise<void> {
    await this.run(async () => {
      const page = await firstValueFrom(this.api.votes(this.search()));
      this.votes.set(page);
      this.votesHasMore.set(page.length === 100);
    });
  }

  async loadMoreVotes(): Promise<void> {
    await this.run(async () => {
      const page = await firstValueFrom(this.api.votes(this.search(), this.votes().length));
      this.votes.update((current) => [...current, ...page]);
      this.votesHasMore.set(page.length === 100);
    });
  }

  async openVote(item: Pick<VoteSummary, 'voteId'>): Promise<void> {
    await this.run(async () => {
      const vote = await firstValueFrom(this.api.vote(item.voteId));
      this.selectedVote.set(vote);
      this.positionStance.set(vote.vote.position?.stance === 'N' ? 'N' : 'Y');
      this.positionWeight.set(vote.vote.position?.weight ?? 3);
      this.positionNote.set(vote.vote.position?.note ?? '');
    });
  }

  async savePosition(): Promise<void> {
    const voteId = this.selectedVote()?.vote.voteId;
    if (!voteId) return;
    const weight = Number(this.positionWeight());
    this.fieldErrors.set({});
    if (!Number.isInteger(weight) || weight < 1 || weight > 5) {
      this.fieldErrors.set({ weight: 'Weight must be an integer between 1 and 5.' });
      return;
    }
    await this.run(async () => {
      try {
        await firstValueFrom(
          this.api.setPosition(
            voteId,
            this.positionStance(),
            weight,
            this.positionNote(),
            this.selectedVote()?.vote.position?.versionId ?? null,
          ),
        );
      } catch (error) {
        if (error instanceof HttpErrorResponse && error.status === 409)
          await this.openVote({ voteId });
        throw error;
      }
      this.notice.set('Position saved as a new version.');
      await Promise.all([this.openVote({ voteId }), this.loadVotes()]);
    });
  }

  async loadVotes(): Promise<void> {
    try {
      const page = await firstValueFrom(this.api.votes(this.search()));
      this.votes.set(page);
      this.votesHasMore.set(page.length === 100);
    } catch (error) {
      this.showError(error);
    }
  }
}
