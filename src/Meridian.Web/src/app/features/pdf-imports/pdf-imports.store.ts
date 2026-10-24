import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import {
  PdfDraft,
  PdfDraftRow,
  PdfExtraction,
  PdfIssue,
  PdfTotals,
} from '../../generated-api/models';
import { FeatureStore } from '../../shared/feature-store';
import { ImportsStore } from '../imports/imports.store';

@Injectable({ providedIn: 'root' })
export class PdfImportsStore extends FeatureStore {
  readonly items = signal<PdfExtraction[]>([]);
  readonly hasMore = signal(false);
  readonly selected = signal<PdfExtraction | null>(null);
  readonly draft = signal<PdfDraft | null>(null);
  readonly issues = signal<PdfIssue[]>([]);
  readonly note = signal('');
  readonly acknowledged = signal(false);
  readonly file = signal<File | null>(null);
  readonly sourceUrl = signal<string | null>(null);
  readonly sourcePage = signal(1);
  readonly dirty = signal(false);
  private selectionRequest = 0;
  private readonly importsStore = inject(ImportsStore);
  readonly totals = computed(() => {
    const rows = this.draft()?.rows ?? [];
    return {
      yes: rows.filter((r) => r.ballot === 'Y').length,
      no: rows.filter((r) => r.ballot === 'N').length,
      abstain: rows.filter((r) => r.ballot === 'A').length,
      absent: rows.filter((r) => r.ballot === 'X').length,
    };
  });
  async refresh(): Promise<void> {
    try {
      const items = await firstValueFrom(this.api.pdfImports());
      this.items.set(items);
      this.hasMore.set(items.length === 50);
      const selected = this.selected();
      if (selected && ['Queued', 'Running'].includes(selected.status)) {
        const item = await firstValueFrom(this.api.pdfImport(selected.id));
        if (this.selected()?.id !== selected.id) return;
        this.selected.set(item);
        if (item.status === 'AwaitingReview') this.applyDraft(item);
      }
    } catch (error) {
      this.showError(error);
    }
  }
  async loadMore(): Promise<void> {
    await this.run(async () => {
      const page = await firstValueFrom(this.api.pdfImports(this.items().length));
      this.items.update((current) => [...current, ...page]);
      this.hasMore.set(page.length === 50);
    });
  }
  chooseFile(event: Event): void {
    this.file.set((event.target as HTMLInputElement).files?.[0] ?? null);
    this.fieldErrors.set({});
  }
  async upload(): Promise<void> {
    const file = this.file();
    if (!file || !file.name.toLowerCase().endsWith('.pdf') || file.size > 20 * 1024 * 1024) {
      this.fieldErrors.set({ file: 'Choose a PDF up to 20 MB.' });
      return;
    }
    await this.run(async () => {
      const item = await firstValueFrom(this.api.uploadPdf(file));
      this.file.set(null);
      this.notice.set(
        item.status === 'Duplicate'
          ? 'Identical PDF already submitted; history was retained.'
          : 'PDF queued for extraction. It will require human review.',
      );
      await this.refresh();
    });
  }
  async open(item: PdfExtraction): Promise<void> {
    if (this.dirty() && !window.confirm('Discard unsaved PDF draft edits?')) return;
    const request = ++this.selectionRequest;
    this.revokeSource();
    this.selected.set(item);
    this.draft.set(null);
    this.issues.set([]);
    this.dirty.set(false);
    await this.run(async () => {
      const details = await firstValueFrom(
        this.api.pdfImport(item.originalExtractionId ?? item.id),
      );
      if (request !== this.selectionRequest) return;
      this.selected.set(details);
      this.applyDraft(details);
      const blob = await firstValueFrom(this.api.pdfSource(details.id));
      if (request !== this.selectionRequest) return;
      this.sourceUrl.set(URL.createObjectURL(blob));
      this.sourcePage.set(1);
    });
  }
  private applyDraft(item: PdfExtraction): void {
    this.draft.set(item.draft ? structuredClone(item.draft) : null);
    this.issues.set(item.issues);
    this.note.set(item.decisionNote ?? '');
    this.acknowledged.set(false);
    this.dirty.set(false);
  }
  setField(
    field: 'voteId' | 'session' | 'voteDate' | 'title' | 'publishedAt',
    value: string,
  ): void {
    this.draft.update((d) => (d ? { ...d, [field]: value } : null));
    this.dirty.set(true);
  }
  setRow(number: number, field: 'countryCode' | 'countryName' | 'ballot', value: string): void {
    this.draft.update((d) =>
      d
        ? { ...d, rows: d.rows.map((r) => (r.rowNumber === number ? { ...r, [field]: value } : r)) }
        : null,
    );
    this.dirty.set(true);
  }
  setTotal(field: keyof PdfTotals, value: number): void {
    this.draft.update((d) =>
      d
        ? {
            ...d,
            printedTotals: {
              ...(d.printedTotals ?? { yes: 0, no: 0, abstain: 0, absent: 0 }),
              [field]: Number(value),
            },
          }
        : null,
    );
    this.dirty.set(true);
  }
  addRow(): void {
    this.draft.update((d) =>
      d
        ? {
            ...d,
            rows: [
              ...d.rows,
              {
                rowNumber: Math.max(0, ...d.rows.map((r) => r.rowNumber)) + 1,
                pageNumber: 0,
                countryCode: '',
                countryName: '',
                ballot: '',
                confidence: null,
                sourceText: 'Manually entered during review',
              },
            ],
          }
        : null,
    );
    this.dirty.set(true);
  }
  removeRow(number: number): void {
    this.draft.update((d) =>
      d ? { ...d, rows: d.rows.filter((r) => r.rowNumber !== number) } : null,
    );
    this.dirty.set(true);
  }
  rowIssues(number: number): PdfIssue[] {
    return this.issues().filter((i) => i.rowNumber === number);
  }
  fieldIssue(code: string): string {
    return this.issues()
      .filter((i) => i.rowNumber === null && i.code === code)
      .map((i) => i.message)
      .join(' ');
  }
  async save(approve = false): Promise<void> {
    const item = this.selected(),
      draft = this.draft();
    if (!item || !draft || item.status !== 'AwaitingReview') return;
    await this.run(async () => {
      try {
        const body = {
          expectedVersion: item.version,
          draft,
          note: this.note(),
          acknowledgeWarnings: this.acknowledged(),
        };
        const current = await firstValueFrom(
          approve ? this.api.approvePdf(item.id, body) : this.api.savePdf(item.id, body),
        );
        if (this.selected()?.id !== item.id) return;
        this.selected.set(current);
        this.applyDraft(current);
        this.notice.set(
          approve
            ? 'Human approval recorded. The normal import pipeline will validate and classify every row.'
            : 'Draft saved and validated; no approved data has changed.',
        );
        await Promise.all([this.refresh(), this.importsStore.loadImports()]);
      } catch (error) {
        await this.handleWriteError(error, item.id);
        throw error;
      }
    });
  }
  async reject(): Promise<void> {
    const item = this.selected();
    if (!item) return;
    await this.run(async () => {
      try {
        const current = await firstValueFrom(
          this.api.rejectPdf(item.id, item.version, this.note()),
        );
        if (this.selected()?.id === item.id) {
          this.selected.set(current);
          this.applyDraft(current);
        }
        this.notice.set('PDF rejected. Approved vote data was not changed.');
        await this.refresh();
      } catch (error) {
        await this.handleWriteError(error, item.id);
        throw error;
      }
    });
  }
  async retry(item: PdfExtraction): Promise<void> {
    await this.run(async () => {
      try {
        await firstValueFrom(this.api.retryPdf(item.id, item.version));
        this.notice.set('Extraction queued for another attempt.');
        await this.refresh();
      } catch (error) {
        await this.handleWriteError(error, item.id);
        throw error;
      }
    });
  }
  private async handleWriteError(error: unknown, id: string): Promise<void> {
    if (!(error instanceof HttpErrorResponse) || this.selected()?.id !== id) return;
    if (error.status === 409) {
      const current = await firstValueFrom(this.api.pdfImport(id));
      if (this.selected()?.id === id) {
        this.selected.set(current);
        this.applyDraft(current);
      }
      return;
    }
    const body: unknown = error.error;
    if (body && typeof body === 'object' && 'issues' in body && Array.isArray(body.issues)) {
      // Reload validation through the typed endpoint by saving only when explicitly requested; retain edits on 422.
      const issues = body.issues.filter(
        (i: unknown): i is PdfIssue =>
          !!i &&
          typeof i === 'object' &&
          'code' in i &&
          typeof i.code === 'string' &&
          'message' in i &&
          typeof i.message === 'string' &&
          'severity' in i &&
          (i.severity === 'Error' || i.severity === 'Warning') &&
          'rowNumber' in i &&
          (i.rowNumber === null || typeof i.rowNumber === 'number'),
      );
      this.issues.set(issues);
    }
  }
  async reparse(): Promise<void> {
    const item = this.selected();
    if (!item || this.dirty()) return;
    await this.run(async () => {
      try {
        const current = await firstValueFrom(this.api.reparsePdf(item.id, item.version));
        if (this.selected()?.id === item.id) {
          this.selected.set(current);
          this.applyDraft(current);
        }
        this.notice.set('Stored analysis re-read. No new Azure request was sent.');
        await this.refresh();
      } catch (error) {
        await this.handleWriteError(error, item.id);
        throw error;
      }
    });
  }
  async ensureSource(): Promise<void> {
    const item = this.selected();
    if (!item || this.sourceUrl()) return;
    const request = this.selectionRequest;
    try {
      const blob = await firstValueFrom(this.api.pdfSource(item.id));
      if (request === this.selectionRequest && this.selected()?.id === item.id)
        this.sourceUrl.set(URL.createObjectURL(blob));
    } catch (error) {
      this.showError(error);
    }
  }
  closeSource(): void {
    this.selectionRequest++;
    this.revokeSource();
  }
  private revokeSource(): void {
    const url = this.sourceUrl();
    if (url) URL.revokeObjectURL(url);
    this.sourceUrl.set(null);
  }
}
