import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse } from '@angular/common/http';
import { signal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { ApiService } from './api.service';
import { SnapshotsPage } from './features/snapshots/snapshots.page';
import { SnapshotsStore } from './features/snapshots/snapshots.store';
import { SnapshotRecord, VoteContribution } from './models';

const snapshot: SnapshotRecord = {
  id: 'snapshot-one',
  status: 'Completed',
  ruleVersion: 'alignment-v1',
  createdBy: 'lana',
  createdAt: '2024-04-01T00:00:00Z',
  completedAt: '2024-04-01T00:00:01Z',
  error: null,
  publicationYear: null,
  publicationQuarter: null,
  publishedAt: null,
  withdrawnAt: null,
  supersededAt: null,
  progressCurrent: 1,
  progressTotal: 1,
  selectedVoteCount: 1,
};
const contribution: VoteContribution = {
  ordinal: 1,
  voteId: 'QA/2024/001',
  voteDate: '2024-03-14',
  title: 'Climate',
  instituteStance: 'N',
  weight: 3,
  memberAtVote: true,
  ballot: 'N',
  alignmentNumerator: 3,
  alignmentDenominator: 3,
  participationNumerator: 1,
  participationDenominator: 1,
  coverageNumerator: 1,
  coverageDenominator: 1,
};

describe('Snapshot dialogs', () => {
  const api = {
    demoUser: signal('lana'),
    contributions: vi.fn(),
    publishSnapshot: vi.fn(),
    snapshots: vi.fn(() => of([])),
    snapshot: vi.fn(() => of(snapshot)),
    snapshotResults: vi.fn(() => of([])),
  };
  beforeEach(() => {
    vi.clearAllMocks();
    api.contributions.mockReturnValue(of([contribution]));
    api.snapshots.mockReturnValue(of([]));
    TestBed.configureTestingModule({
      imports: [SnapshotsPage],
      providers: [{ provide: ApiService, useValue: api }],
    });
  });
  function setup() {
    const fixture = TestBed.createComponent(SnapshotsPage);
    const state = TestBed.inject(SnapshotsStore);
    state.selectedSnapshot.set({ ...snapshot });
    state.snapshotResults.set([
      {
        countryCode: 'FRA',
        countryName: 'France',
        rank: 1,
        alignmentScore: 100,
        participationPercent: 100,
        coveragePercent: 100,
        approvedMemberRecords: 1,
        eligibleMemberVotes: 1,
      },
    ]);
    fixture.detectChanges();
    return { fixture, state };
  }
  it('opens contributions from the header and selects an available country', async () => {
    const { fixture, state } = setup();
    const button = [...fixture.nativeElement.querySelectorAll('button')].find((item: Element) =>
      item.textContent?.includes('View contribution details'),
    ) as HTMLButtonElement;
    button.click();
    await fixture.whenStable();
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('dialog');
    expect(dialog).not.toBeNull();
    expect(dialog.textContent).toContain('QA/2024/001');
    expect(state.countryCode()).toBe('FRA');
    expect(api.contributions).toHaveBeenCalledWith(snapshot.id, 'FRA', 0);
  });
  it('opens the clicked country directly from its ranking row', async () => {
    const { fixture } = setup();
    fixture.nativeElement.querySelector('[aria-label="View contributions for France"]').click();
    await fixture.whenStable();
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('dialog').textContent).toContain('QA/2024/001');
    expect(api.contributions).toHaveBeenCalledWith(snapshot.id, 'FRA', 0);
  });
  it('closes on Escape/cancel and ignores an outstanding response', async () => {
    const { fixture, state } = setup();
    const delayed = new Subject<VoteContribution[]>();
    api.contributions.mockReturnValue(delayed);
    const opening = fixture.componentInstance.openContributionDialog('FRA');
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('dialog');
    expect(dialog.textContent).toContain('Loading contributions');
    dialog.dispatchEvent(new Event('cancel', { cancelable: true }));
    fixture.detectChanges();
    delayed.next([contribution]);
    await opening;
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('dialog')).toBeNull();
    expect(state.contributions()).toEqual([]);
  });
  it('shows contribution errors inside the modal with a correlation reference', async () => {
    const { fixture, state } = setup();
    api.contributions.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 500,
            error: { detail: 'Unable to load details.', correlationId: 'test-reference' },
          }),
      ),
    );
    await fixture.componentInstance.openContributionDialog('FRA');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('dialog [role="alert"]').textContent).toContain(
      'test-reference',
    );
    expect(state.error()).toBe('');
  });
  it('shows an empty result explicitly', async () => {
    const { fixture } = setup();
    api.contributions.mockReturnValue(of([]));
    await fixture.componentInstance.openContributionDialog('ZZZ');
    fixture.detectChanges();
    expect(fixture.nativeElement.querySelector('dialog').textContent).toContain(
      'No contributions were found',
    );
  });
  it('shows publication conflicts in a modal and does not automatically replace', async () => {
    const { fixture, state } = setup();
    state.publicationYear.set(2024);
    state.publicationQuarter.set(4);
    state.notice.set('Old notice');
    api.publishSnapshot.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 409,
            error: { detail: 'Existing publication.', correlationId: 'publication-reference' },
          }),
      ),
    );
    await state.publishSnapshot();
    fixture.detectChanges();
    const dialog = fixture.nativeElement.querySelector('dialog');
    expect(dialog.textContent).toContain('2024 Q4');
    expect(dialog.textContent).toContain('Replace active');
    expect(api.publishSnapshot).toHaveBeenCalledExactlyOnceWith(snapshot.id, 2024, 4, false);
    expect(state.replaceActive()).toBe(false);
    expect(state.error()).toBe('');
    expect(state.notice()).toBe('');
    dialog.querySelector('[aria-label="Close Publication could not be completed"]').click();
    fixture.detectChanges();
    expect(state.publicationConflict()).toBeNull();
  });
  it('retains readable four-digit year and Q4 controls', () => {
    const { fixture, state } = setup();
    state.publicationYear.set(2024);
    state.publicationQuarter.set(4);
    fixture.detectChanges();
    const year = fixture.nativeElement.querySelector('.publication-year') as HTMLInputElement;
    const quarter = fixture.nativeElement.querySelector(
      '.publication-quarter',
    ) as HTMLSelectElement;
    expect(year).not.toBeNull();
    expect(quarter).not.toBeNull();
    expect(quarter.options).toHaveLength(4);
  });
});
