CREATE INDEX ix_vote_versions_vote_history ON vote_versions(assembly_vote_id,id);
CREATE INDEX ix_country_vote_versions_history ON country_vote_versions(assembly_vote_id,country_code,id);
CREATE INDEX ix_staging_deferred_vote ON import_staging_rows((raw_values->>'assembly_vote_id'))
    WHERE classification='Conflict' AND reason='Membership validation depends on the pending vote-date decision.';
