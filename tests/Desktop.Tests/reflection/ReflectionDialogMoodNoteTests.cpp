// ReflectionDialog's mood-only notes (audit 2026-11-03 L2-3, re-reviews RV4-5
// and RV6-3).
//
// A mood chosen without text is saved with the mood's word as the note, as the
// record wizard does (the owner's rule), and that word is stored in its source
// text - 顺利 / 一般 / 糟心 - whatever the interface language. The editor shows
// such a note in the interface language and maps it back when it is saved, also
// after a live language switch.

#include "DesktopTestRunner.h"
#include "TestCollectorGuard.h"
#include "ReflectionDialogFixture.h"

#include <QByteArrayView>
#include <QCoreApplication>
#include <QDir>
#include <QGuiApplication>
#include <QQuickStyle>
#include <QScopeGuard>
#include <QStandardPaths>
#include <QTest>
#include <QTranslator>
#include <QVariantMap>

namespace {

using namespace mrtest::reflectiondialog;

/// One interface language for the three mood words only (Theme.moodLabel),
/// without the build's .qm files.
class MoodWordTranslator final : public QTranslator
{
public:
    enum class Language { English, Japanese };
    explicit MoodWordTranslator(Language language) : m_language(language) {}

    QString translate(const char *context, const char *sourceText, const char *, int) const override
    {
        if (QByteArrayView(context) != "Theme")
            return {};
        const bool english = m_language == Language::English;
        const QString source = QString::fromUtf8(sourceText);
        if (source == QString::fromUtf8("顺利"))
            return english ? QStringLiteral("Smooth") : QString::fromUtf8("順調");
        if (source == QString::fromUtf8("一般"))
            return english ? QStringLiteral("So-so") : QString::fromUtf8("ふつう");
        if (source == QString::fromUtf8("糟心"))
            return english ? QStringLiteral("Rough") : QString::fromUtf8("しんどい");
        return {};
    }
    bool isEmpty() const override { return false; }

private:
    Language m_language;
};

} // namespace

namespace {
class ReflectionDialogMoodNoteTests : public QObject
{
    Q_OBJECT

private Q_SLOTS:
    void initTestCase()
    {
        const QString qmlRoot = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        QVERIFY(QDir(qmlRoot).exists());
        qmlRegisterSingletonType(QUrl::fromLocalFile(qmlRoot + QStringLiteral("/Theme.qml")),
                                 "MentorRecorder", 1, 0, "Theme");
        for (const auto &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs")}) {
            for (const auto &file :
                 QDir(qmlRoot + directory).entryList({QStringLiteral("*.qml")}, QDir::Files)) {
                const QByteArray name = file.chopped(4).toUtf8();
                qmlRegisterType(QUrl::fromLocalFile(qmlRoot + directory + QLatin1Char('/') + file),
                                "MentorRecorder", 1, 0, name.constData());
            }
        }
    }

    /// Audit 2026-11-03 L2-3 (owner's rule, as in the record wizard): a mood
    /// chosen without text is saved with the mood word as the note. The main
    /// window's 保存笔记 sent an empty text instead - the Collector deleted
    /// nothing and the toast said 「已清空该记录的笔记」 for a run without one.
    void aMoodWithoutTextIsSavedAsItsWordInTheMainWindow()
    {
        DialogFixture fixture;
        fixture.backend.syntheticReflection = true;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-a"), QStringLiteral("水晶塔"), 3),
                                   QStringLiteral("补录笔记")));
        QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
        fixture.dialog()->setProperty("mood", QStringLiteral("bad"));
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.size(), 1);
        QCOMPARE(fixture.backend.reflections.first().value(QStringLiteral("mood")).toString(), QStringLiteral("bad"));
        QCOMPARE(fixture.backend.reflections.first().value(QStringLiteral("text")).toString(), QString::fromUtf8("糟心"));
        QVERIFY2(!fixture.controller.toastMessage().contains(QString::fromUtf8("已清空")),
                 qPrintable(fixture.controller.toastMessage()));

        // The mood the editor shows is the one saved: 顺利 needs no change to be chosen.
        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-b"), QStringLiteral("石卫塔"), 1),
                                   QStringLiteral("补录笔记")));
        QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.size(), 2);
        QCOMPARE(fixture.backend.reflections.last().value(QStringLiteral("mood")).toString(), QStringLiteral("good"));
        QCOMPARE(fixture.backend.reflections.last().value(QStringLiteral("text")).toString(), QString::fromUtf8("顺利"));
        QVERIFY(!fixture.controller.toastMessage().contains(QString::fromUtf8("已清空")));
    }

    /// The same rule in the popup beside the float: its faces start on 顺利, so
    /// only a face the player picked is a choice; 完成 with none closes as before.
    void aMoodWithoutTextIsSavedAsItsWordInThePopup()
    {
        DialogFixture fixture;
        fixture.backend.syntheticReflection = true;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        fixture.dialog()->setProperty("standalone", true);
        auto run = finishedRun(QStringLiteral("run-a"), QStringLiteral("水晶塔"), 3);
        run.insert(QStringLiteral("result"), QStringLiteral("COMPLETED"));
        run.insert(QStringLiteral("pending_review"), false);
        QVERIFY(fixture.openForRun(run, QStringLiteral("刚刚完成")));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window()));
        // What a click on the 一般 face does (a Repeater's face has no QObject parent to find).
        fixture.dialog()->setProperty("mood", QStringLiteral("ok"));
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.size(), 1);
        QCOMPARE(fixture.backend.reflections.first().value(QStringLiteral("mood")).toString(), QStringLiteral("ok"));
        QCOMPARE(fixture.backend.reflections.first().value(QStringLiteral("text")).toString(), QString::fromUtf8("一般"));

        QVERIFY(fixture.openForRun(finishedRun(QStringLiteral("run-b"), QStringLiteral("石卫塔"), 1),
                                   QStringLiteral("刚刚完成")));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window()));
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.size(), 1);
    }

    /// Re-review RV4-5: a mood-only note is stored as the mood's Chinese word, and
    /// the editor showed 「糟心」 to an English player. It shows the interface
    /// language's word, and saving it unchanged still stores the Chinese one.
    void aMoodWordNoteReadsInTheInterfaceLanguage()
    {
        MoodWordTranslator english(MoodWordTranslator::Language::English);
        QCoreApplication::installTranslator(&english);
        const auto removeTranslation = qScopeGuard([&] { QCoreApplication::removeTranslator(&english); });
        DialogFixture fixture;
        fixture.backend.syntheticReflection = true;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));

        // Written back after the note was saved, while the window stays open
        // (here: the 结果 correction after it is refused).
        QVERIFY(fixture.openForResult(finishedRun(QStringLiteral("run-a"), QStringLiteral("水晶塔"), 3)));
        QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
        fixture.dialog()->setProperty("mood", QStringLiteral("bad"));
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "resolveWith",
                                          Q_ARG(QVariant, QVariant(QStringLiteral("COMPLETED"))),
                                          Q_ARG(QVariant, QVariant(QStringLiteral("用户确认通关")))));
        QTRY_COMPARE(fixture.backend.reflections.size(), 1);
        QCOMPARE(fixture.backend.reflections.first().value(QStringLiteral("text")).toString(), QString::fromUtf8("糟心"));
        QTRY_COMPARE(fixture.note(), QStringLiteral("Rough"));
        QTRY_VERIFY(!fixture.dialog()->property("busy").toBool());
        QVERIFY(QMetaObject::invokeMethod(fixture.dialog(), "close"));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());

        // Opened on a stored mood word: shown in English, saved back unchanged.
        auto run = finishedRun(QStringLiteral("run-b"), QStringLiteral("石卫塔"), 1);
        run.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("mood"), QStringLiteral("bad")},
                                                             {QStringLiteral("text"), QString::fromUtf8("糟心")}});
        QVERIFY(fixture.openForRun(run, QStringLiteral("编辑笔记")));
        QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
        QCOMPARE(fixture.note(), QStringLiteral("Rough"));
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.size(), 2);
        QCOMPARE(fixture.backend.reflections.last().value(QStringLiteral("text")).toString(), QString::fromUtf8("糟心"));
        QCOMPARE(fixture.backend.reflections.last().value(QStringLiteral("mood")).toString(), QStringLiteral("bad"));

        // A changed mood takes its own word along, still stored in Chinese.
        QVERIFY(fixture.openForRun(run, QStringLiteral("编辑笔记")));
        QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
        fixture.dialog()->setProperty("mood", QStringLiteral("good"));
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.last().value(QStringLiteral("text")).toString(), QString::fromUtf8("顺利"));
    }

    /// Re-review RV6-3: the editor shows a stored mood word in the language it was
    /// filled in, and a live language switch does not rewrite that text. Saving it
    /// afterwards stored the old language's word ("Rough") as the note, because it
    /// no longer matched the new language's label; it stays the Chinese word.
    void aLanguageSwitchKeepsTheStoredMoodWord()
    {
        MoodWordTranslator english(MoodWordTranslator::Language::English);
        MoodWordTranslator japanese(MoodWordTranslator::Language::Japanese);
        const auto speak = [&](MoodWordTranslator &next) {
            QCoreApplication::removeTranslator(&english);
            QCoreApplication::removeTranslator(&japanese);
            QCoreApplication::installTranslator(&next);
        };
        const auto removeTranslation = qScopeGuard([&] {
            QCoreApplication::removeTranslator(&english);
            QCoreApplication::removeTranslator(&japanese);
        });
        speak(english);
        DialogFixture fixture;
        fixture.backend.syntheticReflection = true;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto run = finishedRun(QStringLiteral("run-a"), QStringLiteral("水晶塔"), 3);
        run.insert(QStringLiteral("reflection"), QVariantMap{{QStringLiteral("mood"), QStringLiteral("bad")},
                                                             {QStringLiteral("text"), QString::fromUtf8("糟心")}});

        // The main window's 保存笔记, after the switch to 日本語.
        QVERIFY(fixture.openForRun(run, QStringLiteral("编辑笔记")));
        QTRY_VERIFY(fixture.dialog()->property("opened").toBool());
        QCOMPARE(fixture.note(), QStringLiteral("Rough"));
        speak(japanese);
        QCOMPARE(fixture.note(), QStringLiteral("Rough"));
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.size(), 1);
        QCOMPARE(fixture.backend.reflections.last().value(QStringLiteral("text")).toString(), QString::fromUtf8("糟心"));
        QCOMPARE(fixture.backend.reflections.last().value(QStringLiteral("mood")).toString(), QStringLiteral("bad"));

        // The popup's 完成 with nothing changed: nothing to save, and no label stored.
        speak(english);
        fixture.dialog()->setProperty("standalone", true);
        QVERIFY(fixture.openForRun(run, QStringLiteral("刚刚完成")));
        QVERIFY(QTest::qWaitForWindowExposed(fixture.window()));
        QCOMPARE(fixture.note(), QStringLiteral("Rough"));
        speak(japanese);
        QVERIFY(fixture.click(QStringLiteral("saveReflectionButton")));
        QTRY_VERIFY(!fixture.dialog()->property("visible").toBool());
        QCOMPARE(fixture.backend.reflections.size(), 1);
    }
};
} // namespace

MR_DESKTOP_TEST_SUITE(ReflectionDialogMoodNote)
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QStandardPaths::setTestModeEnabled(true);
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ReflectionDialogMoodNoteTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "ReflectionDialogMoodNoteTests.moc"
