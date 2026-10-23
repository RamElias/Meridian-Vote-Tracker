import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { Subject } from 'rxjs';
import { ApiService } from './api.service';
import { ImportsStore } from './features/imports/imports.store';
import { PositionsStore } from './features/positions/positions.store';
import { SnapshotsStore } from './features/snapshots/snapshots.store';
import { ImportRecord } from './models';

describe('Feature state boundaries', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      providers: [
        {
          provide: ApiService,
          useValue: {
            demoUser: signal('lana'),
            imports: vi.fn(),
            votes: vi.fn(),
          },
        },
      ],
    }),
  );

  it('keeps validation errors local to the screen that owns the form', async () => {
    const imports = TestBed.inject(ImportsStore);
    const snapshots = TestBed.inject(SnapshotsStore);
    const positions = TestBed.inject(PositionsStore);
    await imports.upload();
    await snapshots.createSnapshot();
    expect(imports.fieldErrors()).toEqual({ file: 'Choose a CSV file first.' });
    expect(snapshots.fieldErrors()['voteIds']).toContain('Choose a session');
    expect(positions.fieldErrors()).toEqual({});
    snapshots.clearMessages();
    expect(imports.fieldErrors()['file']).toBeTruthy();
  });

  it('does not block another screen while an import request is pending', async () => {
    const pending = new Subject<ImportRecord[]>();
    const api = TestBed.inject(ApiService);
    vi.mocked(api.imports).mockReturnValue(pending);
    const imports = TestBed.inject(ImportsStore);
    const positions = TestBed.inject(PositionsStore);
    const request = imports.loadMoreImports();
    expect(imports.busy()).toBe(true);
    expect(positions.busy()).toBe(false);
    positions.search.set('independent search');
    pending.next([]);
    await request;
    expect(imports.busy()).toBe(false);
    expect(positions.search()).toBe('independent search');
  });
  it('keeps a screen busy until all its concurrent requests finish', async () => {
    const first = new Subject<ImportRecord[]>();
    const second = new Subject<ImportRecord[]>();
    const api = TestBed.inject(ApiService);
    vi.mocked(api.imports).mockReturnValueOnce(first).mockReturnValueOnce(second);
    const imports = TestBed.inject(ImportsStore);
    const firstRequest = imports.loadMoreImports();
    const secondRequest = imports.loadMoreImports();
    first.next([]);
    await firstRequest;
    expect(imports.busy()).toBe(true);
    second.next([]);
    await secondRequest;
    expect(imports.busy()).toBe(false);
  });
});
