-- docs/privacy-boundary.md §5.1 的显式研究例外，仅作用于候选账本。
-- 默认 NULL；原始负载最多 256 字节，只允许规范小写十六进制且长度与观测一致。
-- ZONE_LOAD 等推断锚点不能携带原始负载。开关与白名单由仓储在写事务内检查。
ALTER TABLE candidate_observations ADD COLUMN payload_hex TEXT NULL
    CHECK (payload_hex IS NULL OR (
        typeof(payload_hex) = 'text'
        AND length(payload_hex) <= 512
        AND length(CAST(payload_hex AS BLOB)) = length(payload_hex)
        AND payload_hex NOT GLOB '*[^0-9a-f]*'
        AND payload_length IS NOT NULL
        AND payload_length BETWEEN 0 AND 256
        AND length(payload_hex) = payload_length * 2
        AND direction IN ('S2C', 'C2S')
    ));
