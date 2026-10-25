import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { ApiService } from './api.service';
import { WorkspaceStore } from './workspace.store';
import { SnapshotsStore } from './features/snapshots/snapshots.store';
import { SnapshotRecord } from './models';

const item = (id: string): SnapshotRecord => ({
  id,
  status: 'Failed',
  ruleVersion: 'alignment-v1',
  createdBy: 'Dana',
  createdAt: '2024-01-01T00:00:00Z',
  completedAt: null,
  error: null,
  progressCurrent: 0,
  progressTotal: 0,
  publicationYear: null,
  publicationQuarter: null,
  publishedAt: null,
  withdrawnAt: null,
  supersededAt: null,
  selectedVoteCount: 0,
});

describe('Snapshot history', () => {
  it('loads another page and retains it through polling', async () => {
    const page = Array.from({ length: 100 }, (_, n) => item(String(n)));
    const api = {
      demoUser: signal('dana'),
      snapshots: vi
        .fn()
        .mockReturnValueOnce(of(page))
        .mockReturnValueOnce(of([item('old-published')]))
        .mockReturnValueOnce(of([...page, item('old-published')])),
    };
    TestBed.configureTestingModule({ providers: [{ provide: ApiService, useValue: api }] });
    const store = TestBed.inject(SnapshotsStore);
    await store.loadSnapshots();
    await store.loadMoreSnapshots();
    await store.loadSnapshots();
    expect(store.snapshots()).toHaveLength(101);
    expect(store.snapshots().at(-1)?.id).toBe('old-published');
    expect(api.snapshots.mock.calls[1][0]).toBe(100);
  });
  it('passes publication filters and starts at page zero', async () => {
    const api = {
      demoUser: signal('dana'),
      snapshots: vi.fn().mockReturnValue(of([item('published')])),
    };
    TestBed.configureTestingModule({ providers: [{ provide: ApiService, useValue: api }] });
    const store = TestBed.inject(SnapshotsStore);
    store.historyYear.set(2024);
    store.historyQuarter.set(1);
    store.historyPublication.set('Active');
    await store.applyHistoryFilter();
    expect(api.snapshots).toHaveBeenCalledWith(0, 100, 2024, 1, 'Active');
  });
  it('polls a selected running snapshot even when history filters hide it', async () => {
    vi.useFakeTimers();
    const api = {
      demoUser: signal('dana'),
      snapshots: vi.fn(() => of([])),
      imports: vi.fn(() => of([])),
      pdfImports: vi.fn(() => of([])),
      conflicts: vi.fn(() => of([])),
      votes: vi.fn(() => of([])),
    };
    TestBed.configureTestingModule({ providers: [{ provide: ApiService, useValue: api }] });
    const workspace = TestBed.inject(WorkspaceStore);
    try {
      await workspace.ngOnInit();
      workspace.snapshotsStore.selectedSnapshot.set({ ...item('hidden'), status: 'Running' });
      const refresh = vi.spyOn(workspace.snapshotsStore, 'loadSnapshots').mockResolvedValue();
      await vi.advanceTimersByTimeAsync(2500);
      expect(refresh).toHaveBeenCalledTimes(1);
    } finally {
      workspace.ngOnDestroy();
      vi.useRealTimers();
    }
  });
});
