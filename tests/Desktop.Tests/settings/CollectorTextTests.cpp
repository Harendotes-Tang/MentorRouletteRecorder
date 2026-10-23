#include "DesktopTestRunner.h"
#include "CollectorText.h"
#include "CollectorTextCatalog.h"
#include "Formatters.h"
#include "IBackend.h"

#include <QCoreApplication>
#include <QDateTime>
#include <QFile>
#include <QJsonParseError>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QSet>
#include <QJsonDocument>
#include <QJsonObject>
#include <QPointer>
#include <QRegularExpression>
#include <QTest>
#include <QTranslator>

#include <memory>

// Sentences the Collector describes by key (contracts/collector-texts.md):
// Simplified Chinese renders the Collector's own wording, a translator's text
// replaces it in another language, and anything unreadable falls back to the
// Chinese the Collector sent.
namespace {

// Stands in for a compiled catalogue: every CollectorText source gets a
// recognisable "translation" that keeps the placeholders.
class MarkingTranslator : public QTranslator
{
public:
    QString translate(const char *context, const char *sourceText, const char *disambiguation,
                      int n) const override
    {
        Q_UNUSED(n)
        if (qstrcmp(context, "CollectorText") != 0 || !sourceText)
            return {};
        seen.insert(QString::fromUtf8(disambiguation));
        return QStringLiteral("[") + QString::fromUtf8(disambiguation) + QStringLiteral("] ") +
               QString::fromUtf8(sourceText);
    }
    bool isEmpty() const override { return false; }
    mutable QSet<QString> seen;
};

QVariantMap ref(const char *key, const QVariantMap &args = {})
{
    QVariantMap map{{QStringLiteral("key"), QString::fromUtf8(key)}};
    if (!args.isEmpty())
        map.insert(QStringLiteral("args"), args);
    return map;
}

// A key with arguments of each kind, found in the generated table so the test
// does not hard-code the catalogue's contents.
const mr::collector_text::CatalogueEntry *entryWith(const char *kind)
{
    for (const auto *it = mr::collector_text::catalogueBegin(); it != mr::collector_text::catalogueEnd();
         ++it) {
        for (const QString &part :
             QString::fromUtf8(it->args).split(QLatin1Char(','), Qt::SkipEmptyParts)) {
            if (part.section(QLatin1Char(':'), 1) == QLatin1String(kind))
                return it;
        }
    }
    return nullptr;
}

QVariant sampleValue(const QString &kind)
{
    if (kind == QLatin1String("number"))
        return 12;
    if (kind == QLatin1String("clock"))
        return QStringLiteral("2026-10-15T12:34:56.000Z");
    if (kind == QLatin1String("text_ref"))
        return ref(mr::collector_text::catalogueBegin()->source.key);
    return QStringLiteral("值");
}

QVariantMap sampleRef(const mr::collector_text::CatalogueEntry &entry)
{
    QVariantMap args;
    for (const QString &part :
         QString::fromUtf8(entry.args).split(QLatin1Char(','), Qt::SkipEmptyParts)) {
        const QStringList pair = part.split(QLatin1Char(':'));
        args.insert(pair.at(0), sampleValue(pair.at(1)));
    }
    return ref(entry.source.key, args);
}

} // namespace

namespace {
class CollectorTextTests : public QObject
{
    Q_OBJECT
private Q_SLOTS:
    void theTableIsNotEmptyAndKeysAreUnique()
    {
        QSet<QString> keys;
        for (const auto *it = mr::collector_text::catalogueBegin();
             it != mr::collector_text::catalogueEnd(); ++it) {
            const QString key = QString::fromUtf8(it->source.key);
            QVERIFY2(!keys.contains(key), qPrintable(key));
            keys.insert(key);
            QVERIFY(mr::collector_text::knows(key));
        }
        QVERIFY(!keys.isEmpty());
        QCOMPARE(mr::collector_text::size(), static_cast<int>(keys.size()));
    }

    void simplifiedChineseIsTheCatalogueWording()
    {
        // No translator: every key renders its source with the values put in.
        for (const auto *it = mr::collector_text::catalogueBegin();
             it != mr::collector_text::catalogueEnd(); ++it) {
            const QString rendered = mr::collector_text::render(sampleRef(*it), QStringLiteral("备用"));
            QVERIFY2(rendered != QStringLiteral("备用"), it->source.key);
            QVERIFY2(!rendered.contains(QRegularExpression(QStringLiteral("\\{[a-z_]+\\}"))),
                     qPrintable(rendered));
        }
    }

    void valuesAreFormattedByKind()
    {
        const auto *number = entryWith("number");
        if (!number)
            QSKIP("no number argument in the catalogue");
        const QString rendered = mr::collector_text::render(sampleRef(*number));
        QVERIFY2(rendered.contains(QStringLiteral("12")), qPrintable(rendered));
    }

    void aClockIsLocalHoursAndMinutes()
    {
        const auto *clock = entryWith("clock");
        if (!clock)
            QSKIP("no clock argument in the catalogue");
        const QString expected = QDateTime::fromString(QStringLiteral("2026-10-15T12:34:56.000Z"),
                                                       Qt::ISODateWithMs)
                                     .toLocalTime()
                                     .toString(QStringLiteral("HH:mm"));
        QVERIFY(mr::collector_text::render(sampleRef(*clock)).contains(expected));
    }

    void anUnreadableReferenceShowsTheChinese()
    {
        const QString chinese = QStringLiteral("采集服务的中文原句。");
        QCOMPARE(mr::collector_text::render(QVariant(), chinese), chinese);
        QCOMPARE(mr::collector_text::render(QStringLiteral("not a map"), chinese), chinese);
        QCOMPARE(mr::collector_text::render(ref("no.such_key"), chinese), chinese);

        const auto *withArgs = entryWith("number");
        if (withArgs) {
            // A missing argument, or one of the wrong type, is not guessed at.
            QCOMPARE(mr::collector_text::render(ref(withArgs->source.key), chinese), chinese);
            QVariantMap wrong = sampleRef(*withArgs);
            QVariantMap args = wrong.value(QStringLiteral("args")).toMap();
            for (auto it = args.begin(); it != args.end(); ++it)
                it.value() = QVariantMap{};
            wrong.insert(QStringLiteral("args"), args);
            QCOMPARE(mr::collector_text::render(wrong, chinese), chinese);
        }
    }

    void theTableHoldsExactlyTheCataloguesKeys()
    {
        // The generated table is checked in; a stale or hand-damaged one (a key
        // swallowed by a comment, a key left over) would silently show Chinese.
        QFile file(QStringLiteral(MR_SOURCE_DIR "/contracts/collector-texts.json"));
        QVERIFY2(file.open(QIODevice::ReadOnly), qPrintable(file.fileName()));
        QJsonParseError error;
        const QJsonDocument document = QJsonDocument::fromJson(file.readAll(), &error);
        QCOMPARE(error.error, QJsonParseError::NoError);
        const QJsonObject texts = document.object().value(QStringLiteral("texts")).toObject();
        const QStringList keys = texts.keys();
        const QSet<QString> catalogue(keys.cbegin(), keys.cend());
        QSet<QString> table;
        for (const auto *it = mr::collector_text::catalogueBegin(); it != mr::collector_text::catalogueEnd();
             ++it) {
            const QString key = QString::fromUtf8(it->source.key);
            table.insert(key);
            const QJsonValue entry = texts.value(key);
            const QString zh = entry.isString() ? entry.toString()
                                                : entry.toObject().value(QStringLiteral("zh")).toString();
            QCOMPARE(QString::fromUtf8(it->source.text), zh);
        }
        QCOMPARE(table, catalogue);
    }

    void aQmlBindingRendersAReferenceReadFromAMapProperty()
    {
        // The QML sites read the reference from a QVariantMap property of a
        // controller and hand it to Fmt.collectorText(); a JS number arrives
        // as a double.
        if (!mr::collector_text::knows(QStringLiteral("calibration.event.pop_repeated")))
            QSKIP("the key is not in the catalogue");
        const QVariantMap status{
            {QStringLiteral("hint_ref"),
             ref("calibration.event.pop_repeated",
                 {{QStringLiteral("roulette"), QStringLiteral("练级迷宫")},
                  {QStringLiteral("count"), 3}})}};
        QQmlEngine engine;
        mr::Formatters formatters;
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Status"), status);
        QQmlComponent component(&engine);
        component.setData("import QtQml\n"
                          "QtObject {\n"
                          "    property string fromMap: Fmt.collectorText(Status.hint_ref, '中文')\n"
                          "    property string fromLiteral: Fmt.collectorText({ key: 'calibration.event.pop_repeated',"
                          " args: { roulette: '练级迷宫', count: 3 } }, '中文')\n"
                          "    property string fromMissing: Fmt.collectorText(Status.no_such_ref, '中文')\n"
                          "}\n",
                          QUrl());
        MarkingTranslator translator;
        QCoreApplication::installTranslator(&translator);
        std::unique_ptr<QObject> object(component.create());
        QCoreApplication::removeTranslator(&translator);
        QVERIFY2(object, qPrintable(component.errorString()));

        const QString expected = QStringLiteral("[calibration.event.pop_repeated] 匹配弹窗：练级迷宫（检测到 3 次）");
        QCOMPARE(object->property("fromMap").toString(), expected);
        QCOMPARE(object->property("fromLiteral").toString(), expected);
        QCOMPARE(object->property("fromMissing").toString(), QStringLiteral("中文"));
    }

    void aNumberArgumentMustBeANumber()
    {
        if (!mr::collector_text::knows(QStringLiteral("calibration.event.pop_repeated")))
            QSKIP("the key is not in the catalogue");
        const auto with = [](const QVariant &count) {
            return mr::collector_text::render(
                ref("calibration.event.pop_repeated",
                    {{QStringLiteral("roulette"), QStringLiteral("练级迷宫")},
                     {QStringLiteral("count"), count}}),
                QStringLiteral("中文"));
        };
        QVERIFY(with(3).contains(QStringLiteral("3")));
        QVERIFY(with(3.0).contains(QStringLiteral("3")));
        QCOMPARE(with(QStringLiteral("3")), QStringLiteral("中文"));
        QCOMPARE(with(true), QStringLiteral("中文"));
        QCOMPARE(with(2.5), QStringLiteral("中文"));
    }

    void anImportRefusalListingEightHeaderErrorsRendersAndARunawayChainDoesNot()
    {
        if (!mr::collector_text::knows(QStringLiteral("import.table.errors_joined")) ||
            !mr::collector_text::knows(QStringLiteral("import.source.unreadable")))
            QSKIP("the import keys are not in the catalogue");
        const auto *leaf = mr::collector_text::catalogueBegin();
        const QVariantMap one = sampleRef(*leaf);
        const auto chain = [&](int errors) {
            QVariantMap joined = one;
            for (int i = 1; i < errors; ++i)
                joined = ref("import.table.errors_joined",
                             {{QStringLiteral("first"), one}, {QStringLiteral("rest"), joined}});
            return ref("import.source.unreadable", {{QStringLiteral("detail"), joined}});
        };

        // The deepest the Collector sends: eight errors, ten levels.
        const QString eight = mr::collector_text::render(chain(8), QStringLiteral("中文"));
        QVERIFY2(eight != QStringLiteral("中文"), qPrintable(eight));
        QCOMPARE(eight.count(mr::collector_text::render(one)), 8);

        // Far deeper than any Collector writes: refused as a whole.
        QCOMPARE(mr::collector_text::render(chain(40), QStringLiteral("中文")), QStringLiteral("中文"));
    }

    void aNestedReferenceThatCannotRenderFallsBackAsAWhole()
    {
        const auto *nested = entryWith("text_ref");
        if (!nested)
            QSKIP("no text_ref argument in the catalogue");
        QVariantMap outer = sampleRef(*nested);
        QVariantMap args = outer.value(QStringLiteral("args")).toMap();
        for (auto it = args.begin(); it != args.end(); ++it) {
            if (it.value().typeId() == QMetaType::QVariantMap)
                it.value() = ref("no.such_key");
        }
        outer.insert(QStringLiteral("args"), args);
        QCOMPARE(mr::collector_text::render(outer, QStringLiteral("中文")), QStringLiteral("中文"));
    }

    void aTranslatorReplacesTheWordingAndAValueIsNotExpandedTwice()
    {
        MarkingTranslator translator;
        QCoreApplication::installTranslator(&translator);
        const char *key = mr::collector_text::catalogueBegin()->source.key;
        const QString rendered = mr::collector_text::render(
            sampleRef(*mr::collector_text::catalogueBegin()), QStringLiteral("中文"));
        QCoreApplication::removeTranslator(&translator);

        QVERIFY2(rendered.startsWith(QStringLiteral("[") + QString::fromUtf8(key) +
                                     QStringLiteral("] ")),
                 qPrintable(rendered));
        QVERIFY(translator.seen.contains(QString::fromUtf8(key)));

        // A text value that looks like a placeholder stays as written.
        const auto *text = entryWith("text");
        if (!text)
            return;
        QVariantMap sample = sampleRef(*text);
        QVariantMap args = sample.value(QStringLiteral("args")).toMap();
        for (auto it = args.begin(); it != args.end(); ++it) {
            if (it.value().typeId() == QMetaType::QString)
                it.value() = QStringLiteral("{row}");
        }
        sample.insert(QStringLiteral("args"), args);
        QVERIFY(mr::collector_text::render(sample).contains(QStringLiteral("{row}")));
    }

    void aLiveTextFollowsTheLanguage()
    {
        const auto *first = mr::collector_text::catalogueBegin();
        const mr::LiveText live = mr::collector_text::live(sampleRef(*first), QStringLiteral("中文"));
        const QString before = live.text();
        MarkingTranslator translator;
        QCoreApplication::installTranslator(&translator);
        const QString after = live.text();
        QCoreApplication::removeTranslator(&translator);
        QVERIFY(before != after);
        QVERIFY(after.startsWith(QStringLiteral("[")));

        // Without a reference it is the Collector's text, unchanged.
        QCOMPARE(mr::collector_text::live(QVariant(), QStringLiteral("中文")).text(),
                 QStringLiteral("中文"));
        QVERIFY(mr::collector_text::live(QVariant(), QString()).isEmpty());
    }

    void aListIsRenderedEntryByEntryOrNotAtAll()
    {
        const auto *first = mr::collector_text::catalogueBegin();
        const QString one = mr::collector_text::render(sampleRef(*first));
        const QStringList chinese{QStringLiteral("甲"), QStringLiteral("乙")};

        QCOMPARE(mr::collector_text::renderList(QVariantList{sampleRef(*first), QVariant()}, chinese),
                 (QStringList{one, QStringLiteral("乙")}));
        // A refs list of another length is ignored as a whole.
        QCOMPARE(mr::collector_text::renderList(QVariantList{sampleRef(*first)}, chinese), chinese);
        QCOMPARE(mr::collector_text::renderList(QVariant(), chinese), chinese);
    }

    void aFailedReplyCarriesItsReferenceAndRendersIt()
    {
        const auto *first = mr::collector_text::catalogueBegin();
        const QJsonObject messageRef = QJsonObject::fromVariantMap(sampleRef(*first));
        auto *reply = new mr::BackendReply(QStringLiteral("id"), QStringLiteral("GetStatus"));
        QPointer<mr::BackendReply> guard(reply);
        QString handled;
        QVariant handledRef;
        reply->whenDone(nullptr, [&](bool ok, const QVariantMap &, const QString &,
                                      const QString &message) {
            QVERIFY(!ok);
            handled = message;
            handledRef = guard ? guard->errorRef() : QVariant();
        });

        MarkingTranslator translator;
        QCoreApplication::installTranslator(&translator);
        reply->fail(QStringLiteral("ERR_BAD_REQUEST"), QStringLiteral("中文"), {}, messageRef);
        QCoreApplication::removeTranslator(&translator);

        QVERIFY2(handled.startsWith(QStringLiteral("[")), qPrintable(handled));
        QCOMPARE(handledRef.toMap().value(QStringLiteral("key")).toString(),
                 QString::fromUtf8(first->source.key));

        // Without a reference the Collector's message passes through untouched.
        auto *plain = new mr::BackendReply(QStringLiteral("id2"), QStringLiteral("GetStatus"));
        QString plainMessage;
        plain->whenDone(nullptr, [&](bool, const QVariantMap &, const QString &,
                                      const QString &message) { plainMessage = message; });
        plain->fail(QStringLiteral("ERR_INTERNAL"), QStringLiteral("原样"));
        QCOMPARE(plainMessage, QStringLiteral("原样"));
    }
};
} // namespace

MR_DESKTOP_QTEST_SUITE(CollectorText, CollectorTextTests, QCoreApplication)
#include "CollectorTextTests.moc"
