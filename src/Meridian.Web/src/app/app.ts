import { HttpErrorResponse } from '@angular/common/http';
import { CommonModule } from '@angular/common';
import { Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { firstValueFrom } from 'rxjs';
import { ApiService } from './api.service';
import {
  ConflictRecord,
  ImportDetails,
  ImportRowDetail,
  ImportRecord,
  SnapshotRecord,
  SnapshotResult,
  VoteContribution,
  VoteDetails,
  VoteSummary,
} from './models';

type Section = 'imports' | 'review' | 'positions' | 'snapshots';

@Component({
  imports: [CommonModule, FormsModule],
  selector: 'app-root',
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App implements OnInit, OnDestroy {
  readonly api = inject(ApiService);
  readonly isAdmin = computed(() => this.api.demoUser() === 'lana');
  readonly processingCount = computed(() => this.imports().filter((item) => ['Queued', 'Running'].includes(item.status)).length);
  readonly completedImportCount = computed(() => this.imports().filter((item) => item.status === 'Completed').length);
  readonly section = signal<Section>('imports');
  readonly imports = signal<ImportRecord[]>([]);
  readonly importsHasMore = signal(false);
  readonly conflicts = signal<ConflictRecord[]>([]);
  readonly selectedConflictId = signal<string | null>(null);
  readonly selectedConflict = computed(() => this.conflicts().find((item) => item.id === this.selectedConflictId()) ?? null);
  readonly votes = signal<VoteSummary[]>([]);
  readonly votesHasMore = signal(false);
  readonly snapshots = signal<SnapshotRecord[]>([]);
  readonly selectedImport = signal<ImportDetails | null>(null);
  readonly importRows = signal<ImportRowDetail[]>([]);
  readonly importRowsHasMore = signal(false);
  readonly importRowFilter = signal('');
  readonly selectedVote = signal<VoteDetails | null>(null);
  readonly selectedSnapshot = signal<SnapshotRecord | null>(null);
  readonly snapshotResults = signal<SnapshotResult[]>([]);
  readonly contributions = signal<VoteContribution[]>([]);
  readonly contributionsHasMore = signal(false);
  readonly busy = signal(false);
  readonly error = signal('');
  readonly notice = signal('');
  readonly search = signal('');
  readonly positionStance = signal<'Y' | 'N'>('Y');
  readonly positionWeight = signal(3);
  readonly positionNote = signal('');
  readonly decisionNote = signal('');
  readonly conflictVoteFilter = signal('');
  readonly conflictStatus = signal<'Pending' | 'Resolved'>('Pending');
  readonly snapshotSession = signal('');
  readonly snapshotFrom = signal('');
  readonly snapshotTo = signal('');
  readonly snapshotVoteIds = signal('');
  readonly publicationYear = signal(new Date().getFullYear());
  readonly publicationQuarter = signal(Math.floor(new Date().getMonth() / 3) + 1);
  readonly replaceActive = signal(false);
  readonly countryCode = signal('USA');

  private selectedFile: File | null = null;
  private selectedProposalIndex = 0;
  private poller?: ReturnType<typeof setInterval>;

  async ngOnInit(): Promise<void> {
    await this.refreshAll();
    this.poller = setInterval(() => {
      if (this.imports().some((item) => ['Queued', 'Running'].includes(item.status))) void this.loadImports();
      if (this.snapshots().some((item) => ['Queued', 'Running'].includes(item.status))) void this.loadSnapshots();
    }, 2500);
  }

  ngOnDestroy(): void {
    if (this.poller) clearInterval(this.poller);
  }

  async refreshAll(): Promise<void> {
    this.error.set('');
    await Promise.all([this.loadImports(), this.loadConflicts(), this.loadVotes(), this.loadSnapshots()]);
  }

  async chooseSection(section: Section): Promise<void> {
    this.section.set(section);
    this.error.set('');
    if (section === 'imports') await this.loadImports();
    if (section === 'review') await this.loadConflicts();
    if (section === 'positions') await this.loadVotes();
    if (section === 'snapshots') await this.loadSnapshots();
  }

  setUser(user: string): void {
    this.api.setDemoUser(user);
    this.selectedVote.set(null);
    this.closeImportDetails();
    void this.refreshAll();
  }

  onFileSelected(event: Event): void {
    this.selectedFile = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.notice.set('');
  }

  async upload(): Promise<void> {
    if (!this.selectedFile) return this.showError('Choose a CSV file first.');
    await this.run(async () => {
      const uploaded = await firstValueFrom(this.api.upload(this.selectedFile!));
      this.notice.set(uploaded.originalImportId ? 'This file already exists; history was updated without reprocessing.' : 'Import queued. Progress continues if you leave this page.');
      this.selectedFile = null;
      await this.loadImports();
    });
  }

  async openImport(item: ImportRecord): Promise<void> {
    await this.run(async () => {
      this.selectedImport.set(await firstValueFrom(this.api.importDetails(item.id)));
      this.importRowFilter.set('');
      const rows = await firstValueFrom(this.api.importRows(item.id));
      this.importRows.set(rows);
      this.importRowsHasMore.set(rows.length === 100);
    });
  }

  async setImportRowFilter(classification: string): Promise<void> {
    this.importRowFilter.set(classification);
    const id = this.selectedImport()?.id;
    if (id) await this.run(async () => {
      const rows = await firstValueFrom(this.api.importRows(id, classification));
      this.importRows.set(rows);
      this.importRowsHasMore.set(rows.length === 100);
    });
  }

  async loadMoreImportRows(): Promise<void> {
    const id = this.selectedImport()?.id;
    const lastRow = this.importRows().at(-1)?.rowNumber;
    if (!id || lastRow === undefined) return;
    await this.run(async () => {
      const rows = await firstValueFrom(this.api.importRows(id, this.importRowFilter(), lastRow));
      this.importRows.update(current => [...current, ...rows]);
      this.importRowsHasMore.set(rows.length === 100);
    });
  }

  closeImportDetails(): void {
    this.selectedImport.set(null);
    this.importRows.set([]);
    this.importRowFilter.set('');
  }

  importSummary(): { key: string; label: string; count: number }[] {
    const summary = this.selectedImport()?.report?.summary ?? {};
    return [
      { key: 'New', label: 'New records', count: Number(summary['New'] ?? 0) },
      { key: 'NoChange', label: 'Unchanged', count: Number(summary['NoChange'] ?? 0) },
      { key: 'Duplicate', label: 'Duplicates', count: Number(summary['Duplicate'] ?? 0) },
      { key: 'Invalid', label: 'Invalid rows', count: Number(summary['Invalid'] ?? 0) },
      { key: 'Contradiction', label: 'Contradictions', count: Number(summary['Contradiction'] ?? 0) },
      { key: 'Conflict', label: 'Needs review', count: Number(summary['Conflict'] ?? 0) },
    ];
  }

  importVoteRows(): { voteId: string; counts: { key: string; label: string; count: number }[]; total: number }[] {
    const votes = this.selectedImport()?.report?.votes ?? {};
    return Object.entries(votes).map(([voteId, rawCounts]) => {
      const counts = Object.entries(rawCounts).map(([key, count]) => ({ key, label: this.outcomeLabel(key), count: Number(count) }));
      return { voteId, counts, total: counts.reduce((sum, item) => sum + item.count, 0) };
    });
  }

  outcomeLabel(key: string): string {
    const labels: Record<string, string> = { New: 'New', NoChange: 'Unchanged', Duplicate: 'Duplicate', Invalid: 'Invalid', Contradiction: 'Contradiction', Conflict: 'Needs review', Unclassified: 'Unclassified' };
    return labels[key] ?? key;
  }

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
      if (typeof item['stance'] === 'string') return `${this.stanceLabel(item['stance'])} · weight ${this.displayValue(item['weight'])}`;
      if (typeof item['ballot'] === 'string') return `${this.ballotLabel(item['ballot'])}${item['countryName'] ? ` · ${item['countryName']}` : ''}`;
      if (item['voteDate'] || item['title'] || item['resolutionTitle']) return `${item['title'] ?? item['resolutionTitle'] ?? 'Vote metadata'}${item['voteDate'] ? ` · ${item['voteDate']}` : ''}`;
      return 'Vote details updated';
    };
    return `${describe(entry.oldValue)} → ${describe(entry.newValue)}`;
  }

  historyActionLabel(action: string): string {
    const labels: Record<string, string> = {
      PositionSet: 'Position set',
      PositionChanged: 'Position changed',
      ConflictAccepted: 'Conflict proposal accepted',
      ConflictRejected: 'Conflict proposals rejected',
      VoteMetadataAccepted: 'Vote details approved',
      CountryVoteAccepted: 'Country ballot approved',
    };
    return labels[action] ?? action;
  }

  conflictFields(targetType: string, value: Record<string, unknown>): { label: string; text: string }[] {
    if (targetType === 'VoteMetadata') return [
      { label: 'Session', text: this.displayValue(value['session']) },
      { label: 'Vote date', text: this.displayValue(value['voteDate']) },
      { label: 'Resolution', text: this.displayValue(value['title'] ?? value['resolutionTitle']) },
      { label: 'Published', text: this.displayValue(value['publishedAt']) },
    ].filter(field => field.text !== '—');
    return [
      { label: 'Ballot', text: this.ballotLabel(value['ballot']) },
      { label: 'Country name', text: this.displayValue(value['countryName']) },
    ].filter(field => field.text !== '—');
  }

  async loadImports(): Promise<void> {
    try {
      const page = await firstValueFrom(this.api.imports());
      this.imports.set(page);
      this.importsHasMore.set(page.length === 100);
    }
    catch (error) { this.showError(error); }
  }

  async loadMoreImports(): Promise<void> {
    await this.run(async () => {
      const page = await firstValueFrom(this.api.imports(this.imports().length));
      this.imports.update(current => [...current, ...page]);
      this.importsHasMore.set(page.length === 100);
    });
  }

  async loadConflicts(): Promise<void> {
    try {
      const conflicts = await firstValueFrom(this.api.conflicts(this.conflictVoteFilter(), this.conflictStatus()));
      this.conflicts.set(conflicts);
      if (!conflicts.some((item) => item.id === this.selectedConflictId())) this.selectedConflictId.set(conflicts[0]?.id ?? null);
    }
    catch (error) { this.showError(error); }
  }

  selectConflict(id: string): void {
    this.selectedConflictId.set(id);
    this.decisionNote.set('');
  }

  async searchConflicts(): Promise<void> {
    this.selectedConflictId.set(null);
    await this.loadConflicts();
  }

  async decide(conflict: ConflictRecord, action: 'Accept' | 'Reject', proposalIndex?: number): Promise<void> {
    this.selectedProposalIndex = proposalIndex ?? 0;
    await this.run(async () => {
      try {
        await firstValueFrom(this.api.decideConflict(conflict.id, action, action === 'Accept' ? this.selectedProposalIndex : undefined, this.decisionNote()));
      } catch (error) {
        if (error instanceof HttpErrorResponse && error.status === 409) await this.loadConflicts();
        throw error;
      }
      this.notice.set(action === 'Accept' ? 'Proposal accepted and audited.' : 'Proposals rejected and audited.');
      await Promise.all([this.loadConflicts(), this.loadImports()]);
    });
  }

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
      this.votes.update(current => [...current, ...page]);
      this.votesHasMore.set(page.length === 100);
    });
  }

  async openVote(item: VoteSummary): Promise<void> {
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
    await this.run(async () => {
      await firstValueFrom(this.api.setPosition(voteId, this.positionStance(), Number(this.positionWeight()), this.positionNote()));
      this.notice.set('Position saved as a new version.');
      await Promise.all([this.openVote({ voteId } as VoteSummary), this.loadVotes()]);
    });
  }

  async loadVotes(): Promise<void> {
    try {
      const page = await firstValueFrom(this.api.votes(this.search()));
      this.votes.set(page);
      this.votesHasMore.set(page.length === 100);
    }
    catch (error) { this.showError(error); }
  }

  async createSnapshot(): Promise<void> {
    const voteIds = this.snapshotVoteIds().split(/[\s,]+/).map((id) => id.trim()).filter(Boolean);
    const session = this.snapshotSession().trim();
    const fromDate = this.snapshotFrom().trim();
    const toDate = this.snapshotTo().trim();
    if (!voteIds.length && !session && !fromDate && !toDate) return this.showError('Choose a session, date range, or one or more vote IDs.');
    await this.run(async () => {
      const selection = {
        ...(session ? { session: Number(session) } : {}),
        ...(fromDate ? { fromDate } : {}),
        ...(toDate ? { toDate } : {}),
        ...(voteIds.length ? { voteIds } : {}),
      };
      const created = await firstValueFrom(this.api.createSnapshot(selection));
      this.notice.set(`Snapshot queued with ${created.selectedVoteCount} selected vote${created.selectedVoteCount === 1 ? '' : 's'}.`);
      await Promise.all([this.loadSnapshots(), this.openSnapshotId(created.id)]);
    });
  }

  async openSnapshot(item: SnapshotRecord): Promise<void> {
    await this.openSnapshotId(item.id);
  }

  private async openSnapshotId(id: string): Promise<void> {
    const details = await firstValueFrom(this.api.snapshot(id));
    this.selectedSnapshot.set(details);
    if (details.publicationYear !== null) this.publicationYear.set(details.publicationYear);
    if (details.publicationQuarter !== null) this.publicationQuarter.set(details.publicationQuarter);
    if (details.status === 'Completed') this.snapshotResults.set(await firstValueFrom(this.api.snapshotResults(id)));
    else this.snapshotResults.set([]);
  }

  async loadSnapshots(): Promise<void> {
    try {
      const items = await firstValueFrom(this.api.snapshots());
      this.snapshots.set(items);
      const selected = this.selectedSnapshot();
      if (selected && ['Queued', 'Running'].includes(selected.status)) await this.openSnapshotId(selected.id);
    } catch (error) { this.showError(error); }
  }

  async loadContributions(): Promise<void> {
    const snapshot = this.selectedSnapshot();
    const code = this.countryCode().trim().toUpperCase();
    if (!snapshot || code.length !== 3) return this.showError('Choose a completed snapshot and a three-letter country code.');
    await this.run(async () => {
      const rows = await firstValueFrom(this.api.contributions(snapshot.id, code));
      this.contributions.set(rows);
      this.contributionsHasMore.set(rows.length === 100);
    });
  }

  async loadMoreContributions(): Promise<void> {
    const snapshot = this.selectedSnapshot();
    const code = this.countryCode().trim().toUpperCase();
    const lastOrdinal = this.contributions().at(-1)?.ordinal;
    if (!snapshot || lastOrdinal === undefined) return;
    await this.run(async () => {
      const rows = await firstValueFrom(this.api.contributions(snapshot.id, code, lastOrdinal));
      this.contributions.update(current => [...current, ...rows]);
      this.contributionsHasMore.set(rows.length === 100);
    });
  }

  async publishSnapshot(): Promise<void> {
    const snapshot = this.selectedSnapshot();
    if (!snapshot || !this.isAdmin()) return;
    await this.run(async () => {
      await firstValueFrom(this.api.publishSnapshot(snapshot.id, Number(this.publicationYear()), Number(this.publicationQuarter()), this.replaceActive()));
      this.notice.set(`Snapshot published for ${this.publicationYear()} Q${this.publicationQuarter()}.`);
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

  progress(item: ImportRecord | SnapshotRecord): number {
    if ('rowsTotal' in item) return item.rowsTotal ? Math.min(100, Math.round((item.rowsProcessed / item.rowsTotal) * 100)) : 0;
    return item.progressTotal ? Math.min(100, Math.round((item.progressCurrent / item.progressTotal) * 100)) : 0;
  }

  hasSelectedFile(): boolean { return this.selectedFile !== null; }

  selectedFileName(): string { return this.selectedFile?.name ?? 'Drop your CSV here'; }

  statusClass(status: string): string {
    if (['Completed', 'Published', 'New', 'NoChange', 'Accepted'].includes(status)) return 'status-success';
    if (['Failed', 'Invalid', 'Contradiction'].includes(status)) return 'status-danger';
    if (['Running', 'Queued', 'Conflict'].includes(status)) return 'status-running';
    return 'status-neutral';
  }

  snapshotStatus(snapshot: SnapshotRecord): string {
    if (snapshot.supersededAt) return 'Superseded';
    if (snapshot.withdrawnAt) return 'Withdrawn';
    return snapshot.publishedAt ? 'Published' : snapshot.status;
  }

  isActivelyPublished(snapshot: SnapshotRecord): boolean {
    return !!snapshot.publishedAt && !snapshot.withdrawnAt && !snapshot.supersededAt;
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

  private async run(action: () => Promise<unknown>): Promise<void> {
    this.busy.set(true);
    this.error.set('');
    try { await action(); }
    catch (error) { this.showError(error); }
    finally { this.busy.set(false); }
  }

  private showError(error: unknown): void {
    if (typeof error === 'string') { this.error.set(error); return; }
    if (error instanceof HttpErrorResponse) {
      const body = error.error as { detail?: string; title?: string } | null;
      this.error.set(body?.detail ?? body?.title ?? error.message);
      return;
    }
    this.error.set('The request could not be completed. Check the API connection and try again.');
  }
}
