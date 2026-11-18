CREATE TABLE community_local (
    id TEXT PRIMARY KEY,
    revision INTEGER NOT NULL CHECK (revision > 0),
    position INTEGER NOT NULL,
    favorite INTEGER NOT NULL CHECK (favorite IN (0, 1)),
    content_json TEXT NOT NULL,
    upstream_json TEXT
);
CREATE INDEX community_local_order ON community_local(position, id);

CREATE TABLE community_cache (
    singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
    index_json BLOB NOT NULL,
    snapshot_json BLOB NOT NULL
);

CREATE TABLE community_receipts (
    request_id TEXT PRIMARY KEY,
    fingerprint TEXT NOT NULL,
    response_json TEXT NOT NULL
);
