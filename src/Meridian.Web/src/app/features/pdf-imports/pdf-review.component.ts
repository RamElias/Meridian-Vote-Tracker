import { Component, computed, inject, OnDestroy, OnInit } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { DomSanitizer } from '@angular/platform-browser';
import { PdfImportsStore } from './pdf-imports.store';
@Component({
  selector: 'app-pdf-review',
  imports: [CommonModule, FormsModule],
  templateUrl: './pdf-review.component.html',
})
export class PdfReviewComponent implements OnDestroy, OnInit {
  readonly store = inject(PdfImportsStore);
  private readonly sanitizer = inject(DomSanitizer);
  // Only a local Blob URL created from our authenticated source endpoint reaches this binding.
  readonly sourcePreview = computed(() => {
    const url = this.store.sourceUrl();
    return url
      ? this.sanitizer.bypassSecurityTrustResourceUrl(url + '#page=' + this.store.sourcePage())
      : null;
  });
  readonly globalIssues = computed(() =>
    this.store
      .issues()
      .filter(
        (i) =>
          i.rowNumber === null &&
          ![
            'voteId',
            'session',
            'voteDate',
            'title',
            'publishedAt',
            'printedTotals',
            'totalsMismatch',
          ].includes(i.code),
      ),
  );
  readonly editable = computed(() => this.store.selected()?.status === 'AwaitingReview');
  ngOnInit(): void {
    void this.store.ensureSource();
  }
  ngOnDestroy(): void {
    this.store.closeSource();
  }
}
