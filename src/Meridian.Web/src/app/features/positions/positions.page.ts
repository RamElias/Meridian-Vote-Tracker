import { Component, inject } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { WorkspaceStore } from '../../workspace.store';

@Component({selector:'app-positions-page', imports:[CommonModule,FormsModule], templateUrl:'./positions.page.html'})
export class PositionsPage {
  private readonly workspace = inject(WorkspaceStore);
  readonly ballotLabel = this.workspace.ballotLabel.bind(this.workspace);
  readonly busy = this.workspace.busy;
  readonly conflicts = this.workspace.conflicts;
  readonly countryCode = this.workspace.countryCode;
  readonly error = this.workspace.error;
  readonly fieldErrors = this.workspace.fieldErrors;
  readonly historyActionLabel = this.workspace.historyActionLabel.bind(this.workspace);
  readonly historyChange = this.workspace.historyChange.bind(this.workspace);
  readonly isAdmin = this.workspace.isAdmin;
  readonly loadMoreVotes = this.workspace.loadMoreVotes.bind(this.workspace);
  readonly openVote = this.workspace.openVote.bind(this.workspace);
  readonly positionNote = this.workspace.positionNote;
  readonly positionStance = this.workspace.positionStance;
  readonly positionWeight = this.workspace.positionWeight;
  readonly savePosition = this.workspace.savePosition.bind(this.workspace);
  readonly search = this.workspace.search;
  readonly searchVotes = this.workspace.searchVotes.bind(this.workspace);
  readonly section = this.workspace.section;
  readonly selectedVote = this.workspace.selectedVote;
  readonly stanceLabel = this.workspace.stanceLabel.bind(this.workspace);
  readonly votes = this.workspace.votes;
  readonly votesHasMore = this.workspace.votesHasMore;
}
