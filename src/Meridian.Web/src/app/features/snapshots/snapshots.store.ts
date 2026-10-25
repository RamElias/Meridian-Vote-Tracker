import { Injectable, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { SnapshotRecord, SnapshotResult, VoteContribution } from '../../models';

import { FeatureStore } from '../../shared/feature-store';

@Injectable({ providedIn: 'root' })
export class SnapshotsStore extends FeatureStore {
  readonly snapshots = signal<SnapshotRecord[]>([]);

  readonly selectedSnapshot = signal<SnapshotRecord | null>(null);

  readonly snapshotResults = signal<SnapshotResult[]>([]);

  readonly snapshotResultsHasMore = signal(false);

  readonly contributions = signal<VoteContribution[]>([]);

  readonly contributionsHasMore = signal(false);

  readonly snapshotSession = signal('');

  readonly snapshotFrom = signal('');

  readonly snapshotTo = signal('');

  readonly snapshotVoteIds = signal('');

  readonly publicationYear = signal(new Date().getFullYear());

  readonly publicationQuarter = signal(Math.floor(new Date().getMonth() / 3) + 1);

  readonly replaceActive = signal(false);

  readonly countryCode = signal('USA');

  readonly contributionsLoading = signal(false);

  private contributionRequest = 0;

  private snapshotRequest = 0;

  private requestedSnapshotId: string | null = null;

  async createSnapshot(): Promise<void> {
    const voteIds = this.snapshotVoteIds()
      .split(/[\s,]+/)
      .map((id) => id.trim())
      .filter(Boolean);
    const session = this.snapshotSession().trim();
    const fromDate = this.snapshotFrom().trim();
    const toDate = this.snapshotTo().trim();
    this.fieldErrors.set({});
    if (!voteIds.length && !session && !fromDate && !toDate) {
      this.fieldErrors.set({ voteIds: 'Choose a session, date range, or one or more vote IDs.' });
      return;
    }
    if (session && (!Number.isInteger(Number(session)) || Number(session) < 1)) {
      this.fieldErrors.set({ session: 'Session must be a positive integer.' });
      return;
    }
    if (fromDate && toDate && fromDate > toDate) {
      this.fieldErrors.set({ dateRange: 'From date must be on or before to date.' });
      return;
    }
    await this.run(async () => {
      const selection = {
        ...(session ? { session: Number(session) } : {}),
        ...(fromDate ? { fromDate } : {}),
        ...(toDate ? { toDate } : {}),
        ...(voteIds.length ? { voteIds } : {}),
      };
      const created = await firstValueFrom(this.api.createSnapshot(selection));
      this.notice.set(
        `Snapshot queued with ${created.selectedVoteCount} selected vote${created.selectedVoteCount === 1 ? '' : 's'}.`,
      );
      await Promise.all([this.loadSnapshots(), this.openSnapshotId(created.id)]);
    });
  }

  async openSnapshot(item: SnapshotRecord): Promise<void> {
    try {
      await this.openSnapshotId(item.id);
    } catch (error) {
      if (this.requestedSnapshotId === item.id) this.showError(error);
    }
  }

  private async openSnapshotId(id: string): Promise<void> {
    const request = ++this.snapshotRequest;
    if (this.requestedSnapshotId !== id) {
      this.resetContributions();
      this.snapshotResults.set([]);
      this.snapshotResultsHasMore.set(false);
      this.selectedSnapshot.set(null);
    }
    this.requestedSnapshotId = id;
    const details = await firstValueFrom(this.api.snapshot(id));
    if (request !== this.snapshotRequest) return;
    this.selectedSnapshot.set(details);
    if (details.publicationYear !== null) this.publicationYear.set(details.publicationYear);
    if (details.publicationQuarter !== null)
      this.publicationQuarter.set(details.publicationQuarter);
    if (details.status === 'Completed') {
      const results = await firstValueFrom(this.api.snapshotResults(id));
      if (request !== this.snapshotRequest) return;
      this.snapshotResults.set(results);
      this.snapshotResultsHasMore.set(results.length === 100);
    }
  }

  async loadMoreSnapshotResults(): Promise<void> {
    const snapshot = this.selectedSnapshot();
    if (!snapshot || !this.snapshotResultsHasMore()) return;
    const request = this.snapshotRequest;
    await this.run(async () => {
      const results = await firstValueFrom(
        this.api.snapshotResults(snapshot.id, this.snapshotResults().length),
      );
      if (request !== this.snapshotRequest || this.selectedSnapshot()?.id !== snapshot.id) return;
      this.snapshotResults.update((current) => [...current, ...results]);
      this.snapshotResultsHasMore.set(results.length === 100);
    });
  }

  readonly snapshotsHasMore = signal(false);
  readonly historyYear = signal<number | null>(null);
  readonly historyQuarter = signal<number | null>(null);
  readonly historyPublication = signal('');
  private historyRequest = 0;
  private historyLoading = false;

  async applyHistoryFilter(): Promise<void> {
    ++this.historyRequest;
    this.snapshots.set([]);
    this.snapshotsHasMore.set(false);
    await this.fetchSnapshotHistory(false);
  }

  async loadSnapshots(): Promise<void> {
    if (this.historyLoading) return;
    await this.fetchSnapshotHistory(false);
  }

  async loadMoreSnapshots(): Promise<void> {
    if (this.historyLoading || !this.snapshotsHasMore()) return;
    await this.fetchSnapshotHistory(true);
  }

  private async fetchSnapshotHistory(append: boolean): Promise<void> {
    const request = ++this.historyRequest;
    this.historyLoading = true;
    try {
      const offset = append ? this.snapshots().length : 0;
      const limit = append ? 100 : Math.min(200, Math.max(100, this.snapshots().length));
      const publication = this.historyPublication();
      if (
        publication &&
        !['Active', 'Withdrawn', 'Superseded', 'Unpublished'].includes(publication)
      )
        return;
      const items = await firstValueFrom(
        this.api.snapshots(
          offset,
          limit,
          this.historyYear() ?? undefined,
          this.historyQuarter() ?? undefined,
          publication
            ? (publication as 'Active' | 'Withdrawn' | 'Superseded' | 'Unpublished')
            : undefined,
        ),
      );
      if (request !== this.historyRequest) return;
      this.snapshots.set(
        append
          ? [...this.snapshots(), ...items]
          : [
              ...items,
              ...this.snapshots().filter((old) => !items.some((fresh) => fresh.id === old.id)),
            ],
      );
      this.snapshotsHasMore.set(items.length === limit);
      const selected = this.selectedSnapshot();
      if (selected && ['Queued', 'Running'].includes(selected.status))
        await this.openSnapshotId(selected.id);
    } catch (error) {
      if (request === this.historyRequest) this.showError(error);
    } finally {
      if (request === this.historyRequest) this.historyLoading = false;
    }
  }

  setCountryCode(code: string): void {
    this.countryCode.set(code);
    this.resetContributions();
  }

  private resetContributions(): void {
    this.contributionRequest++;
    this.contributions.set([]);
    this.contributionsHasMore.set(false);
    this.contributionsLoading.set(false);
  }

  async loadContributions(): Promise<void> {
    this.resetContributions();
    await this.fetchContributions(false);
  }

  async loadMoreContributions(): Promise<void> {
    if (!this.contributionsHasMore() || this.contributionsLoading()) return;
    await this.fetchContributions(true);
  }

  private async fetchContributions(append: boolean): Promise<void> {
    const snapshot = this.selectedSnapshot();
    const code = this.countryCode().trim().toUpperCase();
    if (!snapshot || snapshot.status !== 'Completed' || !/^[A-Z]{3}$/.test(code)) {
      this.fieldErrors.set({
        countryCode: 'Choose a completed snapshot and a three-letter country code.',
      });
      return;
    }
    const request = ++this.contributionRequest;
    const after = append ? (this.contributions().at(-1)?.ordinal ?? 0) : 0;
    this.contributionsLoading.set(true);
    try {
      const rows = await firstValueFrom(this.api.contributions(snapshot.id, code, after));
      if (
        request !== this.contributionRequest ||
        snapshot.id !== this.selectedSnapshot()?.id ||
        code !== this.countryCode().trim().toUpperCase()
      )
        return;
      this.contributions.update((current) => (append ? [...current, ...rows] : rows));
      this.contributionsHasMore.set(rows.length === 100);
    } catch (error) {
      if (request === this.contributionRequest) this.showError(error);
    } finally {
      if (request === this.contributionRequest) this.contributionsLoading.set(false);
    }
  }

  async publishSnapshot(): Promise<void> {
    const snapshot = this.selectedSnapshot();
    if (!snapshot || !this.isAdmin()) return;
    this.fieldErrors.set({});
    if (
      !Number.isInteger(Number(this.publicationYear())) ||
      this.publicationYear() < 1900 ||
      this.publicationYear() > 9999
    ) {
      this.fieldErrors.set({ publicationYear: 'Enter a year from 1900 to 9999.' });
      return;
    }
    await this.run(async () => {
      await firstValueFrom(
        this.api.publishSnapshot(
          snapshot.id,
          Number(this.publicationYear()),
          Number(this.publicationQuarter()),
          this.replaceActive(),
        ),
      );
      this.notice.set(
        `Snapshot published for ${this.publicationYear()} Q${this.publicationQuarter()}.`,
      );
      await Promise.all([this.loadSnapshots(), this.openSnapshotId(snapshot.id)]);
    });
  }

  async withdrawSnapshot(item: SnapshotRecord): Promise<void> {
    if (!this.isAdmin()) return;
    await this.run(async () => {
      await firstValueFrom(this.api.withdrawSnapshot(item.id));
      this.notice.set('Publication withdrawn; the snapshot remains in history.');
      await this.loadSnapshots();
    });
  }

  snapshotStatus(snapshot: SnapshotRecord): string {
    if (snapshot.supersededAt) return 'Superseded';
    if (snapshot.withdrawnAt) return 'Withdrawn';
    return snapshot.publishedAt ? 'Published' : snapshot.status;
  }

  isActivelyPublished(snapshot: SnapshotRecord): boolean {
    return !!snapshot.publishedAt && !snapshot.withdrawnAt && !snapshot.supersededAt;
  }
}
