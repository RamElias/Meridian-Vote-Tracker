import { Injectable, computed, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';

import { ImportDetails, ImportRowDetail, ImportRecord } from '../../models';

import { FeatureStore } from '../../shared/feature-store';

@Injectable({ providedIn: 'root' })
export class ImportsStore extends FeatureStore {
  readonly processingCount = computed(
    () => this.imports().filter((item) => ['Queued', 'Running'].includes(item.status)).length,
  );

  readonly completedImportCount = computed(
    () => this.imports().filter((item) => item.status === 'Completed').length,
  );

  readonly imports = signal<ImportRecord[]>([]);

  readonly importsHasMore = signal(false);

  readonly selectedImport = signal<ImportDetails | null>(null);

  readonly importRows = signal<ImportRowDetail[]>([]);

  readonly importRowsHasMore = signal(false);

  readonly importRowFilter = signal('');

  private selectedFile: File | null = null;

  onFileSelected(event: Event): void {
    this.selectedFile = (event.target as HTMLInputElement).files?.[0] ?? null;
    this.notice.set('');
  }

  async upload(): Promise<void> {
    this.fieldErrors.set({});
    if (!this.selectedFile) {
      this.fieldErrors.set({ file: 'Choose a CSV file first.' });
      return;
    }
    if (
      !this.selectedFile.name.toLowerCase().endsWith('.csv') ||
      this.selectedFile.size > 150 * 1024 * 1024
    ) {
      this.fieldErrors.set({ file: 'Choose a CSV file up to 150 MB.' });
      return;
    }
    await this.run(async () => {
      const uploaded = await firstValueFrom(this.api.upload(this.selectedFile!));
      this.notice.set(
        uploaded.originalImportId
          ? 'This file already exists; history was updated without reprocessing.'
          : 'Import queued. Progress continues if you leave this page.',
      );
      this.selectedFile = null;
      await this.loadImports();
    });
  }

  async openImport(item: Pick<ImportRecord, 'id'>): Promise<void> {
    await this.run(async () => {
      this.selectedImport.set(await firstValueFrom(this.api.importDetails(item.id)));
      this.importRowFilter.set('');
      const rows = await firstValueFrom(this.api.importRows(item.id));
      this.importRows.set(rows);
      this.importRowsHasMore.set(rows.length === 100);
    });
  }

  async setImportRowFilter(classification: string): Promise<void> {
    this.importRowFilter.set(classification);
    const id = this.selectedImport()?.id;
    if (id)
      await this.run(async () => {
        const rows = await firstValueFrom(this.api.importRows(id, classification));
        this.importRows.set(rows);
        this.importRowsHasMore.set(rows.length === 100);
      });
  }

  async loadMoreImportRows(): Promise<void> {
    const id = this.selectedImport()?.id;
    const lastRow = this.importRows().at(-1)?.rowNumber;
    if (!id || lastRow === undefined) return;
    await this.run(async () => {
      const rows = await firstValueFrom(this.api.importRows(id, this.importRowFilter(), lastRow));
      this.importRows.update((current) => [...current, ...rows]);
      this.importRowsHasMore.set(rows.length === 100);
    });
  }

  closeImportDetails(): void {
    this.selectedImport.set(null);
    this.importRows.set([]);
    this.importRowFilter.set('');
  }

  importSummary(): { key: string; label: string; count: number }[] {
    const summary = this.selectedImport()?.report?.summary ?? {};
    return [
      { key: 'New', label: 'New records', count: Number(summary['New'] ?? 0) },
      { key: 'NoChange', label: 'Unchanged', count: Number(summary['NoChange'] ?? 0) },
      { key: 'Duplicate', label: 'Duplicates', count: Number(summary['Duplicate'] ?? 0) },
      { key: 'Invalid', label: 'Invalid rows', count: Number(summary['Invalid'] ?? 0) },
      {
        key: 'Contradiction',
        label: 'Contradictions',
        count: Number(summary['Contradiction'] ?? 0),
      },
      { key: 'Conflict', label: 'Needs review', count: Number(summary['Conflict'] ?? 0) },
    ];
  }

  importVoteRows(): {
    voteId: string;
    counts: { key: string; label: string; count: number }[];
    total: number;
  }[] {
    const votes = this.selectedImport()?.report?.votes ?? {};
    return Object.entries(votes).map(([voteId, rawCounts]) => {
      const counts = Object.entries(rawCounts).map(([key, count]) => ({
        key,
        label: this.outcomeLabel(key),
        count: Number(count),
      }));
      return { voteId, counts, total: counts.reduce((sum, item) => sum + item.count, 0) };
    });
  }

  outcomeLabel(key: string): string {
    const labels: Record<string, string> = {
      New: 'New',
      NoChange: 'Unchanged',
      Duplicate: 'Duplicate',
      Invalid: 'Invalid',
      Contradiction: 'Contradiction',
      Conflict: 'Needs review',
      Unclassified: 'Unclassified',
    };
    return labels[key] ?? key;
  }

  async loadImports(): Promise<void> {
    try {
      const page = await firstValueFrom(this.api.imports());
      this.imports.set(page);
      this.importsHasMore.set(page.length === 100);
      const selected = this.selectedImport();
      if (selected && ['Queued', 'Running'].includes(selected.status)) {
        const details = await firstValueFrom(this.api.importDetails(selected.id));
        if (this.selectedImport()?.id !== selected.id) return;
        this.selectedImport.set(details);
        if (details.status === 'Completed') {
          const rows = await firstValueFrom(
            this.api.importRows(selected.id, this.importRowFilter()),
          );
          if (this.selectedImport()?.id !== selected.id) return;
          this.importRows.set(rows);
          this.importRowsHasMore.set(rows.length === 100);
        }
      }
    } catch (error) {
      this.showError(error);
    }
  }

  async loadMoreImports(): Promise<void> {
    await this.run(async () => {
      const page = await firstValueFrom(this.api.imports(this.imports().length));
      this.imports.update((current) => [...current, ...page]);
      this.importsHasMore.set(page.length === 100);
    });
  }

  normalizationChanges(row: ImportRowDetail): string {
    return Object.entries(row.normalizations)
      .map(([field, change]) => {
        if (!change || typeof change !== 'object') return field;
        const pair = change as Record<string, unknown>;
        return `${field}: ${JSON.stringify(pair['original'])} → ${JSON.stringify(pair['normalized'])}`;
      })
      .join('; ');
  }

  hasSelectedFile(): boolean {
    return this.selectedFile !== null;
  }

  selectedFileName(): string {
    return this.selectedFile?.name ?? 'Drop your CSV here';
  }
}
