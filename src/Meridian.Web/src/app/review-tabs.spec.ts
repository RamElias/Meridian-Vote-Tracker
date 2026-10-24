import { TestBed } from '@angular/core/testing';
import { signal } from '@angular/core';
import { of } from 'rxjs';
import { ApiService } from './api.service';
import { ReviewPage } from './features/review/review.page';
import { ReviewStore } from './features/review/review.store';
import { PdfImportsStore } from './features/pdf-imports/pdf-imports.store';
import { Conflict, PdfExtraction } from './generated-api/models';
const conflict = (id: string, status: 'Pending' | 'Accepted'): Conflict => ({
  id,
  status,
  voteId: id,
  countryCode: 'AAA',
  createdAt: '2024-01-01',
  currentSource: { fileName: 'original.csv', uploadedBy: 'Dana', uploadedAt: '2024-01-01' },
  currentValue: { ballot: 'Y' },
  decidedAt: null,
  decidedBy: null,
  decisionNote: null,
  decisionValue: null,
  proposals: [],
  targetType: 'CountryVote',
});
describe('Unified review queue', () => {
  it('offers Conflicts, Extractions and Resolved without rendering a second queue', async () => {
    const pending = conflict('pending', 'Pending'),
      resolved = conflict('resolved', 'Accepted');
    TestBed.configureTestingModule({
      imports: [ReviewPage],
      providers: [
        {
          provide: ApiService,
          useValue: {
            demoUser: signal('dana'),
            conflicts: (_: string, status: string) =>
              of(status === 'Resolved' ? [resolved] : [pending]),
            pdfImports: () => of([]),
          },
        },
      ],
    });
    const fixture = TestBed.createComponent(ReviewPage);
    await TestBed.inject(ReviewStore).loadConflicts();
    fixture.detectChanges();
    const root: HTMLElement = fixture.nativeElement;
    expect(root.querySelectorAll('[role=tab]').length).toBe(3);
    expect(root.textContent).toContain('Extractions');
    expect(root.textContent).toContain('pending');
    await fixture.componentInstance.chooseTab('resolved');
    fixture.detectChanges();
    expect(root.querySelector('[role=tabpanel]')?.textContent).toContain('resolved');
    expect(root.querySelector('[role=tabpanel]')?.textContent).not.toContain('pending');
    await fixture.componentInstance.chooseTab('extractions');
    fixture.detectChanges();
    expect(root.querySelectorAll('.pdf-queue').length).toBe(0);
    expect(root.textContent).toContain('No extractions');
  });
});
