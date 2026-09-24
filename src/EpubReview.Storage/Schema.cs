namespace EpubReview.Storage;

internal static class Schema
{
    public const string Sql = """
CREATE TABLE IF NOT EXISTS projects(
    project_id TEXT PRIMARY KEY,
    title TEXT NOT NULL,
    created_at TEXT NOT NULL,
    head_event_id TEXT,
    head_seq INTEGER NOT NULL,
    head_hash TEXT NOT NULL
);
CREATE TABLE IF NOT EXISTS branches(
    branch_id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    name TEXT NOT NULL,
    parent_branch_id TEXT,
    fork_event_id TEXT,
    head_event_id TEXT,
    head_seq INTEGER NOT NULL,
    head_hash TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(project_id, branch_id)
);
CREATE TABLE IF NOT EXISTS events(
    event_id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    branch_id TEXT NOT NULL,
    seq INTEGER NOT NULL,
    parent_event_id TEXT,
    prev_hash TEXT NOT NULL,
    event_hash TEXT NOT NULL,
    kind TEXT NOT NULL,
    occurred_at TEXT NOT NULL,
    actor TEXT NOT NULL,
    summary TEXT NOT NULL,
    payload_json TEXT NOT NULL,
    input_summaries_json TEXT NOT NULL DEFAULT '[]',
    params_version TEXT,
    parent_deps_json TEXT NOT NULL DEFAULT '[]',
    topic TEXT,
    PRIMARY KEY(project_id, branch_id, seq),
    UNIQUE(project_id, event_id)
);
CREATE TABLE IF NOT EXISTS materials(
    material_set_id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    content_version TEXT NOT NULL,
    zip_bytes BLOB NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(project_id, material_set_id)
);
CREATE TABLE IF NOT EXISTS analyses(
    analysis_id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    branch_id TEXT NOT NULL,
    dependency_version TEXT NOT NULL,
    material_set_id TEXT NOT NULL,
    params_version_id TEXT NOT NULL,
    result_json TEXT NOT NULL,
    created_at TEXT NOT NULL,
    PRIMARY KEY(project_id, analysis_id)
);
CREATE TABLE IF NOT EXISTS decisions(
    decision_id TEXT NOT NULL,
    project_id TEXT NOT NULL REFERENCES projects(project_id) ON DELETE CASCADE,
    branch_id TEXT NOT NULL,
    event_id TEXT NOT NULL,
    finding_id TEXT NOT NULL,
    verdict TEXT NOT NULL,
    rationale TEXT NOT NULL,
    adjusted_params_json TEXT,
    conflicts INTEGER NOT NULL DEFAULT 0,
    created_at TEXT NOT NULL,
    PRIMARY KEY(project_id, decision_id)
);
CREATE TABLE IF NOT EXISTS recovery_log(
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    project_id TEXT,
    operation TEXT NOT NULL,
    evidence TEXT NOT NULL,
    outcome TEXT NOT NULL,
    recovered_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_events_project ON events(project_id, occurred_at);
CREATE INDEX IF NOT EXISTS ix_analyses_dep ON analyses(project_id, dependency_version);
""";
}
