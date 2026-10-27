using MentorRecorder.Collector.Storage.Mutations;
using MentorRecorder.Collector.Storage.Repositories;

namespace MentorRecorder.Collector.UnitTests;

/// <summary>
/// 每个夹具独占临时数据库、时钟及变更服务；只初始化默认设置，不创建业务记录。
/// 派生夹具负责场景数据，Dispose 释放数据库；不适用于需要 Reopen 后重建服务的测试。
/// </summary>
internal abstract class RunMutationTestContext : IDisposable
{
    protected RunMutationTestContext()
    {
        Database = new TestDatabase();
        Settings = new SettingsRepository(Database.Database, Database.Clock);
        Settings.EnsureDefaults();
        Runs = new RunRepository(Database.Database);
        Revisions = new RunRevisionRepository(Database.Database);
        Service = new RunMutationService(Database.Database, Settings, Database.Clock);
    }

    public TestDatabase Database { get; }

    public SettingsRepository Settings { get; }

    public RunRepository Runs { get; }

    public RunRevisionRepository Revisions { get; }

    public RunMutationService Service { get; }

    public void Dispose() => Database.Dispose();
}
