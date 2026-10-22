CREATE TABLE vote_positions (
    assembly_vote_id text PRIMARY KEY REFERENCES votes(assembly_vote_id),
    current_version_id bigint NULL
);
CREATE TABLE position_versions (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    assembly_vote_id text NOT NULL REFERENCES vote_positions(assembly_vote_id),
    stance char(1) NOT NULL CHECK (stance IN ('Y','N')),
    weight smallint NOT NULL CHECK (weight BETWEEN 1 AND 5),
    actor_user_id text NOT NULL REFERENCES app_users(user_id),
    note text NULL,
    created_at timestamptz NOT NULL DEFAULT now()
);
ALTER TABLE vote_positions ADD CONSTRAINT fk_vote_positions_current_version FOREIGN KEY (current_version_id) REFERENCES position_versions(id);
CREATE INDEX ix_position_versions_vote_created ON position_versions(assembly_vote_id,created_at DESC);
