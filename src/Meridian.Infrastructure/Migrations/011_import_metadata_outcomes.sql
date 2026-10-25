ALTER TABLE import_staging_rows
    ADD COLUMN ballot_classification text,
    ADD COLUMN metadata_conflict_id uuid REFERENCES conflicts(id);
CREATE INDEX ix_staging_metadata_conflict ON import_staging_rows(metadata_conflict_id)
    WHERE metadata_conflict_id IS NOT NULL;
CREATE INDEX ix_snapshot_history_order ON ranking_snapshots(created_at DESC, id DESC);
