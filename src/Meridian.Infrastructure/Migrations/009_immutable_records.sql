CREATE INDEX ix_jobs_import_id ON background_jobs((payload->>'importId')) WHERE job_type='csv_import';
CREATE FUNCTION reject_immutable_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN RAISE EXCEPTION '% is append-only', TG_TABLE_NAME USING ERRCODE='55000'; END $$;
CREATE TRIGGER immutable_vote_versions BEFORE UPDATE OR DELETE ON vote_versions FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER immutable_ballot_versions BEFORE UPDATE OR DELETE ON country_vote_versions FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER immutable_position_versions BEFORE UPDATE OR DELETE ON position_versions FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER immutable_audit BEFORE UPDATE OR DELETE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE FUNCTION protect_completed_snapshot() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 IF OLD.status='Completed' THEN
  IF TG_OP='DELETE' THEN RAISE EXCEPTION 'Completed snapshots cannot be deleted' USING ERRCODE='55000'; END IF;
  IF (to_jsonb(NEW)-ARRAY['publication_year','publication_quarter','published_at','published_by_user_id','withdrawn_at','withdrawn_by_user_id','superseded_at','superseded_by']) IS DISTINCT FROM
     (to_jsonb(OLD)-ARRAY['publication_year','publication_quarter','published_at','published_by_user_id','withdrawn_at','withdrawn_by_user_id','superseded_at','superseded_by']) THEN
   RAISE EXCEPTION 'Completed snapshot inputs and results are immutable' USING ERRCODE='55000';
  END IF;
 END IF;
 IF TG_OP='DELETE' THEN RETURN OLD; END IF;
 RETURN NEW;
END $$;
CREATE TRIGGER immutable_completed_snapshot BEFORE UPDATE OR DELETE ON ranking_snapshots FOR EACH ROW EXECUTE FUNCTION protect_completed_snapshot();
-- Statement transition tables avoid a PL/pgSQL invocation for every contribution row.
CREATE FUNCTION protect_snapshot_child_rows() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
 PERFORM 1 FROM ranking_snapshots s WHERE s.id IN (SELECT DISTINCT snapshot_id FROM changed_rows) FOR SHARE;
 IF EXISTS(SELECT 1 FROM changed_rows r JOIN ranking_snapshots s ON s.id=r.snapshot_id WHERE s.status='Completed') THEN
  RAISE EXCEPTION 'Completed snapshot inputs and results are immutable' USING ERRCODE='55000';
 END IF;
 RETURN NULL;
END $$;
CREATE TRIGGER protect_snapshot_votes_insert AFTER INSERT ON ranking_snapshot_votes REFERENCING NEW TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_snapshot_votes_update AFTER UPDATE ON ranking_snapshot_votes REFERENCING OLD TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_snapshot_votes_delete AFTER DELETE ON ranking_snapshot_votes REFERENCING OLD TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_contributions_insert AFTER INSERT ON ranking_snapshot_contributions REFERENCING NEW TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_contributions_update AFTER UPDATE ON ranking_snapshot_contributions REFERENCING OLD TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_contributions_delete AFTER DELETE ON ranking_snapshot_contributions REFERENCING OLD TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_results_insert AFTER INSERT ON ranking_snapshot_results REFERENCING NEW TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_results_update AFTER UPDATE ON ranking_snapshot_results REFERENCING OLD TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_results_delete AFTER DELETE ON ranking_snapshot_results REFERENCING OLD TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();

CREATE TRIGGER protect_snapshot_votes_update_destination AFTER UPDATE ON ranking_snapshot_votes REFERENCING NEW TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_snapshot_votes_truncate BEFORE TRUNCATE ON ranking_snapshot_votes FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();

CREATE TRIGGER protect_contributions_update_destination AFTER UPDATE ON ranking_snapshot_contributions REFERENCING NEW TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_contributions_truncate BEFORE TRUNCATE ON ranking_snapshot_contributions FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();

CREATE TRIGGER protect_results_update_destination AFTER UPDATE ON ranking_snapshot_results REFERENCING NEW TABLE AS changed_rows FOR EACH STATEMENT EXECUTE FUNCTION protect_snapshot_child_rows();
CREATE TRIGGER protect_results_truncate BEFORE TRUNCATE ON ranking_snapshot_results FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER protect_vote_versions_truncate BEFORE TRUNCATE ON vote_versions FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER protect_country_vote_versions_truncate BEFORE TRUNCATE ON country_vote_versions FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER protect_position_versions_truncate BEFORE TRUNCATE ON position_versions FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER protect_audit_events_truncate BEFORE TRUNCATE ON audit_events FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
CREATE TRIGGER protect_ranking_snapshots_truncate BEFORE TRUNCATE ON ranking_snapshots FOR EACH STATEMENT EXECUTE FUNCTION reject_immutable_change();
