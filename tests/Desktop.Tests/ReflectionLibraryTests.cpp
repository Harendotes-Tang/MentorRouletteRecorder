#include "IBackend.h"
#include "ReflectionLibraryController.h"
#include "RunListModel.h"

#include <QGuiApplication>
#include <QJsonArray>
#include <QSignalSpy>
#include <QTest>
#include <QTimer>

namespace {
class QueryBackend final : public mr::IBackend
{
public:
    QString backendName() const override { return QStringLiteral("test"); }
    bool isConnected() const override { return true; }
    QList<QJsonObject> queries;
    bool reject = false;
    mr::BackendReply *request(const QString &type, const QJsonObject &payload) override {
        queries.append(payload);
        auto *reply = new mr::BackendReply(QStringLiteral("test"), type, this);
        const bool rejected = reject;
        const int page = payload.value(QStringLiteral("page")).toInt(1);
        QTimer::singleShot(0, reply, [reply, rejected, page] {
            if (rejected) {
                reply->fail(QStringLiteral("ERR_QUERY"), QStringLiteral("数据库暂不可读"));
                return;
            }
            QJsonArray items;
            items.append(QJsonObject{{QStringLiteral("run_id"), QStringLiteral("page-%1").arg(page)},
                {QStringLiteral("reflection"), QJsonObject{{QStringLiteral("text"), QStringLiteral("完整心得")}}}});
            reply->succeed({{QStringLiteral("items"), items}, {QStringLiteral("page_info"),
                QJsonObject{{QStringLiteral("page"), page}, {QStringLiteral("total"), 31}}}});
        });
        return reply;
    }
};
}

class ReflectionLibraryTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void independentFiltersAndPaging() {
        QueryBackend backend;
        mr::RunListModel history;
        history.setBackend(&backend);
        history.setFilter({{QStringLiteral("text"), QStringLiteral("历史筛选")}});
        QTRY_VERIFY(!history.isLoading());
        mr::ReflectionLibraryController library(&backend);
        QCOMPARE(backend.queries.size(), 1);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        QVERIFY(backend.queries.last().value(QStringLiteral("filter")).toObject().value(QStringLiteral("with_reflection")).toBool());
        library.runs()->nextPage();
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->page(), 2);
        QCOMPARE(history.page(), 1);
        QCOMPARE(history.runAt(0).value(QStringLiteral("run_id")).toString(), QStringLiteral("page-1"));
        library.setFilter({{QStringLiteral("text"), QStringLiteral("  心得副本  ")},
            {QStringLiteral("include_deleted"), true}, {QStringLiteral("with_reflection"), false}});
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->page(), 1);
        const auto filter = backend.queries.last().value(QStringLiteral("filter")).toObject();
        QCOMPARE(filter.value(QStringLiteral("text")).toString(), QStringLiteral("心得副本"));
        QVERIFY(filter.value(QStringLiteral("with_reflection")).toBool());
        QVERIFY(!filter.contains(QStringLiteral("include_deleted")));
        history.reload();
        QTRY_VERIFY(!history.isLoading());
        QCOMPARE(backend.queries.last().value(QStringLiteral("filter")).toObject().value(QStringLiteral("text")).toString(), QStringLiteral("历史筛选"));
    }
    void failureIsNotAnEmptyResultAndRetryWorks() {
        QueryBackend backend;
        backend.reject = true;
        mr::ReflectionLibraryController library(&backend);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->rowCount(), 0);
        QVERIFY(!library.runs()->loadError().isEmpty());
        backend.reject = false;
        library.reload();
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(library.runs()->total(), 31);
        QVERIFY(library.runs()->loadError().isEmpty());
    }
    void inactiveRefreshAndBackendLifetime() {
        auto *backend = new QueryBackend;
        mr::ReflectionLibraryController library(backend);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        library.runs()->nextPage();
        QTRY_VERIFY(!library.runs()->isLoading());
        library.setActive(false);
        const int before = backend->queries.size();
        Q_EMIT backend->liveEvent({{QStringLiteral("kind"), QStringLiteral("run_updated")}});
        QCOMPARE(backend->queries.size(), before);
        library.setActive(true);
        QTRY_VERIFY(!library.runs()->isLoading());
        QCOMPARE(backend->queries.size(), before + 1);
        QCOMPARE(library.runs()->page(), 2);
        delete backend;
        library.reload();
        library.runs()->reload();
        QVERIFY(!library.runs()->isLoading());
    }
};

int main(int argc, char **argv) {
    QGuiApplication app(argc, argv);
    ReflectionLibraryTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ReflectionLibraryTests.moc"
