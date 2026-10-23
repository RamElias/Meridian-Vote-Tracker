import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { ImportsStore } from './imports.store';
import { ReviewStore } from '../review/review.store';

@Component({
  selector: 'app-imports-page',
  imports: [CommonModule, FormsModule],
  templateUrl: './imports.page.html',
})
export class ImportsPage {
  private readonly workspace = inject(ImportsStore);
  private readonly reviewStore = inject(ReviewStore);
  readonly normalizationChanges = this.workspace.normalizationChanges.bind(this.workspace);
  readonly fieldErrors = this.workspace.fieldErrors;
  readonly ballotLabel = this.workspace.ballotLabel.bind(this.workspace);
  readonly busy = this.workspace.busy;
  readonly closeImportDetails = this.workspace.closeImportDetails.bind(this.workspace);
  readonly completedImportCount = this.workspace.completedImportCount;
  readonly conflicts = this.reviewStore.conflicts;
  readonly hasSelectedFile = this.workspace.hasSelectedFile.bind(this.workspace);
  readonly importRowFilter = this.workspace.importRowFilter;
  readonly importRows = this.workspace.importRows;
  readonly importRowsHasMore = this.workspace.importRowsHasMore;
  readonly importSummary = this.workspace.importSummary.bind(this.workspace);
  readonly importVoteRows = this.workspace.importVoteRows.bind(this.workspace);
  readonly imports = this.workspace.imports;
  readonly importsHasMore = this.workspace.importsHasMore;
  readonly loadImports = this.workspace.loadImports.bind(this.workspace);
  readonly loadMoreImportRows = this.workspace.loadMoreImportRows.bind(this.workspace);
  readonly loadMoreImports = this.workspace.loadMoreImports.bind(this.workspace);
  readonly onFileSelected = this.workspace.onFileSelected.bind(this.workspace);
  readonly openImport = this.workspace.openImport.bind(this.workspace);
  readonly outcomeLabel = this.workspace.outcomeLabel.bind(this.workspace);
  readonly processingCount = this.workspace.processingCount;
  readonly progress = this.workspace.progress.bind(this.workspace);
  readonly selectedFileName = this.workspace.selectedFileName.bind(this.workspace);
  readonly selectedImport = this.workspace.selectedImport;
  readonly setImportRowFilter = this.workspace.setImportRowFilter.bind(this.workspace);
  readonly statusClass = this.workspace.statusClass.bind(this.workspace);
  readonly upload = this.workspace.upload.bind(this.workspace);
}
