CREATE TABLE pdf_extractions (
 id uuid PRIMARY KEY REFERENCES background_jobs(id),
 file_name text NOT NULL,
 file_path text NOT NULL,
 content_sha256 char(64) NOT NULL,
 status text NOT NULL CHECK(status IN ('Queued','Running','AwaitingReview','Accepted','Rejected','Failed','Duplicate')),
 original_extraction_id uuid NULL REFERENCES pdf_extractions(id),
 uploaded_by_user_id text NOT NULL REFERENCES app_users(user_id),
 created_at timestamptz NOT NULL DEFAULT now(),
 updated_at timestamptz NOT NULL DEFAULT now(),
 version integer NOT NULL DEFAULT 1 CHECK(version>0),
 operation_url text NULL,
 analysis jsonb NULL,
 extracted_draft jsonb NULL,
 draft jsonb NULL,
 issues jsonb NOT NULL DEFAULT '[]',
 error_message text NULL,
 reviewed_by_user_id text NULL REFERENCES app_users(user_id),
 reviewed_at timestamptz NULL,
 decision_note text NULL,
 import_id uuid NULL REFERENCES imports(id)
);
CREATE INDEX ix_pdf_hash ON pdf_extractions(content_sha256,created_at);
CREATE INDEX ix_pdf_status ON pdf_extractions(status,created_at);
ALTER TABLE imports ADD COLUMN source_pdf_extraction_id uuid NULL REFERENCES pdf_extractions(id);
CREATE TRIGGER protect_reviewed_pdf BEFORE UPDATE OR DELETE ON pdf_extractions
 FOR EACH ROW WHEN (OLD.status IN ('Accepted','Rejected','Duplicate')) EXECUTE FUNCTION reject_immutable_change();
