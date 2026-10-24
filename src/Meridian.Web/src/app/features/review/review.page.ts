import { PdfImportsStore } from '../pdf-imports/pdf-imports.store';
import { PdfExtraction } from '../../generated-api/models';
import { PdfReviewComponent } from '../pdf-imports/pdf-review.component';
import { Component, inject, computed, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ReviewStore } from './review.store';

@Component({
  selector: 'app-review-page',
  imports: [CommonModule, FormsModule, PdfReviewComponent],
  templateUrl: './review.page.html',
})
export class ReviewPage {
  private readonly workspace = inject(ReviewStore);

  readonly pdf = inject(PdfImportsStore);
  readonly tab = this.workspace.reviewTab;
  readonly selectedKind = this.workspace.selectedReviewKind;
  readonly queueConflicts = computed(() =>
    this.tab() === 'extractions'
      ? []
      : this.tab() === 'resolved'
        ? this.workspace.resolvedConflicts()
        : this.workspace.conflicts(),
  );
  readonly queuePdfs = computed(() =>
    this.tab() === 'conflicts'
      ? []
      : this.pdf
          .items()
          .filter((p) =>
            this.tab() === 'resolved'
              ? ['Accepted', 'Rejected'].includes(p.status)
              : !['Accepted', 'Rejected', 'Duplicate'].includes(p.status),
          )
          .filter((p) => {
            const filter = this.workspace.conflictVoteFilter().trim().toLowerCase();
            return (
              !filter ||
              p.fileName.toLowerCase().includes(filter.replaceAll('/', '_')) ||
              p.draft?.voteId.toLowerCase().includes(filter)
            );
          }),
  );
  readonly pendingPdfCount = computed(
    () =>
      this.pdf.items().filter((p) => !['Accepted', 'Rejected', 'Duplicate'].includes(p.status))
        .length,
  );
  async chooseTab(tab: 'conflicts' | 'extractions' | 'resolved'): Promise<void> {
    if (this.pdf.dirty() && tab !== this.tab()) {
      if (!window.confirm('Discard unsaved PDF draft edits?')) return;
      this.pdf.dirty.set(false);
    }
    this.tab.set(tab);
    await Promise.all([this.workspace.loadConflicts(), this.pdf.refresh()]);
    const conflict = this.queueConflicts()[0];
    if (conflict) {
      this.selectedKind.set('conflict');
      this.workspace.selectConflict(conflict.id);
    } else if (this.queuePdfs()[0]) await this.openPdf(this.queuePdfs()[0]);
    else {
      this.selectedKind.set(tab === 'extractions' ? 'pdf' : 'conflict');
      this.workspace.selectedConflictId.set(null);
    }
  }
  async openPdf(item: PdfExtraction): Promise<void> {
    await this.pdf.open(item);
    if (this.pdf.selected()?.id === (item.originalExtractionId ?? item.id))
      this.selectedKind.set('pdf');
  }
  pickConflict(id: string): void {
    this.selectedKind.set('conflict');
    this.workspace.selectConflict(id);
  }
  async refreshReview(): Promise<void> {
    await Promise.all([this.workspace.searchConflicts(), this.pdf.refresh()]);
  }

  readonly busy = this.workspace.busy;
  readonly conflictFields = this.workspace.conflictFields.bind(this.workspace);
  readonly conflictStatus = this.workspace.conflictStatus;
  readonly conflictVoteFilter = this.workspace.conflictVoteFilter;
  readonly conflicts = this.workspace.conflicts;
  readonly decide = this.workspace.decide.bind(this.workspace);
  readonly decisionNote = this.workspace.decisionNote;
  readonly loadConflicts = this.workspace.loadConflicts.bind(this.workspace);
  readonly searchConflicts = this.workspace.searchConflicts.bind(this.workspace);
  readonly selectConflict = this.workspace.selectConflict.bind(this.workspace);
  readonly selectedConflict = this.workspace.selectedConflict;
  readonly selectedConflictId = this.workspace.selectedConflictId;
}
