ALTER TABLE import_staging_rows ADD COLUMN normalized_values jsonb;
ALTER TABLE import_staging_rows ADD COLUMN normalizations jsonb NOT NULL DEFAULT '{}'::jsonb;
UPDATE import_staging_rows SET normalized_values=raw_values;
ALTER TABLE import_staging_rows ALTER COLUMN normalized_values SET NOT NULL;
CREATE INDEX ix_staging_normalized_deferred_vote ON import_staging_rows((normalized_values->>'assembly_vote_id'))
    WHERE classification='Conflict' AND reason='Membership validation depends on the pending vote-date decision.';
CREATE TABLE job_progress (
    job_id uuid PRIMARY KEY REFERENCES background_jobs(id),
    phase text NOT NULL,
    unit text NOT NULL DEFAULT 'rows',
    current_count bigint NOT NULL DEFAULT 0 CHECK(current_count>=0),
    total_count bigint NULL CHECK(total_count IS NULL OR total_count>=0),
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX ix_staging_unchanged_history ON import_staging_rows((normalized_values->>'assembly_vote_id'),import_id) WHERE classification='NoChange';
