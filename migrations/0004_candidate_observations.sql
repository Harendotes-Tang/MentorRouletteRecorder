-- 候选验证的独立账本。观测不是正式记录，不关联 mentor_runs/run_events/run_revisions。
-- 首末时间只用于区域簇锚点；核对只改变自身摘要，并追加 candidate_reviews。
-- 30 天 / 20 000 行由仓储在写、查询及导出事务内清理；过期历史随观测级联删除。
CREATE TABLE candidate_observations (
    observation_id      TEXT NOT NULL PRIMARY KEY,
    capture_session_id  TEXT NOT NULL,
    profile_id          TEXT NOT NULL,
    hypothesis_name     TEXT NOT NULL,
    group_name          TEXT NULL,
    direction           TEXT NOT NULL CHECK (direction IN ('S2C', 'C2S', 'NONE')),
    opcode              INTEGER NULL CHECK (opcode BETWEEN 0 AND 65535),
    payload_length      INTEGER NULL CHECK (payload_length BETWEEN 0 AND 65535),
    payload_hash12      TEXT NULL CHECK (length(payload_hash12) = 12 AND payload_hash12 NOT GLOB '*[^0-9a-f]*'),
    connection_tag      TEXT NOT NULL CHECK (length(connection_tag) = 12 AND connection_tag NOT GLOB '*[^0-9a-f]*'),
    observed_at_utc     TEXT NOT NULL CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', observed_at_utc) IS NOT NULL
                                          AND strftime('%Y-%m-%dT%H:%M:%fZ', observed_at_utc) = observed_at_utc),
    t_ms                INTEGER NOT NULL CHECK (t_ms >= 0),
    first_observed_at_utc TEXT NULL,
    last_observed_at_utc  TEXT NULL,
    first_t_ms           INTEGER NULL CHECK (first_t_ms >= 0),
    last_t_ms            INTEGER NULL CHECK (last_t_ms >= 0),
    review_verdict      TEXT NULL CHECK (review_verdict IN ('CORRECT', 'WRONG', 'UNSURE')),
    review_note         TEXT NULL CHECK (length(review_note) BETWEEN 1 AND 2000),
    reviewed_at_utc     TEXT NULL CHECK (reviewed_at_utc IS NULL OR
        (strftime('%Y-%m-%dT%H:%M:%fZ', reviewed_at_utc) IS NOT NULL AND
         strftime('%Y-%m-%dT%H:%M:%fZ', reviewed_at_utc) = reviewed_at_utc)),
    CHECK ((review_verdict IS NULL AND review_note IS NULL AND reviewed_at_utc IS NULL)
        OR (review_verdict IS NOT NULL AND reviewed_at_utc IS NOT NULL)),
    CHECK ((direction = 'NONE' AND hypothesis_name = 'ZONE_LOAD' AND group_name = 'zone_load'
            AND opcode IS NULL AND payload_length IS NULL AND payload_hash12 IS NULL
            AND first_observed_at_utc IS NOT NULL AND last_observed_at_utc IS NOT NULL
            AND strftime('%Y-%m-%dT%H:%M:%fZ', first_observed_at_utc) IS NOT NULL
            AND strftime('%Y-%m-%dT%H:%M:%fZ', first_observed_at_utc) = first_observed_at_utc
            AND strftime('%Y-%m-%dT%H:%M:%fZ', last_observed_at_utc) IS NOT NULL
            AND strftime('%Y-%m-%dT%H:%M:%fZ', last_observed_at_utc) = last_observed_at_utc
            AND first_t_ms IS NOT NULL AND last_t_ms IS NOT NULL
            AND first_t_ms <= last_t_ms AND last_t_ms = t_ms)
        OR (direction IN ('S2C', 'C2S') AND opcode IS NOT NULL
            AND payload_length IS NOT NULL AND payload_hash12 IS NOT NULL
            AND first_observed_at_utc IS NULL AND last_observed_at_utc IS NULL
            AND first_t_ms IS NULL AND last_t_ms IS NULL))
);

CREATE INDEX ix_candidate_observed ON candidate_observations(observed_at_utc DESC, observation_id DESC);
CREATE INDEX ix_candidate_session_observed ON candidate_observations(capture_session_id, observed_at_utc DESC, observation_id DESC);

CREATE TABLE candidate_reviews (
    review_id           TEXT NOT NULL PRIMARY KEY,
    observation_id      TEXT NOT NULL REFERENCES candidate_observations(observation_id) ON DELETE CASCADE,
    request_id          TEXT NOT NULL UNIQUE,
    verdict             TEXT NOT NULL CHECK (verdict IN ('CORRECT', 'WRONG', 'UNSURE')),
    note                TEXT NULL CHECK (length(note) BETWEEN 1 AND 2000),
    reviewed_at_utc     TEXT NOT NULL CHECK (strftime('%Y-%m-%dT%H:%M:%fZ', reviewed_at_utc) IS NOT NULL
                                          AND strftime('%Y-%m-%dT%H:%M:%fZ', reviewed_at_utc) = reviewed_at_utc)
);

CREATE INDEX ix_candidate_reviews_observation ON candidate_reviews(observation_id, reviewed_at_utc, review_id);
-- 全局导出按时间及隐含 rowid 顺序流读，避免 temp_store=MEMORY 的全历史排序。
CREATE INDEX ix_candidate_reviews_time ON candidate_reviews(reviewed_at_utc);
CREATE TRIGGER candidate_reviews_no_update BEFORE UPDATE ON candidate_reviews
BEGIN
    SELECT RAISE(ABORT, 'candidate_reviews are append-only');
END;
