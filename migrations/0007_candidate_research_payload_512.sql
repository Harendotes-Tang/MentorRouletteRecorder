-- 研究负载上限统一为 512 字节；0001–0006 保持不变，兼容已有校验和。
-- 只替换独立的 payload_hex 列，不重建观测父表，以保留审阅外键、rowid 和顺序。
-- MigrationRunner 将暂存、列替换和回填放在同一事务中，任一步失败均整体回滚。
CREATE TEMP TABLE candidate_payload_0007 (
    observation_id TEXT NOT NULL PRIMARY KEY,
    payload_hex TEXT NOT NULL
);

INSERT INTO temp.candidate_payload_0007 (observation_id, payload_hex)
SELECT observation_id, payload_hex
FROM candidate_observations
WHERE payload_hex IS NOT NULL;

ALTER TABLE candidate_observations DROP COLUMN payload_hex;
ALTER TABLE candidate_observations ADD COLUMN payload_hex TEXT NULL
    CHECK (payload_hex IS NULL OR (
        typeof(payload_hex) = 'text'
        AND length(payload_hex) <= 1024
        AND length(CAST(payload_hex AS BLOB)) = length(payload_hex)
        AND payload_hex NOT GLOB '*[^0-9a-f]*'
        AND payload_length IS NOT NULL
        AND payload_length BETWEEN 0 AND 512
        AND length(payload_hex) = payload_length * 2
        AND direction IN ('S2C', 'C2S')
    ));

UPDATE candidate_observations
SET payload_hex = (
    SELECT saved.payload_hex FROM temp.candidate_payload_0007 AS saved
    WHERE saved.observation_id = candidate_observations.observation_id
)
WHERE observation_id IN (SELECT observation_id FROM temp.candidate_payload_0007);

DROP TABLE temp.candidate_payload_0007;
