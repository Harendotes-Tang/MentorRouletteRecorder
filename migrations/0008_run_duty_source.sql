-- 记录“副本身份是怎么来的”，只增一列，不改动 0001–0007 及其校验和。
--
-- ZONE_TERRITORY 的偏移尚未在副本内负载上核实，此前 SetDuty 会把由 territory_id
-- 反查出来的 content_id 写进统计聚合列，一个错误的偏移就会污染副本统计（评审 M-5）。
-- 现在自动写入只落 territory_id 与显示用的名称/分类，并在这一列标注来源：
--   CONTENT_ID 来自报文里明确的 content_id；TERRITORY 由区域反查推断；MANUAL 由人工填写。
-- NULL 表示这条记录还没有任何副本身份，或者是 0008 之前写入的历史行。
ALTER TABLE mentor_runs ADD COLUMN duty_source TEXT NULL
    CHECK (duty_source IS NULL OR duty_source IN ('CONTENT_ID', 'TERRITORY', 'MANUAL'));
