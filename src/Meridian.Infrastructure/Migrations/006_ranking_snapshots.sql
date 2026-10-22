CREATE TABLE ranking_snapshots (
    id uuid PRIMARY KEY REFERENCES background_jobs(id),
    status text NOT NULL CHECK (status IN ('Queued','Running','Completed','Failed')),
    selection jsonb NOT NULL,
    calculation_rule_version text NOT NULL,
    created_by_user_id text NOT NULL REFERENCES app_users(user_id),
    created_at timestamptz NOT NULL DEFAULT now(),
    inputs_captured_at timestamptz NULL,
    completed_at timestamptz NULL,
    error_message text NULL,
    publication_year integer NULL,
    publication_quarter smallint NULL CHECK (publication_quarter IS NULL OR publication_quarter BETWEEN 1 AND 4),
    published_at timestamptz NULL,
    published_by_user_id text NULL REFERENCES app_users(user_id),
    withdrawn_at timestamptz NULL,
    withdrawn_by_user_id text NULL REFERENCES app_users(user_id),
    superseded_at timestamptz NULL,
    superseded_by uuid NULL REFERENCES ranking_snapshots(id),
    CHECK ((published_at IS NULL AND publication_year IS NULL AND publication_quarter IS NULL AND published_by_user_id IS NULL) OR (published_at IS NOT NULL AND publication_year IS NOT NULL AND publication_quarter IS NOT NULL AND published_by_user_id IS NOT NULL))
);
CREATE UNIQUE INDEX ux_active_snapshot_publication_period ON ranking_snapshots(publication_year,publication_quarter) WHERE published_at IS NOT NULL AND withdrawn_at IS NULL AND superseded_at IS NULL;
CREATE INDEX ix_ranking_snapshots_created ON ranking_snapshots(created_at DESC);
CREATE TABLE ranking_snapshot_votes (
    snapshot_id uuid NOT NULL REFERENCES ranking_snapshots(id),
    ordinal integer NOT NULL,
    assembly_vote_id text NOT NULL REFERENCES votes(assembly_vote_id),
    vote_version_id bigint NOT NULL REFERENCES vote_versions(id),
    position_version_id bigint NOT NULL REFERENCES position_versions(id),
    vote_date date NOT NULL,
    session integer NOT NULL,
    resolution_title text NOT NULL,
    stance char(1) NOT NULL CHECK (stance IN ('Y','N')),
    weight smallint NOT NULL CHECK (weight BETWEEN 1 AND 5),
    PRIMARY KEY (snapshot_id,assembly_vote_id),
    UNIQUE (snapshot_id,ordinal)
);
CREATE TABLE ranking_snapshot_contributions (
    snapshot_id uuid NOT NULL,
    country_code char(3) NOT NULL REFERENCES member_states(country_code),
    country_name text NOT NULL,
    assembly_vote_id text NOT NULL,
    country_vote_version_id bigint NULL REFERENCES country_vote_versions(id),
    ballot char(1) NULL CHECK (ballot IS NULL OR ballot IN ('Y','N','A','X')),
    member_at_vote boolean NOT NULL,
    alignment_numerator numeric(12,2) NOT NULL,
    alignment_denominator numeric(12,2) NOT NULL,
    participation_numerator integer NOT NULL,
    participation_denominator integer NOT NULL,
    coverage_numerator integer NOT NULL,
    coverage_denominator integer NOT NULL,
    PRIMARY KEY (snapshot_id,country_code,assembly_vote_id),
    FOREIGN KEY (snapshot_id,assembly_vote_id) REFERENCES ranking_snapshot_votes(snapshot_id,assembly_vote_id)
);
CREATE INDEX ix_snapshot_contributions_detail ON ranking_snapshot_contributions(snapshot_id,country_code,assembly_vote_id);
CREATE TABLE ranking_snapshot_results (
    snapshot_id uuid NOT NULL REFERENCES ranking_snapshots(id),
    country_code char(3) NOT NULL REFERENCES member_states(country_code),
    country_name text NOT NULL,
    rank integer NULL,
    alignment_score numeric(9,4) NULL,
    alignment_numerator numeric(12,2) NOT NULL,
    alignment_denominator numeric(12,2) NOT NULL,
    participation_percent numeric(9,4) NULL,
    participating_ballots integer NOT NULL,
    approved_ballots integer NOT NULL,
    coverage_percent numeric(9,4) NULL,
    approved_member_records integer NOT NULL,
    eligible_member_votes integer NOT NULL,
    PRIMARY KEY (snapshot_id,country_code)
);
CREATE INDEX ix_snapshot_results_rank ON ranking_snapshot_results(snapshot_id,rank,country_code);
