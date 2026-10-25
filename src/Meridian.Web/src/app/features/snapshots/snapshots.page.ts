import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { SnapshotsStore } from './snapshots.store';

@Component({
  selector: 'app-snapshots-page',
  imports: [CommonModule, FormsModule],
  templateUrl: './snapshots.page.html',
})
export class SnapshotsPage {
  private readonly workspace = inject(SnapshotsStore);
  readonly snapshotsHasMore = this.workspace.snapshotsHasMore;
  readonly loadMoreSnapshots = this.workspace.loadMoreSnapshots.bind(this.workspace);
  readonly historyYear = this.workspace.historyYear;
  readonly historyQuarter = this.workspace.historyQuarter;
  readonly historyPublication = this.workspace.historyPublication;
  readonly applyHistoryFilter = this.workspace.applyHistoryFilter.bind(this.workspace);
  readonly ballotLabel = this.workspace.ballotLabel.bind(this.workspace);
  readonly busy = this.workspace.busy;
  readonly contributions = this.workspace.contributions;
  readonly contributionsHasMore = this.workspace.contributionsHasMore;
  readonly contributionsLoading = this.workspace.contributionsLoading;
  readonly countryCode = this.workspace.countryCode;
  readonly createSnapshot = this.workspace.createSnapshot.bind(this.workspace);
  readonly fieldErrors = this.workspace.fieldErrors;
  readonly formatPercent = this.workspace.formatPercent.bind(this.workspace);
  readonly isActivelyPublished = this.workspace.isActivelyPublished.bind(this.workspace);
  readonly isAdmin = this.workspace.isAdmin;
  readonly loadContributions = this.workspace.loadContributions.bind(this.workspace);
  readonly loadMoreContributions = this.workspace.loadMoreContributions.bind(this.workspace);
  readonly loadMoreSnapshotResults = this.workspace.loadMoreSnapshotResults.bind(this.workspace);
  readonly loadSnapshots = this.workspace.loadSnapshots.bind(this.workspace);
  readonly openSnapshot = this.workspace.openSnapshot.bind(this.workspace);
  readonly progress = this.workspace.progress.bind(this.workspace);
  readonly publicationQuarter = this.workspace.publicationQuarter;
  readonly publicationYear = this.workspace.publicationYear;
  readonly publishSnapshot = this.workspace.publishSnapshot.bind(this.workspace);
  readonly replaceActive = this.workspace.replaceActive;
  readonly selectedSnapshot = this.workspace.selectedSnapshot;
  readonly setCountryCode = this.workspace.setCountryCode.bind(this.workspace);
  readonly snapshotFrom = this.workspace.snapshotFrom;
  readonly snapshotResults = this.workspace.snapshotResults;
  readonly snapshotResultsHasMore = this.workspace.snapshotResultsHasMore;
  readonly snapshotSession = this.workspace.snapshotSession;
  readonly snapshotStatus = this.workspace.snapshotStatus.bind(this.workspace);
  readonly snapshotTo = this.workspace.snapshotTo;
  readonly snapshotVoteIds = this.workspace.snapshotVoteIds;
  readonly snapshots = this.workspace.snapshots;
  readonly stanceLabel = this.workspace.stanceLabel.bind(this.workspace);
  readonly statusClass = this.workspace.statusClass.bind(this.workspace);
  readonly withdrawSnapshot = this.workspace.withdrawSnapshot.bind(this.workspace);
}
