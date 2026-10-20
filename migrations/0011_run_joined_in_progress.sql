-- 记录这一场是否中途加入进行中的副本，只增一列，不改动 0001–0010 及其校验和。
--
-- 国服客户端进本后，副本的导演报文先宣布开始（命令 0x40000001，带时限），随后才报告剩余时间
-- （命令 0x80000004）。中途加入时玩家收不到这一场的开始宣布，先到的是剩余时间
-- （docs/protocol-profile-format.md §13）。状态机据此在进本后写下一次：
--   1 先收到剩余时间，说明副本在进入之前已经开始；
--   0 先收到开始宣布，说明玩家在场时副本才开始；
--   NULL 两者都没有收到（离开得太早、档案不比较这两种报文、0011 之前写入的历史行、
--        人工补录时没有填写）。
ALTER TABLE mentor_runs ADD COLUMN joined_in_progress INTEGER NULL
    CHECK (joined_in_progress IS NULL OR joined_in_progress IN (0, 1));
