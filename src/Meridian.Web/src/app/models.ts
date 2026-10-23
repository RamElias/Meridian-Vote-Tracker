// The API contract has one source of truth: openapi.json. Regenerate with npm run generate:api.
export type {
  ImportRecord, ImportDetails, ImportReport, ImportRow as ImportRowDetail,
  VoteSummary, VoteDetails, CountryBallot as CountryVoteDetail, VoteHistory as VoteHistoryEntry,
  ConflictProposal, Conflict as ConflictRecord, Snapshot as SnapshotRecord,
  SnapshotResult, VoteContribution,
} from './generated-api/models';
