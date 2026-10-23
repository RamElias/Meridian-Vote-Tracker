import { Injectable, inject, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { ConflictsService } from './generated-api/services/conflicts.service';
import { ImportsService } from './generated-api/services/imports.service';
import { SnapshotsService } from './generated-api/services/snapshots.service';
import { VotesService } from './generated-api/services/votes.service';
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

/** UI-facing facade over the OpenAPI-generated Angular client. */
@Injectable({ providedIn: 'root' })
export class ApiService {
  private readonly importsApi = inject(ImportsService);
  private readonly conflictsApi = inject(ConflictsService);
  private readonly votesApi = inject(VotesService);
  private readonly snapshotsApi = inject(SnapshotsService);
  readonly demoUser = signal(localStorage.getItem('meridian-demo-user') ?? 'lana');

  setDemoUser(user: string): void {
    this.demoUser.set(user);
    localStorage.setItem('meridian-demo-user', user);
  }

  imports(offset = 0, limit = 100): Observable<ImportRecord[]> {
    return this.importsApi.listImports({ offset, limit });
  }

  importDetails(id: string): Observable<ImportDetails> {
    return this.importsApi.getImport({ id });
  }

  importRows(id: string, classification = '', after = 0): Observable<ImportRowDetail[]> {
    return this.importsApi.listImportRows({ id, limit: 100, after, classification: classification || undefined });
  }

  upload(file: File): Observable<{ importId: string; status: string; originalImportId: string | null }> {
    return this.importsApi.createImport({ body: { file } }).pipe(map(result => ({
      importId: result.importId,
      status: result.status,
      originalImportId: result.originalImportId ?? null,
    })));
  }

  conflicts(voteId = '', status = 'Pending'): Observable<ConflictRecord[]> {
    if (status !== 'Pending' && status !== 'Resolved' && status !== 'Accepted' && status !== 'Rejected' && status !== 'All') throw new Error('Invalid conflict status');
    return this.conflictsApi.listPendingConflicts({ voteId: voteId.trim() || undefined, status });
  }

  decideConflict(id: string, action: 'Accept' | 'Reject', proposalIndex?: number, note?: string): Observable<{ id: string; status: string }> {
    return this.conflictsApi.decideConflict({ id, body: { action, proposalIndex, note } }).pipe(map(result => ({ id: result.id, status: result.status })));
  }

  votes(search = '', offset = 0, limit = 100): Observable<VoteSummary[]> {
    return this.votesApi.listVotes({ search: search.trim() || undefined, offset, limit });
  }

  vote(voteId: string): Observable<VoteDetails> {
    return this.votesApi.getVote({ voteId });
  }

  setPosition(voteId: string, stance: 'Y' | 'N', weight: number, note: string, expectedVersionId: number | null): Observable<unknown> {
    return this.votesApi.setVotePosition({ voteId, body: { stance, weight, note, expectedVersionId } });
  }

  snapshots(): Observable<SnapshotRecord[]> {
    return this.snapshotsApi.listSnapshots();
  }

  createSnapshot(selection: { session?: number; fromDate?: string; toDate?: string; voteIds?: string[] }): Observable<{ id: string; status: string; selectedVoteCount: number }> {
    return this.snapshotsApi.createSnapshot({ body: selection }).pipe(map(result => ({
      id: result.id,
      status: result.status,
      selectedVoteCount: result.selectedVoteCount,
    })));
  }

  snapshot(id: string): Observable<SnapshotRecord> {
    return this.snapshotsApi.getSnapshot({ id });
  }

  snapshotResults(id: string, offset = 0, limit = 100): Observable<SnapshotResult[]> {
    return this.snapshotsApi.listSnapshotResults({ id, offset, limit });
  }

  contributions(id: string, countryCode: string, after = 0): Observable<VoteContribution[]> {
    return this.snapshotsApi.listSnapshotContributions({ id, countryCode, after, limit: 100 });
  }

  publishSnapshot(id: string, year: number, quarter: number, replaceActive: boolean): Observable<unknown> {
    return this.snapshotsApi.publishSnapshot({ id, body: { year, quarter, replaceActive } });
  }

  withdrawSnapshot(id: string): Observable<unknown> {
    return this.snapshotsApi.withdrawSnapshot({ id });
  }
}
