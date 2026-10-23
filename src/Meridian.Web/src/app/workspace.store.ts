import { HttpErrorResponse } from '@angular/common/http';
import { Injectable, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
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

@Injectable({ providedIn: 'root' })
export class WorkspaceStore implements OnInit, OnDestroy {
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
  readonly snapshotResultsHasMore = signal(false);
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

  readonly fieldErrors = signal<Record<string,string>>({});
  readonly contributionsLoading = signal(false);
  private contributionRequest = 0;
  private snapshotRequest = 0;
  private requestedSnapshotId: string | null = null;
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
    this.fieldErrors.set({});
    if (!this.selectedFile) { this.fieldErrors.set({file:'Choose a CSV file first.'}); return; }
    if (!this.selectedFile.name.toLowerCase().endsWith('.csv') || this.selectedFile.size>150*1024*1024) { this.fieldErrors.set({file:'Choose a CSV file up to 150 MB.'}); return; }
    await this.run(async () => {
      const uploaded = await firstValueFrom(this.api.upload(this.selectedFile!));
      this.notice.set(uploaded.originalImportId ? 'This file already exists; history was updated without reprocessing.' : 'Import queued. Progress continues if you leave this page.');
      this.selectedFile = null;
      await this.loadImports();
    });
  }

  async openImport(item: Pick<ImportRecord,'id'>): Promise<void> {
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
      if (typeof item['unchangedRows'] === 'number') return `${item['unchangedRows']} unchanged rows`;
      if (typeof item['stance'] === 'string') return `${this.stanceLabel(item['stance'])} · weight ${this.displayValue(item['weight'])}`;
      if (typeof item['ballot'] === 'string') return `${this.ballotLabel(item['ballot'])}${item['countryName'] ? ` · ${item['countryName']}` : ''}`;
      if (item['voteDate'] || item['title'] || item['resolutionTitle']) return `${item['title'] ?? item['resolutionTitle'] ?? 'Vote metadata'}${item['voteDate'] ? ` · ${item['voteDate']}` : ''}`;
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
      const selected = this.selectedImport();
      if (selected && ['Queued','Running'].includes(selected.status)) {
        const details=await firstValueFrom(this.api.importDetails(selected.id));
        if (this.selectedImport()?.id !== selected.id) return;
        this.selectedImport.set(details);
        if (details.status==='Completed') {
          const rows=await firstValueFrom(this.api.importRows(selected.id,this.importRowFilter()));
          if (this.selectedImport()?.id !== selected.id) return;
          this.importRows.set(rows);
          this.importRowsHasMore.set(rows.length===100);
        }
      }
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

  async openVote(item: Pick<VoteSummary,'voteId'>): Promise<void> {
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
    if (!Number.isInteger(weight) || weight < 1 || weight > 5) { this.fieldErrors.set({weight:'Weight must be an integer between 1 and 5.'}); return; }
    await this.run(async () => {
      try {
        await firstValueFrom(this.api.setPosition(voteId, this.positionStance(), weight, this.positionNote(), this.selectedVote()?.vote.position?.versionId ?? null));
      } catch (error) {
        if (error instanceof HttpErrorResponse && error.status === 409) await this.openVote({voteId});
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
    }
    catch (error) { this.showError(error); }
  }

  async createSnapshot(): Promise<void> {
    const voteIds = this.snapshotVoteIds().split(/[\s,]+/).map((id) => id.trim()).filter(Boolean);
    const session = this.snapshotSession().trim();
    const fromDate = this.snapshotFrom().trim();
    const toDate = this.snapshotTo().trim();
    this.fieldErrors.set({});
    if (!voteIds.length && !session && !fromDate && !toDate) { this.fieldErrors.set({voteIds:'Choose a session, date range, or one or more vote IDs.'}); return; }
    if (session && (!Number.isInteger(Number(session)) || Number(session)<1)) { this.fieldErrors.set({session:'Session must be a positive integer.'}); return; }
    if (fromDate && toDate && fromDate>toDate) { this.fieldErrors.set({dateRange:'From date must be on or before to date.'}); return; }
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
    try { await this.openSnapshotId(item.id); }
    catch(error) { if (this.requestedSnapshotId===item.id) this.showError(error); }
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
    if (details.publicationQuarter !== null) this.publicationQuarter.set(details.publicationQuarter);
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
    const request=this.snapshotRequest;
    await this.run(async () => {
      const results = await firstValueFrom(this.api.snapshotResults(snapshot.id, this.snapshotResults().length));
      if (request!==this.snapshotRequest || this.selectedSnapshot()?.id !== snapshot.id) return;
      this.snapshotResults.update(current => [...current, ...results]);
      this.snapshotResultsHasMore.set(results.length === 100);
    });
  }

  async loadSnapshots(): Promise<void> {
    try {
      const items = await firstValueFrom(this.api.snapshots());
      this.snapshots.set(items);
      const selected = this.selectedSnapshot();
      if (selected && ['Queued', 'Running'].includes(selected.status)) await this.openSnapshotId(selected.id);
    } catch (error) { this.showError(error); }
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
      this.fieldErrors.set({countryCode:'Choose a completed snapshot and a three-letter country code.'});
      return;
    }
    const request = ++this.contributionRequest;
    const after = append ? this.contributions().at(-1)?.ordinal ?? 0 : 0;
    this.contributionsLoading.set(true);
    try {
      const rows = await firstValueFrom(this.api.contributions(snapshot.id, code, after));
      if (request !== this.contributionRequest || snapshot.id !== this.selectedSnapshot()?.id || code !== this.countryCode().trim().toUpperCase()) return;
      this.contributions.update(current => append ? [...current, ...rows] : rows);
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
    if (!Number.isInteger(Number(this.publicationYear())) || this.publicationYear()<1900 || this.publicationYear()>9999) { this.fieldErrors.set({publicationYear: 'Enter a year from 1900 to 9999.'}); return; }
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

  normalizationChanges(row: ImportRowDetail): string {
    return Object.entries(row.normalizations).map(([field,change]) => {
      if (!change || typeof change !== 'object') return field;
      const pair=change as Record<string,unknown>;
      return `${field}: ${JSON.stringify(pair['original'])} → ${JSON.stringify(pair['normalized'])}`;
    }).join('; ');
  }

  progress(item: ImportRecord | SnapshotRecord): number {
    if (item.status==='Completed') return 100;
    if (item.progress?.total) return Math.min(100,Math.round(100*item.progress.current/item.progress.total));
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
      const body = error.error as { detail?: string; title?: string; correlationId?: string; unknownVoteIds?: string[]; votesWithoutPosition?: string[] } | null;
      const message = body?.detail ?? body?.title ?? error.message;
      const reference = body?.correlationId ?? error.headers.get('X-Correlation-ID');
      if (body?.unknownVoteIds?.length || body?.votesWithoutPosition?.length) this.fieldErrors.set({voteIds: message + ' ' + (body.unknownVoteIds ?? body.votesWithoutPosition)?.join(', ')});
      this.error.set(message + (reference ? ` (Reference: ${reference})` : ''));
      return;
    }
    this.error.set('The request could not be completed. Check the API connection and try again.');
  }
}
