import { PdfImportsStore } from './features/pdf-imports/pdf-imports.store';
import { Injectable, inject, signal, computed, OnInit, OnDestroy } from '@angular/core';
import { ImportsStore } from './features/imports/imports.store';
import { ReviewStore } from './features/review/review.store';
import { PositionsStore } from './features/positions/positions.store';
import { SnapshotsStore } from './features/snapshots/snapshots.store';
import { ApiService } from './api.service';
type Section = 'imports' | 'review' | 'positions' | 'snapshots';
@Injectable({ providedIn: 'root' })
export class WorkspaceStore implements OnInit, OnDestroy {
  readonly importsStore = inject(ImportsStore);
  readonly reviewStore = inject(ReviewStore);
  readonly positionsStore = inject(PositionsStore);
  readonly snapshotsStore = inject(SnapshotsStore);
  readonly pdfStore = inject(PdfImportsStore);
  readonly api = inject(ApiService);
  readonly section = signal<Section>('imports');
  readonly activeStore = computed(
    () =>
      ({
        imports: this.importsStore,
        review: this.reviewStore,
        positions: this.positionsStore,
        snapshots: this.snapshotsStore,
      })[this.section()],
  );
  readonly error = computed(() => this.activeStore().error());
  readonly notice = computed(() => this.activeStore().notice());
  clearMessages(): void {
    this.activeStore().clearMessages();
  }
  private poller?: ReturnType<typeof setInterval>;
  async ngOnInit(): Promise<void> {
    await this.refreshAll();
    this.poller = setInterval(() => {
      if (this.pdfStore.items().some((i) => ['Queued', 'Running'].includes(i.status)))
        void this.pdfStore.refresh();
      if (this.importsStore.imports().some((item) => ['Queued', 'Running'].includes(item.status)))
        void this.importsStore.loadImports();
      if (
        this.snapshotsStore
          .snapshots()
          .some((item) => ['Queued', 'Running'].includes(item.status)) ||
        ['Queued', 'Running'].includes(this.snapshotsStore.selectedSnapshot()?.status ?? '')
      )
        void this.snapshotsStore.loadSnapshots();
    }, 2500);
  }
  ngOnDestroy(): void {
    if (this.poller) clearInterval(this.poller);
  }
  async refreshAll(): Promise<void> {
    this.importsStore.error.set('');
    this.reviewStore.error.set('');
    this.positionsStore.error.set('');
    this.snapshotsStore.error.set('');
    await Promise.all([
      this.pdfStore.refresh(),
      this.importsStore.loadImports(),
      this.reviewStore.loadConflicts(),
      this.positionsStore.loadVotes(),
      this.snapshotsStore.loadSnapshots(),
    ]);
  }
  async chooseSection(section: Section): Promise<void> {
    this.section.set(section);
    this.activeStore().error.set('');
    if (section === 'imports')
      await Promise.all([this.importsStore.loadImports(), this.pdfStore.refresh()]);
    if (section === 'review')
      await Promise.all([this.reviewStore.loadConflicts(), this.pdfStore.refresh()]);
    if (section === 'positions') await this.positionsStore.loadVotes();
    if (section === 'snapshots') await this.snapshotsStore.loadSnapshots();
  }
  setUser(user: string): void {
    this.api.setDemoUser(user);
    this.positionsStore.selectedVote.set(null);
    this.importsStore.closeImportDetails();
    void this.refreshAll();
  }
  readonly imports = this.importsStore.imports;
  readonly processingCount = this.importsStore.processingCount;
  readonly completedImportCount = this.importsStore.completedImportCount;
  readonly closeImportDetails = this.importsStore.closeImportDetails.bind(this.importsStore);
  readonly conflicts = this.reviewStore.conflicts;
  readonly snapshots = this.snapshotsStore.snapshots;
}
