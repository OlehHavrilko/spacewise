namespace Spacewise.Data;

// Spec §8. Append migrations; never edit a released one.
// Differences from §8: paths.parent_id is 0 for a root segment (UNIQUE treats NULLs as distinct),
// scans keeps the scan GUID and the stopped-early flag, names are plain because the whole file is encrypted (ADR 0005).
public static class Schemas
{
    // Regenerable: on loss it is recreated and the volume rescanned.
    public static readonly DatabaseSchema Cache = new("cache", DurableWrites: false,
    [
        """
        CREATE TABLE volumes(id INTEGER PRIMARY KEY, volume_guid TEXT UNIQUE, serial INTEGER, fs TEXT, label TEXT,
            capabilities_json TEXT, is_ssd INTEGER, last_seen_utc TEXT);
        CREATE TABLE paths(id INTEGER PRIMARY KEY, parent_id INTEGER NOT NULL, name TEXT NOT NULL);
        CREATE UNIQUE INDEX paths_parent_name ON paths(parent_id, name);
        CREATE TABLE scans(id INTEGER PRIMARY KEY, scan_guid TEXT NOT NULL UNIQUE, volume_id INTEGER REFERENCES volumes(id),
            root_path_id INTEGER NOT NULL REFERENCES paths(id), started_utc TEXT, completed_utc TEXT NOT NULL,
            state TEXT NOT NULL, mode TEXT NOT NULL, policy_version TEXT, catalog_version TEXT,
            files INTEGER NOT NULL, dirs INTEGER, logical_bytes INTEGER NOT NULL, allocated_bytes INTEGER,
            excluded_by_policy INTEGER NOT NULL, inaccessible INTEGER NOT NULL, unvisited INTEGER NOT NULL,
            stopped_early INTEGER NOT NULL, error_count INTEGER NOT NULL, usn_journal_id INTEGER, next_usn INTEGER);
        CREATE TABLE folder_agg(scan_id INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE, path_id INTEGER NOT NULL,
            depth INTEGER NOT NULL, files INTEGER NOT NULL, logical_bytes INTEGER NOT NULL, allocated_bytes INTEGER,
            newest_write_utc TEXT, oldest_access_utc TEXT, category_hint TEXT, PRIMARY KEY(scan_id, path_id)) WITHOUT ROWID;
        CREATE TABLE top_files(scan_id INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE, path_id INTEGER NOT NULL,
            file_id BLOB, logical_bytes INTEGER NOT NULL, allocated_bytes INTEGER, last_write_utc TEXT, links INTEGER);
        CREATE INDEX top_files_scan ON top_files(scan_id);
        CREATE TABLE findings(id INTEGER PRIMARY KEY, scan_id INTEGER NOT NULL REFERENCES scans(id) ON DELETE CASCADE,
            rule_id TEXT, rule_version INTEGER, path_id INTEGER, bytes_reclaimable INTEGER, source TEXT, category TEXT,
            consequence TEXT, risk_class TEXT, p_calibrated REAL, conformal_set_size INTEGER, flags TEXT, evidence_json TEXT);
        CREATE TABLE volume_snapshot(id INTEGER PRIMARY KEY, volume_id INTEGER REFERENCES volumes(id), taken_utc TEXT NOT NULL,
            total_bytes INTEGER, free_bytes INTEGER, available_bytes INTEGER, trigger TEXT);
        CREATE TABLE app(id INTEGER PRIMARY KEY, kind TEXT, display_name TEXT, publisher TEXT, install_path_id INTEGER,
            uninstall_key TEXT, winget_id TEXT, measured_bytes INTEGER, estimated_bytes INTEGER, install_date TEXT, state TEXT);
        CREATE TABLE app_executable(app_id INTEGER NOT NULL REFERENCES app(id) ON DELETE CASCADE, exe_path_id INTEGER NOT NULL,
            PRIMARY KEY(app_id, exe_path_id));
        CREATE TABLE usage_observation(app_id INTEGER NOT NULL REFERENCES app(id) ON DELETE CASCADE, source TEXT NOT NULL,
            last_seen_utc TEXT, run_count INTEGER, sid_hash TEXT NOT NULL, observed_in_scan INTEGER,
            PRIMARY KEY(app_id, source, sid_hash));
        CREATE TABLE coverage_window(source TEXT NOT NULL, sid_hash TEXT NOT NULL, from_utc TEXT, to_utc TEXT, gaps_json TEXT,
            PRIMARY KEY(source, sid_hash));
        CREATE TABLE app_usage_status(app_id INTEGER PRIMARY KEY REFERENCES app(id) ON DELETE CASCADE, status TEXT,
            evidence_level TEXT, window_days INTEGER, winning_source TEXT, computed_utc TEXT);
        """,
    ]);

    // Valuable: never trimmed automatically except purged quarantine older than 90 days (FR-DATA-002).
    public static readonly DatabaseSchema Journal = new("journal", DurableWrites: true,
    [
        """
        CREATE TABLE plans(id INTEGER PRIMARY KEY, created_utc TEXT NOT NULL, scan_id INTEGER, digest BLOB, origin TEXT,
            state TEXT, elevated INTEGER, expires_utc TEXT, free_before_json TEXT, free_after_json TEXT);
        CREATE TABLE plan_items(plan_id INTEGER NOT NULL REFERENCES plans(id), seq INTEGER NOT NULL, method TEXT,
            volume_guid TEXT, file_id BLOB, path_enc BLOB, expected_size INTEGER, expected_mtime TEXT, rule_id TEXT,
            rule_version INTEGER, action_id TEXT, state TEXT, skip_reason TEXT, PRIMARY KEY(plan_id, seq));
        CREATE TABLE actions(id INTEGER PRIMARY KEY, plan_id INTEGER, seq INTEGER, started_utc TEXT, completed_utc TEXT,
            result_code INTEGER, bytes_planned INTEGER, bytes_freed_measured INTEGER, native_reported INTEGER, detail TEXT);
        CREATE TABLE quarantine(id INTEGER PRIMARY KEY, plan_id INTEGER, seq INTEGER, original_path_enc BLOB,
            original_volume_guid TEXT, stored_path_enc BLOB, sha256 BLOB, size INTEGER, attrs INTEGER, sddl TEXT,
            created_utc TEXT, expires_utc TEXT, restored_utc TEXT, purged_utc TEXT);
        CREATE TABLE decision_journal(id INTEGER PRIMARY KEY, ts TEXT NOT NULL, actor TEXT, mode TEXT, fact_id TEXT,
            path_hash BLOB, path_enc BLOB, bytes INTEGER, category TEXT, p_calibrated REAL, model_versions_json TEXT,
            policy_rule_ids_json TEXT, llm_explanation TEXT, user_action TEXT, executor_result TEXT, undo_token TEXT,
            prev_hash BLOB, row_hash BLOB);
        CREATE TABLE feedback(id INTEGER PRIMARY KEY, finding_key TEXT, verdict TEXT, created_utc TEXT);
        CREATE TABLE user_rules(id INTEGER PRIMARY KEY, kind TEXT, scope_json TEXT, created_utc TEXT, revision INTEGER);
        CREATE TABLE corridor(id INTEGER PRIMARY KEY, enabled INTEGER, level TEXT, categories_json TEXT, volumes_json TEXT,
            min_age_days INTEGER, max_bytes_per_run INTEGER, trigger_json TEXT, method TEXT, revision INTEGER, updated_utc TEXT);
        CREATE TABLE model_installations(model_id TEXT NOT NULL, revision TEXT NOT NULL, sha256 BLOB, path TEXT, license TEXT,
            tier TEXT, installed_utc TEXT, eval_version TEXT, state TEXT, PRIMARY KEY(model_id, revision));
        CREATE TABLE settings(key TEXT PRIMARY KEY, value_json TEXT);
        """,
    ]);
}
