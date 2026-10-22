export interface ImportRecord {
  id: string;
  fileName: string;
  status: string;
  rowsProcessed: number;
  rowsTotal: number | null;
  createdAt: string;
  completedAt: string | null;
  originalImportId: string | null;
  uploadedBy: string | null;
}

export interface ImportDetails extends ImportRecord {
  report: ImportReport | null;
}

export interface ImportReport {
  summary?: Record<string, number>;
  votes?: Record<string, Record<string, number>>;
  conflicts?: Record<string, number>;
  flaggedRows?: number;
}

export interface ImportRowDetail {
  rowNumber: number;
  classification: string | null;
  reason: string | null;
  values: Record<string, unknown>;
}

export interface VoteSummary {
  voteId: string;
  session: number;
  voteDate: string;
  title: string;
  stance: string | null;
  weight: number | null;
  pendingConflicts: number;
}

export interface VoteDetails {
  vote: VoteSummary & {
    voteVersionId: number;
    publishedAt: string;
    position: {
      versionId: number;
      stance: string;
      weight: number;
      updatedBy: string;
      note: string | null;
      updatedAt: string;
    } | null;
  };
  countryVotes: CountryVoteDetail[];
  history: VoteHistoryEntry[];
}

export interface CountryVoteDetail {
  countryCode: string;
  countryName: string;
  ballot: string;
  versionId: number;
  sourceImportId: string;
}

export interface VoteHistoryEntry {
  action: string;
  actor: string;
  oldValue: unknown;
  newValue: unknown;
  note: string | null;
  correlationId: string | null;
  occurredAt: string;
}

export interface ConflictProposal {
  value: Record<string, unknown>;
  source: {
    importId: string;
    fileName: string;
    uploadedBy: string | null;
    uploadedAt: string;
    rowNumber?: number | null;
  };
}

export interface ConflictRecord {
  id: string;
  targetType: string;
  voteId: string;
  countryCode: string | null;
  currentValue: Record<string, unknown>;
  currentSource?: {
    fileName: string;
    uploadedBy: string | null;
    uploadedAt: string;
  } | null;
  proposals: ConflictProposal[];
  createdAt: string;
  status: 'Pending' | 'Accepted' | 'Rejected';
  decisionValue: Record<string, unknown> | null;
  decidedBy: string | null;
  decisionNote: string | null;
  decidedAt: string | null;
}

export interface SnapshotRecord {
  id: string;
  status: string;
  ruleVersion: string;
  createdBy: string;
  createdAt: string;
  completedAt: string | null;
  publicationYear: number | null;
  publicationQuarter: number | null;
  publishedAt: string | null;
  withdrawnAt: string | null;
  supersededAt: string | null;
  progressCurrent: number;
  progressTotal: number;
  selectedVoteCount: number;
  error?: string | null;
}

export interface SnapshotResult {
  countryCode: string;
  countryName: string;
  rank: number | null;
  alignmentScore: number | null;
  participationPercent: number | null;
  coveragePercent: number | null;
  approvedMemberRecords: number;
  eligibleMemberVotes: number;
}

export interface VoteContribution {
  ordinal: number;
  voteId: string;
  voteDate: string;
  title: string;
  instituteStance: string;
  weight: number;
  memberAtVote: boolean;
  ballot: string | null;
  alignmentNumerator: number;
  alignmentDenominator: number;
  participationNumerator: number;
  participationDenominator: number;
  coverageNumerator: number;
  coverageDenominator: number;
}
