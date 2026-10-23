import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { ApiService } from './api.service';
import { App } from './app';

describe('App', () => {
  const apiMock = {
    demoUser: signal('lana'),
    setDemoUser: vi.fn(),
    imports: vi.fn(() => of([])),
    conflicts: vi.fn(() => of([])),
    votes: vi.fn(() => of([])),
    snapshots: vi.fn(() => of([])),
  };

  beforeEach(async () => {
    apiMock.demoUser.set('lana');
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [{ provide: ApiService, useValue: apiMock }],
    }).compileComponents();
  });

  it('renders the imports workspace and core navigation', () => {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Data imports');
    expect(compiled.textContent).toContain('Conflict review');
    expect(compiled.textContent).toContain('Vote positions');
    expect(compiled.textContent).toContain('Rankings & snapshots');

    fixture.destroy();
  });

  it('counts queued and completed imports independently', () => {
    const fixture = TestBed.createComponent(App);
    const app = fixture.componentInstance.workspace;
    app.imports.set([
      { id: '1', fileName: 'queued.csv', status: 'Queued', rowsProcessed: 0, rowsTotal: 5, createdAt: '', completedAt: null, originalImportId: null, uploadedBy: null },
      { id: '2', fileName: 'done.csv', status: 'Completed', rowsProcessed: 5, rowsTotal: 5, createdAt: '', completedAt: '', originalImportId: null, uploadedBy: null },
    ]);

    expect(app.processingCount()).toBe(1);
    expect(app.completedImportCount()).toBe(1);
    fixture.destroy();
  });

  it('distinguishes active, withdrawn, and superseded publications', () => {
    const app = TestBed.createComponent(App).componentInstance.workspace;
    const base = {
      id: 'snapshot', status: 'Completed', ruleVersion: 'alignment-v1', createdBy: 'lana', createdAt: '', completedAt: '',
      publicationYear: 2024, publicationQuarter: 1, publishedAt: '2026-10-21T12:00:00Z', withdrawnAt: null, supersededAt: null,
      progressCurrent: 1, progressTotal: 1, selectedVoteCount: 1, error: null,
    };

    expect(app.snapshotStatus(base)).toBe('Published');
    expect(app.isActivelyPublished(base)).toBe(true);
    expect(app.snapshotStatus({ ...base, withdrawnAt: '2026-10-22T12:00:00Z' })).toBe('Withdrawn');
    expect(app.isActivelyPublished({ ...base, withdrawnAt: '2026-10-22T12:00:00Z' })).toBe(false);
    expect(app.snapshotStatus({ ...base, supersededAt: '2026-10-22T12:00:00Z' })).toBe('Superseded');
    expect(app.isActivelyPublished({ ...base, supersededAt: '2026-10-22T12:00:00Z' })).toBe(false);
  });
});
