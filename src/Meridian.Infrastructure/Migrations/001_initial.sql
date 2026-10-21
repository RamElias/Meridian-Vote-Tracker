CREATE EXTENSION IF NOT EXISTS btree_gist;
CREATE TABLE member_states (
    country_code char(3) PRIMARY KEY CHECK (country_code ~ '^[A-Z]{3}$'),
    country_name text NOT NULL CHECK (length(trim(country_name)) > 0)
);
CREATE TABLE membership_periods (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    country_code char(3) NOT NULL REFERENCES member_states(country_code),
    member_from date NOT NULL,
    member_to date NULL,
    CHECK (member_to IS NULL OR member_to >= member_from),
    UNIQUE (country_code, member_from),
    EXCLUDE USING gist (country_code WITH =, daterange(member_from, COALESCE(member_to, 'infinity'::date), '[]') WITH &&)
);
CREATE TABLE background_jobs (
    id uuid PRIMARY KEY,
    job_type text NOT NULL,
    status text NOT NULL CHECK (status IN ('Queued','Running','Completed','Failed')),
    progress_current bigint NOT NULL DEFAULT 0 CHECK (progress_current >= 0),
    progress_total bigint NULL CHECK (progress_total IS NULL OR progress_total >= 0),
    payload jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT now(),
    started_at timestamptz NULL,
    completed_at timestamptz NULL,
    error_message text NULL
);
CREATE INDEX ix_background_jobs_status_created ON background_jobs(status, created_at);
CREATE TABLE imports (
    id uuid PRIMARY KEY,
    file_name text NOT NULL,
    content_sha256 char(64) NOT NULL,
    file_path text NOT NULL,
    status text NOT NULL CHECK (status IN ('Queued','Running','Completed','Failed','Duplicate')),
    original_import_id uuid NULL REFERENCES imports(id),
    rows_total bigint NULL,
    rows_processed bigint NOT NULL DEFAULT 0,
    report jsonb NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    completed_at timestamptz NULL,
    UNIQUE (content_sha256)
);
CREATE TABLE import_staging_rows (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    import_id uuid NOT NULL REFERENCES imports(id) ON DELETE CASCADE,
    row_number bigint NOT NULL,
    raw_values jsonb NOT NULL,
    classification text NULL,
    reason text NULL,
    UNIQUE (import_id, row_number)
);
CREATE INDEX ix_staging_import_vote ON import_staging_rows(import_id, ((raw_values->>'assembly_vote_id')));
