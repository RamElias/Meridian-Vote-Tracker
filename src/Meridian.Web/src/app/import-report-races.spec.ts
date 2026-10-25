import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of, Subject } from 'rxjs';
import { ApiService } from './api.service';
import { ImportsStore } from './features/imports/imports.store';
import { ImportDetails, ImportRowDetail } from './models';

const detail = (id: string): ImportDetails => ({
  id,
  fileName: id + '.csv',
  status: 'Completed',
  rowsProcessed: 1,
  rowsTotal: 1,
  createdAt: '2024-01-01T00:00:00Z',
  completedAt: '2024-01-01T00:00:01Z',
  originalImportId: null,
  uploadedBy: 'Dana',
  report: null,
});
const row = (id: string, rowNumber = 2): ImportRowDetail => ({
  rowNumber,
  classification: 'New',
  reason: null,
  values: { assembly_vote_id: id },
  normalizedValues: {},
  normalizations: {},
});

describe('Import report request identity', () => {
  let api: {
    demoUser: ReturnType<typeof signal<string>>;
    importDetails: ReturnType<typeof vi.fn>;
    importRows: ReturnType<typeof vi.fn>;
    imports: ReturnType<typeof vi.fn>;
  };
  let store: ImportsStore;
  beforeEach(() => {
    api = {
      demoUser: signal('dana'),
      importDetails: vi.fn((id: string) => of(detail(id))),
      importRows: vi.fn(),
      imports: vi.fn(),
    };
    TestBed.configureTestingModule({ providers: [{ provide: ApiService, useValue: api }] });
    store = TestBed.inject(ImportsStore);
  });

  it('ignores A rows arriving after B is selected', async () => {
    const delayed = new Subject<ImportRowDetail[]>();
    api.importRows.mockImplementation((id: string) => (id === 'A' ? delayed : of([row('B')])));
    const first = store.openImport({ id: 'A' });
    await Promise.resolve();
    await store.openImport({ id: 'B' });
    delayed.next([row('A')]);
    await first;
    expect(store.selectedImport()?.id).toBe('B');
    expect(store.importRows()[0].values['assembly_vote_id']).toBe('B');
  });

  it('does not reopen a closed report after delayed details return', async () => {
    const delayed = new Subject<ImportDetails>();
    api.importDetails.mockReturnValue(delayed);
    const opening = store.openImport({ id: 'A' });
    store.closeImportDetails();
    delayed.next(detail('A'));
    await opening;
    expect(store.selectedImport()).toBeNull();
    expect(api.importRows).not.toHaveBeenCalled();
  });

  it('ignores old filter results', async () => {
    store.selectedImport.set(detail('A'));
    const delayed = new Subject<ImportRowDetail[]>();
    api.importRows.mockReturnValueOnce(delayed).mockReturnValueOnce(of([row('invalid')]));
    const old = store.setImportRowFilter('New');
    await store.setImportRowFilter('Invalid');
    delayed.next([row('new')]);
    await old;
    expect(store.importRowFilter()).toBe('Invalid');
    expect(store.importRows()[0].values['assembly_vote_id']).toBe('invalid');
  });

  it('discards an old append after the report changes', async () => {
    store.selectedImport.set(detail('A'));
    store.importRows.set([row('A')]);
    const delayed = new Subject<ImportRowDetail[]>();
    api.importRows.mockReturnValueOnce(delayed).mockReturnValueOnce(of([row('B')]));
    const append = store.loadMoreImportRows();
    await store.openImport({ id: 'B' });
    delayed.next([row('A', 3)]);
    await append;
    expect(store.importRows().map((item) => item.values['assembly_vote_id'])).toEqual(['B']);
  });

  it('preserves loaded history pages and suppresses overlapping polls', async () => {
    const records = Array.from({ length: 150 }, (_, n) => detail(String(n)));
    store.imports.set(records);
    store.importsHasMore.set(true);
    const pending = new Subject<ImportDetails[]>();
    api.imports.mockReturnValue(pending);
    const poll = store.loadImports();
    await store.loadImports();
    pending.next(records.slice(0, 100));
    await poll;
    expect(api.imports).toHaveBeenCalledTimes(1);
    expect(store.imports()).toHaveLength(150);
    expect(store.importsHasMore()).toBe(true);
  });
});
