CREATE TABLE app_users (
    user_id text PRIMARY KEY,
    display_name text NOT NULL,
    role text NOT NULL CHECK (role IN ('Analyst','Admin'))
);
INSERT INTO app_users(user_id,display_name,role) VALUES
    ('dana','Dana','Analyst'),
    ('omer','Omer','Analyst'),
    ('lana','Lana','Admin')
ON CONFLICT (user_id) DO UPDATE SET display_name=EXCLUDED.display_name,role=EXCLUDED.role;
ALTER TABLE imports ADD COLUMN uploaded_by_user_id text NULL REFERENCES app_users(user_id);
CREATE TABLE audit_events (
    id uuid PRIMARY KEY,
    entity_type text NOT NULL,
    entity_key text NOT NULL,
    action text NOT NULL,
    actor_user_id text NOT NULL REFERENCES app_users(user_id),
    old_value jsonb NULL,
    new_value jsonb NULL,
    source_import_id uuid NULL REFERENCES imports(id),
    note text NULL,
    correlation_id text NULL,
    occurred_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_audit_entity ON audit_events(entity_type,entity_key,occurred_at DESC);
