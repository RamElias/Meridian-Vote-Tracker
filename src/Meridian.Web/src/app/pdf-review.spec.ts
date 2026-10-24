import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of, Subject, throwError } from 'rxjs';
import { HttpErrorResponse } from '@angular/common/http';
import { ApiService } from './api.service';
import { PdfImportsStore } from './features/pdf-imports/pdf-imports.store';
import { PdfReviewComponent } from './features/pdf-imports/pdf-review.component';
import { PdfDraft, PdfExtraction } from './generated-api/models';

const draft: PdfDraft = {
  voteId: 'MA/2024/777',
  session: '79',
  voteDate: '2024-01-01',
  title: 'Review test',
  publishedAt: '',
  printedTotals: { yes: 1, no: 0, abstain: 0, absent: 0 },
  rows: [
    {
      rowNumber: 1,
      pageNumber: 1,
      countryCode: 'AAA',
      countryName: 'Alpha',
      ballot: 'Y',
      confidence: 0.4,
      sourceText: 'AAA | Alpha | X',
    },
  ],
};
const record = (id: string): PdfExtraction => ({
  id,
  fileName: id + '.pdf',
  status: 'AwaitingReview',
  version: 3,
  uploadedBy: 'Dana',
  createdAt: '2024-01-01T00:00:00Z',
  originalExtractionId: null,
  draft: structuredClone(draft),
  issues: [
    {
      code: 'confidence',
      severity: 'Warning',
      message: 'Check low confidence ballot',
      rowNumber: 1,
    },
  ],
  error: null,
  importId: null,
  reviewedBy: null,
  reviewedAt: null,
  decisionNote: null,
  progress: null,
});

describe('PDF human review', () => {
  const api = {
    demoUser: signal('dana'),
    pdfImport: vi.fn(),
    pdfSource: vi.fn(() => of(new Blob(['%PDF-']))),
    pdfImports: vi.fn(() => of([])),
    savePdf: vi.fn(),
    approvePdf: vi.fn(),
    imports: vi.fn(() => of([])),
  };
  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('confirm', () => true);
    URL.createObjectURL = vi.fn(() => 'blob:test-pdf');
    URL.revokeObjectURL = vi.fn();
    TestBed.configureTestingModule({
      imports: [PdfReviewComponent],
      providers: [{ provide: ApiService, useValue: api }],
    });
  });
  afterEach(() => vi.unstubAllGlobals());

  it('shows source-review warnings and keeps approval disabled until acknowledgement', async () => {
    const store = TestBed.inject(PdfImportsStore);
    const item = record('first');
    store.selected.set(item);
    store.draft.set(structuredClone(draft));
    store.issues.set(item.issues);
    const fixture = TestBed.createComponent(PdfReviewComponent);
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.textContent).toContain('Check low confidence ballot');
    const approve = Array.from(root.querySelectorAll('button')).find((b) =>
      b.textContent?.includes('Approve and queue'),
    )!;
    expect(approve.disabled).toBe(true);
    store.acknowledged.set(true);
    fixture.detectChanges();
    expect(approve.disabled).toBe(false);
    expect(api.approvePdf).not.toHaveBeenCalled();
    fixture.destroy();
  });

  it('keeps edited values on validation failure and shows the relevant issue', async () => {
    const store = TestBed.inject(PdfImportsStore);
    store.selected.set(record('first'));
    store.draft.set(structuredClone(draft));
    store.acknowledged.set(true);
    store.setRow(1, 'ballot', 'N');
    api.approvePdf.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 422,
            error: {
              detail: 'Correct the totals',
              issues: [
                {
                  code: 'totalsMismatch',
                  severity: 'Error',
                  message: 'Totals differ',
                  rowNumber: null,
                },
              ],
            },
          }),
      ),
    );
    await store.save(true);
    expect(store.draft()?.rows[0].ballot).toBe('N');
    expect(store.fieldIssue('totalsMismatch')).toBe('Totals differ');
    expect(store.error()).toContain('Correct the totals');
  });

  it('ignores late detail responses after selecting another PDF', async () => {
    const first = new Subject<PdfExtraction>();
    api.pdfImport.mockImplementation((id: string) =>
      id === 'first' ? first : of(record('second')),
    );
    const store = TestBed.inject(PdfImportsStore);
    const request = store.open(record('first'));
    await store.open(record('second'));
    first.next(record('first'));
    await request;
    expect(store.selected()?.id).toBe('second');
    expect(store.draft()?.title).toBe('Review test');
    expect(api.pdfSource).toHaveBeenCalledTimes(1);
  });
});
