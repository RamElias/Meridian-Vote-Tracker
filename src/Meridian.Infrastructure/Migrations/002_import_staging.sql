ALTER TABLE imports DROP CONSTRAINT imports_content_sha256_key;
ALTER TABLE imports DROP CONSTRAINT imports_status_check;
ALTER TABLE imports ADD CONSTRAINT imports_status_check
    CHECK (status IN ('Queued','Running','Staged','Completed','Failed','Duplicate'));
