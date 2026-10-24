import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { PdfImportsStore } from './pdf-imports.store';
import { WorkspaceStore } from '../../workspace.store';
@Component({
  selector: 'app-pdf-upload',
  imports: [CommonModule],
  templateUrl: './pdf-upload.component.html',
})
export class PdfUploadComponent {
  readonly store = inject(PdfImportsStore);
  readonly workspace = inject(WorkspaceStore);
  async review(item: Parameters<PdfImportsStore['open']>[0]): Promise<void> {
    await this.store.open(item);
    if (this.store.selected()?.id !== (item.originalExtractionId ?? item.id)) return;
    this.workspace.reviewStore.reviewTab.set('extractions');
    this.workspace.reviewStore.selectedReviewKind.set('pdf');
    await this.workspace.chooseSection('review');
  }
}
