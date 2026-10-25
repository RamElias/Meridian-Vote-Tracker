import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Subject, of } from 'rxjs';
import { ApiService } from './api.service';
import { SnapshotsStore } from './features/snapshots/snapshots.store';
import { SnapshotsPage } from './features/snapshots/snapshots.page';
import { SnapshotRecord, VoteContribution } from './models';

const snapshot = (id: string): SnapshotRecord => ({
  id,
  status: 'Completed',
  ruleVersion: 'alignment-v1',
  createdBy: 'lana',
  createdAt: '',
  completedAt: '',
  publicationYear: null,
  publicationQuarter: null,
  publishedAt: null,
  withdrawnAt: null,
  supersededAt: null,
  progressCurrent: 1,
  progressTotal: 1,
  selectedVoteCount: 1,
  error: null,
});
const contribution = (voteId: string): VoteContribution => ({
  ordinal: 1,
  voteId,
  voteDate: '2024-01-01',
  title: voteId,
  instituteStance: 'Y',
  weight: 1,
  memberAtVote: true,
  ballot: 'Y',
  alignmentNumerator: 1,
  alignmentDenominator: 1,
  participationNumerator: 1,
  participationDenominator: 1,
  coverageNumerator: 1,
  coverageDenominator: 1,
});

describe('Snapshot contribution selection', () => {
  const api = {
    demoUser: signal('lana'),
    contributions: vi.fn(),
    snapshot: vi.fn(),
    snapshotResults: vi.fn(() => of([])),
  };
  beforeEach(() => {
    vi.clearAllMocks();
    TestBed.configureTestingModule({
      imports: [SnapshotsPage],
      providers: [{ provide: ApiService, useValue: api }],
    });
  });
  it('clears pagination and ignores a late response after the country changes', async () => {
    const fixture = TestBed.createComponent(SnapshotsPage);
    const state = TestBed.inject(SnapshotsStore);
    state.selectedSnapshot.set(snapshot('one'));
    state.setCountryCode('AAA');
    const late = new Subject<VoteContribution[]>();
    api.contributions
      .mockReturnValueOnce(late)
      .mockReturnValueOnce(of([contribution('new-country')]));
    const previous = fixture.componentInstance.openContributionDialog('AAA');
    state.contributionsHasMore.set(true);
    state.setCountryCode('BBB');
    expect(state.contributions()).toEqual([]);
    expect(state.contributionsHasMore()).toBe(false);
    await state.loadContributions();
    late.next([contribution('old-country')]);
    late.complete();
    await previous;
    fixture.detectChanges();
    expect(fixture.nativeElement.textContent).toContain('new-country');
    expect(fixture.nativeElement.textContent).not.toContain('old-country');
    expect(state.contributions().map((row) => row.voteId)).toEqual(['new-country']);
  });
  it('clears old contributions when opening another snapshot and ignores late snapshot details', async () => {
    const fixture = TestBed.createComponent(SnapshotsPage);
    const state = TestBed.inject(SnapshotsStore);
    state.selectedSnapshot.set(snapshot('initial'));
    state.contributions.set([contribution('old-snapshot')]);
    state.contributionsHasMore.set(true);
    const late = new Subject<SnapshotRecord>();
    api.snapshot.mockReturnValueOnce(late).mockReturnValueOnce(of(snapshot('new')));
    const previous = state.openSnapshot(snapshot('old'));
    expect(state.contributions()).toEqual([]);
    expect(state.contributionsHasMore()).toBe(false);
    await state.openSnapshot(snapshot('new'));
    late.next(snapshot('old'));
    late.complete();
    await previous;
    fixture.detectChanges();
    expect(state.selectedSnapshot()?.id).toBe('new');
    expect(fixture.nativeElement.textContent).not.toContain('old-snapshot');
  });
});
