import { Component, inject, OnInit, OnDestroy } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { WorkspaceStore } from './workspace.store';
import { ImportsPage } from './features/imports/imports.page';
import { ReviewPage } from './features/review/review.page';
import { PositionsPage } from './features/positions/positions.page';
import { SnapshotsPage } from './features/snapshots/snapshots.page';

@Component({
  selector: 'app-root',
  imports: [FormsModule, ImportsPage, ReviewPage, PositionsPage, SnapshotsPage],
  styleUrl: './app.scss',
  templateUrl: './app.html',
})
export class App implements OnInit, OnDestroy {
  readonly workspace = inject(WorkspaceStore);
  readonly api = this.workspace.api;
  readonly chooseSection = this.workspace.chooseSection.bind(this.workspace);
  readonly clearMessages = this.workspace.clearMessages.bind(this.workspace);
  readonly closeImportDetails = this.workspace.closeImportDetails.bind(this.workspace);
  readonly conflicts = this.workspace.conflicts;
  readonly error = this.workspace.error;
  readonly imports = this.workspace.imports;
  readonly notice = this.workspace.notice;
  readonly refreshAll = this.workspace.refreshAll.bind(this.workspace);
  readonly section = this.workspace.section;
  readonly setUser = this.workspace.setUser.bind(this.workspace);
  readonly snapshots = this.workspace.snapshots;
  ngOnInit(): void {
    void this.workspace.ngOnInit();
  }
  ngOnDestroy(): void {
    this.workspace.ngOnDestroy();
  }
}
