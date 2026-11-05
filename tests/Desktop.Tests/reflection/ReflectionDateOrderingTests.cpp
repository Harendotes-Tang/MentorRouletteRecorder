#include "ReflectionLibraryController.h"
#include "DesktopTestRunner.h"
#include <QCoreApplication>
#include <QDateTime>
#include <QTest>

namespace {
QStringList ids(const QVariantList &runs)
{
    QStringList result;
    for (const QVariant &run : runs)
        result.append(run.toMap().value(QStringLiteral("run_id")).toString());
    return result;
}
QVariantMap dayRun(const QString &date)
{
    return {{QStringLiteral("run_id"), QStringLiteral("day")},
            {QStringLiteral("import_metadata"), QVariantMap{{QStringLiteral("source_recorded_at"), date}}}};
}
QVariantMap instantRun(const QString &instant)
{
    return {{QStringLiteral("run_id"), QStringLiteral("instant")},
            {QStringLiteral("matched_at_utc"), instant}};
}

class ReflectionDateOrderingTests final : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void localDayAgainstRealInstant_data()
    {
        QTest::addColumn<int>("offset");
        QTest::addColumn<QString>("date");
        QTest::addColumn<QString>("instant");
        QTest::addColumn<bool>("ascending");
        QTest::addColumn<QStringList>("expected");
        for (const int offset : {8, -5}) {
            for (const QString &format : {QStringLiteral("2026-11-04"), QStringLiteral("2026/11/4"), QStringLiteral("2026.11.4")}) {
                for (const bool ascending : {true, false}) {
                    const QString name = QStringLiteral("%1-%2-%3").arg(offset).arg(format).arg(ascending);
                    const bool dayFirst = (offset > 0) == ascending;
                    QTest::newRow(qPrintable(name)) << offset << format
                        << (offset > 0 ? QStringLiteral("2026-11-03T20:00:00Z") : QStringLiteral("2026-11-04T02:00:00Z"))
                        << ascending << (dayFirst ? QStringList{QStringLiteral("day"), QStringLiteral("instant")}
                                                 : QStringList{QStringLiteral("instant"), QStringLiteral("day")});
                }
            }
        }
    }
    void localDayAgainstRealInstant()
    {
        QFETCH(int, offset);
        QFETCH(QString, date);
        QFETCH(QString, instant);
        QFETCH(bool, ascending);
        QFETCH(QStringList, expected);
        mr::ReflectionLibraryController library(nullptr);
        QCOMPARE(ids(library.orderedByDate({instantRun(instant), dayRun(date)}, ascending,
                     QTimeZone::fromSecondsAheadOfUtc(offset * 3600))), expected);
    }
    void productionEntryUsesSystemLocalDay()
    {
        const QDate day(2026, 11, 4);
        const auto instant = day.startOfDay(QTimeZone::systemTimeZone()).addSecs(60).toUTC();
        mr::ReflectionLibraryController library(nullptr);
        QCOMPARE(ids(library.orderedByDate({instantRun(instant.toString(Qt::ISODateWithMs)), dayRun(day.toString(Qt::ISODate))}, true)),
                 (QStringList{QStringLiteral("day"), QStringLiteral("instant")}));
    }
    void timeFieldsKeepTheirPriority()
    {
        mr::ReflectionLibraryController library(nullptr);
        QVariantMap run = dayRun(QStringLiteral("2026-11-04"));
        run[QStringLiteral("import_metadata")] = QVariantMap{
            {QStringLiteral("source_recorded_at"), QStringLiteral("2026-11-04")},
            {QStringLiteral("source_recorded_at_utc"), QStringLiteral("2026-11-03T01:00:00Z")}};
        run[QStringLiteral("ended_at_utc")] = QStringLiteral("2026-11-02T01:00:00Z");
        run[QStringLiteral("entered_at_utc")] = QStringLiteral("2026-11-01T01:00:00Z");
        run[QStringLiteral("matched_at_utc")] = QStringLiteral("2026-10-31T01:00:00Z");
        const QStringList keys{QStringLiteral("matched_at_utc"), QStringLiteral("entered_at_utc"), QStringLiteral("ended_at_utc")};
        for (int i = 0; i < 4; ++i) {
            const QString boundary = QDate(2026, 10, 31).addDays(i).toString(Qt::ISODate) + QStringLiteral("T02:00:00Z");
            QCOMPARE(ids(library.orderedByDate({instantRun(boundary), run}, true)),
                     (QStringList{QStringLiteral("day"), QStringLiteral("instant")}));
            if (i < keys.size()) run.remove(keys[i]);
        }
    }
};
}
MR_DESKTOP_QTEST_SUITE(ReflectionDateOrdering, ReflectionDateOrderingTests, QCoreApplication)
#include "ReflectionDateOrderingTests.moc"
