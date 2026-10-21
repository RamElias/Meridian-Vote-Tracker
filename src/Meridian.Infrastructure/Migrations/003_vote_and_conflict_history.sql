CREATE TABLE votes (
    assembly_vote_id text PRIMARY KEY,
    current_version_id bigint NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE TABLE vote_versions (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    assembly_vote_id text NOT NULL REFERENCES votes(assembly_vote_id),
    session integer NOT NULL,
    vote_date date NOT NULL,
    resolution_title text NOT NULL,
    published_at timestamptz NOT NULL,
    source_import_id uuid NOT NULL REFERENCES imports(id),
    created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE votes ADD CONSTRAINT fk_votes_current_version FOREIGN KEY (current_version_id) REFERENCES vote_versions(id);
CREATE TABLE country_votes (
    assembly_vote_id text NOT NULL REFERENCES votes(assembly_vote_id),
    country_code char(3) NOT NULL REFERENCES member_states(country_code),
    current_version_id bigint NULL,
    PRIMARY KEY (assembly_vote_id,country_code)
);
CREATE TABLE country_vote_versions (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    assembly_vote_id text NOT NULL,
    country_code char(3) NOT NULL,
    ballot char(1) NOT NULL CHECK (ballot IN ('Y','N','A','X')),
    country_name text NOT NULL,
    source_import_id uuid NOT NULL REFERENCES imports(id),
    created_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (assembly_vote_id,country_code) REFERENCES country_votes(assembly_vote_id,country_code)
);
ALTER TABLE country_votes ADD CONSTRAINT fk_country_votes_current_version FOREIGN KEY (current_version_id) REFERENCES country_vote_versions(id);
CREATE TABLE conflicts (
    id uuid PRIMARY KEY,
    target_type text NOT NULL CHECK (target_type IN ('VoteMetadata','CountryVote')),
    assembly_vote_id text NOT NULL REFERENCES votes(assembly_vote_id),
    country_code char(3) NULL REFERENCES member_states(country_code),
    status text NOT NULL DEFAULT 'Pending' CHECK (status IN ('Pending','Accepted','Rejected')),
    current_value jsonb NOT NULL,
    proposals jsonb NOT NULL DEFAULT '[]'::jsonb,
    opened_by_import_id uuid NOT NULL REFERENCES imports(id),
    decision_value jsonb NULL,
    decided_by text NULL,
    decision_note text NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    decided_at timestamptz NULL,
    CHECK ((target_type='VoteMetadata' AND country_code IS NULL) OR (target_type='CountryVote' AND country_code IS NOT NULL))
);
CREATE UNIQUE INDEX ux_conflicts_open_vote ON conflicts(assembly_vote_id) WHERE target_type='VoteMetadata' AND status='Pending';
CREATE UNIQUE INDEX ux_conflicts_open_country_vote ON conflicts(assembly_vote_id,country_code) WHERE target_type='CountryVote' AND status='Pending';
CREATE INDEX ix_conflicts_pending ON conflicts(status,created_at);
