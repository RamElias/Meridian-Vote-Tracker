import { HttpErrorResponse } from '@angular/common/http';
import { computed, inject, signal } from '@angular/core';

import { ApiService } from '../api.service';
import { ImportRecord, SnapshotRecord } from '../models';

// Shared presentation and request feedback only; feature data belongs to each screen store.
export abstract class FeatureStore {
  readonly api = inject(ApiService);

  readonly isAdmin = computed(() => this.api.demoUser() === 'lana');

  readonly busy = signal(false);
  private pendingRequests = 0;

  readonly error = signal('');

  readonly notice = signal('');

  readonly fieldErrors = signal<Record<string, string>>({});

  ballotLabel(value: unknown): string {
    const ballot = String(value ?? '').toUpperCase();
    const labels: Record<string, string> = { Y: 'Yes', N: 'No', A: 'Abstain', X: 'Absent' };
    return labels[ballot] ? `${labels[ballot]} (${ballot})` : ballot || 'Not recorded';
  }

  stanceLabel(value: string | null | undefined): string {
    if (value === 'Y') return 'For (Y)';
    if (value === 'N') return 'Against (N)';
    return 'Not set';
  }

  historyChange(entry: { oldValue: unknown; newValue: unknown }): string {
    const describe = (value: unknown): string => {
      if (!value || typeof value !== 'object' || Array.isArray(value)) return 'Initial value';
      const item = value as Record<string, unknown>;
      if (typeof item['unchangedRows'] === 'number')
        return `${item['unchangedRows']} unchanged rows`;
      if (typeof item['stance'] === 'string')
        return `${this.stanceLabel(item['stance'])} · weight ${this.displayValue(item['weight'])}`;
      if (typeof item['ballot'] === 'string')
        return `${this.ballotLabel(item['ballot'])}${item['countryName'] ? ` · ${item['countryName']}` : ''}`;
      if (item['voteDate'] || item['title'] || item['resolutionTitle'])
        return `${item['title'] ?? item['resolutionTitle'] ?? 'Vote metadata'}${item['voteDate'] ? ` · ${item['voteDate']}` : ''}`;
      return 'Vote details updated';
    };
    return `${describe(entry.oldValue)} → ${describe(entry.newValue)}`;
  }

  historyActionLabel(action: string): string {
    const labels: Record<string, string> = {
      VoteImported: 'Vote imported',
      CountryBallotImported: 'Country ballot imported',
      ImportUnchanged: 'Unchanged import verified',
      PositionSet: 'Position set',
      PositionChanged: 'Position changed',
      ConflictAccepted: 'Conflict proposal accepted',
      ConflictRejected: 'Conflict proposals rejected',
      VoteMetadataAccepted: 'Vote details approved',
      CountryVoteAccepted: 'Country ballot approved',
    };
    return labels[action] ?? action;
  }

  progress(item: ImportRecord | SnapshotRecord): number {
    if (item.status === 'Completed') return 100;
    if (item.progress?.total)
      return Math.min(100, Math.round((100 * item.progress.current) / item.progress.total));
    if ('rowsTotal' in item)
      return item.rowsTotal
        ? Math.min(100, Math.round((item.rowsProcessed / item.rowsTotal) * 100))
        : 0;
    return item.progressTotal
      ? Math.min(100, Math.round((item.progressCurrent / item.progressTotal) * 100))
      : 0;
  }

  statusClass(status: string): string {
    if (['Completed', 'Published', 'New', 'NoChange', 'Accepted'].includes(status))
      return 'status-success';
    if (['Failed', 'Invalid', 'Contradiction'].includes(status)) return 'status-danger';
    if (['Running', 'Queued', 'Conflict'].includes(status)) return 'status-running';
    return 'status-neutral';
  }

  formatPercent(value: number | null): string {
    return value === null ? 'N/A' : `${value.toFixed(1)}%`;
  }

  displayValue(value: unknown): string {
    if (value === null || value === undefined || value === '') return '—';
    if (typeof value === 'object') return '—';
    return String(value);
  }

  clearMessages(): void {
    this.error.set('');
    this.notice.set('');
  }

  protected async run(action: () => Promise<unknown>): Promise<void> {
    this.pendingRequests++;
    this.busy.set(true);
    this.error.set('');
    try {
      await action();
    } catch (error) {
      this.showError(error);
    } finally {
      this.busy.set(--this.pendingRequests > 0);
    }
  }

  protected showError(error: unknown): void {
    if (typeof error === 'string') {
      this.error.set(error);
      return;
    }
    if (error instanceof HttpErrorResponse) {
      const body = error.error as {
        detail?: string;
        title?: string;
        correlationId?: string;
        unknownVoteIds?: string[];
        votesWithoutPosition?: string[];
      } | null;
      const message = body?.detail ?? body?.title ?? error.message;
      const reference = body?.correlationId ?? error.headers.get('X-Correlation-ID');
      if (body?.unknownVoteIds?.length || body?.votesWithoutPosition?.length)
        this.fieldErrors.set({
          voteIds: message + ' ' + (body.unknownVoteIds ?? body.votesWithoutPosition)?.join(', '),
        });
      this.error.set(message + (reference ? ` (Reference: ${reference})` : ''));
      return;
    }
    this.error.set('The request could not be completed. Check the API connection and try again.');
  }
}
