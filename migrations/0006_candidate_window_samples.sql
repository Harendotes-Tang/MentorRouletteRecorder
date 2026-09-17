-- 排本窗口采样（docs/live-validation-guide.md §7.3）：从 QUEUE_REGISTRATION 起到进本簇为止，
-- 候选观测器把这段时间里每一种 (方向, opcode, 长度) 的报文合并成一行元数据，
-- 用 occurrences 记录出现次数；只记元数据，绝不携带负载（payload_hex 恒 NULL）。
-- 普通观测每行恰好对应一条报文，occurrences 恒为 1。
ALTER TABLE candidate_observations ADD COLUMN occurrences INTEGER NOT NULL DEFAULT 1
    CHECK (occurrences >= 1 AND occurrences <= 1000000);
