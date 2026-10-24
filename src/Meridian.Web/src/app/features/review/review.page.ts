import { PdfReviewComponent } from '../pdf-imports/pdf-review.component';
import { Component, inject } from '@angular/core';
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
