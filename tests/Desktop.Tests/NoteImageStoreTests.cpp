// ---------------------------------------------------------------------------
// tst_noteimagestore - 备注图片.
//
// The store itself (where the root lands, what a run id may look like, what a
// file must be to get in, the copy / list / remove round trip, and that a
// removal can never reach outside the run's folder), then the wizard on top of
// it: images are staged in the dialog, applied only on 保存, and an image-only
// save neither asks the Collector for anything nor writes a revision.
// ---------------------------------------------------------------------------

#include "TestCollectorGuard.h"
#include "AppController.h"
#include "Formatters.h"
#include "JobCatalog.h"
#include "MockBackend.h"
#include "NoteImageStore.h"
#include "RoleCatalog.h"
#include "RunFormValidator.h"

#include <QColor>
#include <QBuffer>
#include <QClipboard>
#include <QDataStream>
#include <QDir>
#include <QFile>
#include <QFileInfo>
#include <QGuiApplication>
#include <QImage>
#include <QJSValue>
#include <QMimeData>
#include <QProcess>
#include <QQmlComponent>
#include <QQmlContext>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QScopeGuard>
#include <QSignalSpy>
#include <QTemporaryDir>
#include <QTest>
#include <QVariantList>
#include <QVariantMap>

#include <memory>

namespace {

const QString kRunId = QStringLiteral("0d3f6c2a-9b1e-4c7d-8a5f-1234567890ab");

/// A real, decodable image at \a path; the format follows the suffix.
bool writeImage(const QString &path, const QColor &color = Qt::red)
{
    QImage image(24, 16, QImage::Format_RGB32);
    image.fill(color);
    return image.save(path);
}

QVariantList asList(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toList()
                                        : value.toList();
}

QVariantMap asMap(const QVariant &value)
{
    return value.canConvert<QJSValue>() ? value.value<QJSValue>().toVariant().toMap()
                                        : value.toMap();
}

QStringList names(const QVariantList &rows)
{
    QStringList out;
    for (const QVariant &row : rows)
        out.append(row.toMap().value(QStringLiteral("name")).toString());
    return out;
}

/// One temp root for the store and one for the player's "pictures".
struct StoreFixture {
    QTemporaryDir root;
    QTemporaryDir pictures;
    mr::NoteImageStore store{root.path()};

    QString picture(const QString &name, const QColor &color = Qt::red)
    {
        const QString path = QDir(pictures.path()).absoluteFilePath(name);
        if (!writeImage(path, color))
            return {};
        return path;
    }

    QString runDir() const { return store.runDirectory(kRunId); }
};

/// The wizard on a mock backend, with NoteImages pointed at a temp root.
struct DialogFixture {
    mr::MockBackend backend;
    mr::AppController controller{&backend, nullptr};
    mr::Formatters formatters;
    mr::JobCatalog jobs;
    mr::RoleCatalog roles;
    mr::RunFormValidator validator;
    StoreFixture files;
    QQmlEngine engine;
    std::unique_ptr<QObject> root;
    QString errors;

    bool create()
    {
        engine.rootContext()->setContextProperty(QStringLiteral("App"), &controller);
        engine.rootContext()->setContextProperty(QStringLiteral("Fmt"), &formatters);
        engine.rootContext()->setContextProperty(QStringLiteral("Jobs"), &jobs);
        engine.rootContext()->setContextProperty(QStringLiteral("Roles"), &roles);
        engine.rootContext()->setContextProperty(QStringLiteral("RunForm"), &validator);
        engine.rootContext()->setContextProperty(QStringLiteral("NoteImages"), &files.store);
        engine.rootContext()->setContextProperty(QStringLiteral("ReduceMotion"), true);
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 900; height: 800; visible: true
    EditRunDialog { objectName: "edit" }
})",
                          QUrl());
        root.reset(component.create());
        for (const auto &error : component.errors())
            errors += error.toString() + QLatin1Char('\n');
        if (!root)
            return false;
        auto *edit = dialog();
        edit->setProperty("dutyOptions",
                          QVariantList{QVariantMap{{QStringLiteral("content_id"), 70},
                                                   {QStringLiteral("duty_name"),
                                                    QString::fromUtf8("伊库拉尔堡垒")}}});
        edit->setProperty("jobOptions",
                          QVariantList{QVariantMap{{QStringLiteral("job_id"), 19},
                                                   {QStringLiteral("job_name"),
                                                    QString::fromUtf8("骑士")}}});
        return true;
    }

    QObject *dialog() const { return root->findChild<QObject *>(QStringLiteral("edit")); }

    static QVariantMap run()
    {
        return {
            {QStringLiteral("run_id"), kRunId},
            {QStringLiteral("revision"), 1},
            {QStringLiteral("matched_at_utc"), QStringLiteral("2026-09-04T12:39:05.125Z")},
            {QStringLiteral("entered_at_utc"), QStringLiteral("2026-09-04T12:41:00.000Z")},
            {QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T12:59:40.422Z")},
            // Every key collectFields() emits, so an untouched form is no correction.
            {QStringLiteral("content_id"), 70},
            {QStringLiteral("territory_id"), QVariant::fromValue(nullptr)},
            {QStringLiteral("duty_name"), QString::fromUtf8("伊库拉尔堡垒")},
            {QStringLiteral("job_id"), 19},
            {QStringLiteral("job_name"), QString::fromUtf8("骑士")},
            {QStringLiteral("role"), QStringLiteral("UNKNOWN")},
            {QStringLiteral("result"), QStringLiteral("COMPLETED")},
            {QStringLiteral("contributes_to_goal"), true},
            {QStringLiteral("note"), QString()},
        };
    }

    bool openForRun()
    {
        return QMetaObject::invokeMethod(dialog(), "openForRun",
                                         Q_ARG(QVariant, QVariant(run())));
    }

    bool stage(const QString &path)
    {
        QVariant staged;
        if (!QMetaObject::invokeMethod(dialog(), "stageNoteImage", Q_RETURN_ARG(QVariant, staged),
                                       Q_ARG(QVariant, QVariant(path))))
            return false;
        return staged.toBool();
    }

    bool submit() { return QMetaObject::invokeMethod(dialog(), "submit"); }

    QVariantList rows() const { return asList(dialog()->property("noteImageRows")); }

    /// The item the wizard puts on step 3; Repeater delegates are not children
    /// of the dialog, so the lookup walks the popup's visual tree.
    QQuickItem *item(const QString &name) const
    {
        const auto *content = dialog()->property("contentItem").value<QQuickItem *>();
        QList<QQuickItem *> pending;
        if (content)
            pending.append(const_cast<QQuickItem *>(content));
        while (!pending.isEmpty()) {
            QQuickItem *current = pending.takeFirst();
            if (current->objectName() == name)
                return current;
            pending.append(current->childItems());
        }
        return dialog()->findChild<QQuickItem *>(name);
    }
};

} // namespace

class NoteImageStoreTests : public QObject
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

    // ------------------------------------------------------------ 位置 --

    void rootIsBesideTheExecutableUnlessOverridden()
    {
        const QString appDir = QStringLiteral("D:/MentorRecorder");
        QCOMPARE(mr::NoteImageStore::resolveRoot(QString(), appDir),
                 QStringLiteral("D:/MentorRecorder/note-images"));
        QCOMPARE(mr::NoteImageStore::resolveRoot(QStringLiteral("   "), appDir),
                 QStringLiteral("D:/MentorRecorder/note-images"));
        // An override wins, cleaned and absolute, whatever slashes it came with.
        QCOMPARE(mr::NoteImageStore::resolveRoot(QStringLiteral(" E:\\pics\\..\\notes\\ "), appDir),
                 QStringLiteral("E:/notes"));
        // The production constructor honours the same rule through the environment.
        qputenv(mr::NoteImageStore::kRootVariable, QByteArrayLiteral("E:/elsewhere"));
        mr::NoteImageStore moved;
        QCOMPARE(moved.rootDirectory(), QStringLiteral("E:/elsewhere"));
        QCOMPARE(moved.rootDirectoryNative(), QStringLiteral("E:\\elsewhere"));
        qunsetenv(mr::NoteImageStore::kRootVariable);
        mr::NoteImageStore beside;
        QVERIFY(beside.rootDirectory().endsWith(QStringLiteral("/note-images")));
    }

    void runIdNamesAFolderSoOnlyAUuidIsAccepted()
    {
        StoreFixture fixture;
        QVERIFY(mr::NoteImageStore::isValidRunId(kRunId));
        QVERIFY(mr::NoteImageStore::isValidRunId(kRunId.toUpper()));
        for (const QString &bad : {QString(), QStringLiteral("run-a"), QStringLiteral("../x"),
                                   QStringLiteral("0d3f6c2a-9b1e-4c7d-8a5f-1234567890ab/.."),
                                   QStringLiteral("0d3f6c2a9b1e4c7d8a5f1234567890ab")}) {
            QVERIFY2(!mr::NoteImageStore::isValidRunId(bad), qPrintable(bad));
            QVERIFY(fixture.store.runDirectory(bad).isEmpty());
            QVERIFY(fixture.store.imagesFor(bad).isEmpty());
            const QVariantMap result =
                fixture.store.commit(bad, {fixture.picture(QStringLiteral("a.png"))}, {});
            QVERIFY(!result.value(QStringLiteral("ok")).toBool());
            QVERIFY(!result.value(QStringLiteral("error")).toString().isEmpty());
        }
        // Nothing was created for any of them.
        QVERIFY(QDir(fixture.root.path())
                    .entryList(QDir::AllEntries | QDir::NoDotAndDotDot)
                    .isEmpty());
        // The folder name is the canonical lower-case form.
        QCOMPARE(fixture.store.runDirectory(kRunId.toUpper()),
                 QDir::cleanPath(fixture.root.path()) + QLatin1Char('/') + kRunId);
    }

    // ------------------------------------------------------------ 内容 --

    void onlyDecodableImagesOfASupportedFormatGetIn()
    {
        StoreFixture fixture;

        // The suffix says PNG; the bytes do not.
        const QString fake = QDir(fixture.pictures.path()).absoluteFilePath(QStringLiteral("fake.png"));
        {
            QFile file(fake);
            QVERIFY(file.open(QIODevice::WriteOnly));
            file.write("this is not an image");
        }
        QVariantMap look = fixture.store.inspect(fake);
        QVERIFY(!look.value(QStringLiteral("ok")).toBool());
        QVERIFY(look.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("图片格式")));

        // Missing, empty, oversized.
        look = fixture.store.inspect(QStringLiteral("Z:/nowhere/none.png"));
        QVERIFY(!look.value(QStringLiteral("ok")).toBool());
        QVERIFY(look.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("找不到")));

        const QString empty = QDir(fixture.pictures.path()).absoluteFilePath(QStringLiteral("empty.png"));
        {
            QFile file(empty);
            QVERIFY(file.open(QIODevice::WriteOnly));
        }
        look = fixture.store.inspect(empty);
        QVERIFY(!look.value(QStringLiteral("ok")).toBool());
        QVERIFY(look.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("空文件")));

        const QString huge = QDir(fixture.pictures.path()).absoluteFilePath(QStringLiteral("huge.png"));
        {
            QFile file(huge);
            QVERIFY(file.open(QIODevice::WriteOnly));
            QVERIFY(file.resize(mr::NoteImageStore::kMaxImageBytes + 1));
        }
        look = fixture.store.inspect(huge);
        QVERIFY(!look.value(QStringLiteral("ok")).toBool());
        QVERIFY(look.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("超过")));

        // A real one, whatever the suffix claims, is described by its bytes.
        const QString real = fixture.picture(QStringLiteral("shot.jpeg"));
        QVERIFY(!real.isEmpty());
        look = fixture.store.inspect(real);
        QVERIFY2(look.value(QStringLiteral("ok")).toBool(),
                 qPrintable(look.value(QStringLiteral("error")).toString()));
        QCOMPARE(look.value(QStringLiteral("format")).toString(), QStringLiteral("jpg"));
        QCOMPARE(look.value(QStringLiteral("name")).toString(), QStringLiteral("shot.jpeg"));
        QVERIFY(look.value(QStringLiteral("url")).toString().startsWith(QStringLiteral("file:")));
        QVERIFY(look.value(QStringLiteral("byte_count")).toLongLong() > 0);

        // commit checks every file before copying any: one bad file, no folder.
        const QVariantMap result = fixture.store.commit(kRunId, {real, fake}, {});
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(!QDir(fixture.runDir()).exists());
        QVERIFY(fixture.store.imagesFor(kRunId).isEmpty());
    }

    void truncatedImageDoesNotCommitAnyChanges()
    {
        StoreFixture fixture;
        const QString existing = fixture.picture(QStringLiteral("existing.png"));
        QVERIFY(fixture.store.addFile(kRunId, existing).value(QStringLiteral("ok")).toBool());
        const QString stored = fixture.store.imagesFor(kRunId).first().toMap()
                                   .value(QStringLiteral("path")).toString();
        const QString damaged = fixture.picture(QStringLiteral("truncated.png"));
        QFile file(damaged);
        QVERIFY(file.open(QIODevice::ReadWrite));
        QVERIFY(file.resize(45)); // Valid PNG signature and IHDR, incomplete pixel data.
        file.close();
        QVERIFY(QImage(damaged).isNull());
        QVERIFY(!fixture.store.inspect(damaged).value(QStringLiteral("ok")).toBool());
        const QVariantMap result = fixture.store.commit(kRunId, {existing, damaged}, {stored});
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(QFileInfo::exists(stored));
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), 1);
        QVERIFY(result.value(QStringLiteral("added")).toStringList().isEmpty());
    }

    // ------------------------------------------------------------ 往返 --

    void commitCopiesListsAndRemovesInsideTheRunFolder()
    {
        StoreFixture fixture;
        QSignalSpy changed(&fixture.store, &mr::NoteImageStore::imagesChanged);
        const QString first = fixture.picture(QStringLiteral("one.png"), Qt::red);
        const QString second = fixture.picture(QStringLiteral("two.jpeg"), Qt::blue);
        QVERIFY(!first.isEmpty() && !second.isEmpty());

        QVariantMap result = fixture.store.commit(kRunId, {first, second}, {});
        QVERIFY2(result.value(QStringLiteral("ok")).toBool(),
                 qPrintable(result.value(QStringLiteral("error")).toString()));
        QCOMPARE(result.value(QStringLiteral("added")).toStringList(), QStringList({first, second}));
        QCOMPARE(changed.count(), 1);
        QCOMPARE(changed.first().first().toString(), kRunId);

        // Copied, not moved; stored by the real format; in order; readable back.
        QVERIFY(QFileInfo::exists(first));
        QVariantList rows = fixture.store.imagesFor(kRunId);
        QCOMPARE(rows.size(), 2);
        const QStringList stored = names(rows);
        QVERIFY2(stored.at(0).endsWith(QStringLiteral("-01.png")), qPrintable(stored.at(0)));
        QVERIFY2(stored.at(1).endsWith(QStringLiteral("-02.jpg")), qPrintable(stored.at(1)));
        for (const QVariant &row : rows) {
            const QVariantMap map = row.toMap();
            const QString path = map.value(QStringLiteral("path")).toString();
            QVERIFY2(path.startsWith(fixture.runDir() + QLatin1Char('/')), qPrintable(path));
            QVERIFY(!QImage(path).isNull());
            QCOMPARE(map.value(QStringLiteral("url")).toString(), QUrl::fromLocalFile(path).toString());
            QVERIFY(map.value(QStringLiteral("byte_count")).toLongLong() > 0);
        }
        QCOMPARE(QImage(rows.first().toMap().value(QStringLiteral("path")).toString()).pixelColor(0, 0),
                 QColor(Qt::red));

        // A second commit a moment later sorts after the first.
        QTest::qWait(5);
        const QString third = fixture.picture(QStringLiteral("three.bmp"), Qt::green);
        result = fixture.store.commit(kRunId, {third}, {});
        QVERIFY(result.value(QStringLiteral("ok")).toBool());
        rows = fixture.store.imagesFor(kRunId);
        QCOMPARE(rows.size(), 3);
        QVERIFY(names(rows).at(2).endsWith(QStringLiteral(".bmp")));

        // Removal: a path outside the run folder is refused and left alone; the
        // others go; an already-missing one counts as removed.
        const QString outside = fixture.picture(QStringLiteral("outside.png"));
        const QString keep = rows.at(2).toMap().value(QStringLiteral("path")).toString();
        const QString goneOne = rows.at(0).toMap().value(QStringLiteral("path")).toString();
        const QString goneTwo = rows.at(1).toMap().value(QStringLiteral("path")).toString();
        result = fixture.store.commit(
            kRunId, {},
            {outside, goneOne, fixture.runDir() + QStringLiteral("/../escape.png"), fixture.runDir()});
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QCOMPARE(result.value(QStringLiteral("removed")).toStringList(), QStringList({goneOne}));
        QVERIFY(QFileInfo::exists(outside));
        QVERIFY(!QFileInfo::exists(goneOne));
        QVERIFY(QFileInfo::exists(keep));
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), 2);

        result = fixture.store.commit(kRunId, {}, {goneTwo, goneOne, keep});
        QVERIFY2(result.value(QStringLiteral("ok")).toBool(),
                 qPrintable(result.value(QStringLiteral("error")).toString()));
        QCOMPARE(result.value(QStringLiteral("removed")).toStringList().size(), 3);
        QVERIFY(fixture.store.imagesFor(kRunId).isEmpty());
        // The empty run folder is gone with its last image.
        QVERIFY(!QDir(fixture.runDir()).exists());
        QCOMPARE(changed.count(), 4);

        // Nothing to do is not a change.
        result = fixture.store.commit(kRunId, {}, {});
        QVERIFY(result.value(QStringLiteral("ok")).toBool());
        QCOMPARE(changed.count(), 4);
    }

    void clipboardImagesUseTheExistingCopyAndDecodeBoundary_data()
    {
        QTest::addColumn<bool>("encoded");
        QTest::newRow("native-screenshot") << false;
        QTest::newRow("encoded-png") << true;
    }

    void clipboardImagesUseTheExistingCopyAndDecodeBoundary()
    {
        QFETCH(bool, encoded);
        StoreFixture fixture;
        QSignalSpy changed(&fixture.store, &mr::NoteImageStore::imagesChanged);
        QSignalSpy clipboardChanged(&fixture.store, &mr::NoteImageStore::clipboardChanged);
        QImage image(32, 24, QImage::Format_ARGB32);
        image.fill(QColor(12, 34, 56, 160));
        if (encoded) {
            QByteArray bytes;
            QBuffer buffer(&bytes);
            QVERIFY(buffer.open(QIODevice::WriteOnly));
            QVERIFY(image.save(&buffer, "PNG"));
            auto *mime = new QMimeData;
            mime->setData(QStringLiteral("image/png"), bytes);
            QGuiApplication::clipboard()->setMimeData(mime);
        } else {
            QGuiApplication::clipboard()->setImage(image);
        }
        QVERIFY(fixture.store.clipboardHasImage());
        QVERIFY(clipboardChanged.count() > 0);
        const QVariantMap result = fixture.store.addClipboard(kRunId);
        QVERIFY2(result.value(QStringLiteral("ok")).toBool(), qPrintable(result.value(QStringLiteral("error")).toString()));
        QCOMPARE(changed.count(), 1);
        const auto rows = fixture.store.imagesFor(kRunId);
        QCOMPARE(rows.size(), 1);
        const QImage stored(rows.first().toMap().value(QStringLiteral("path")).toString());
        QCOMPARE(stored.size(), image.size());
        QCOMPARE(stored.pixelColor(0, 0), image.pixelColor(0, 0));
        // The run owns only the committed copy; the bounded conversion file is gone.
        for (const QString &source : result.value(QStringLiteral("added")).toStringList())
            QVERIFY(!QFileInfo::exists(source));
    }

    void emptyTextCorruptOversizedAndBombClipboardsNeverWrite()
    {
        StoreFixture fixture;
        auto *clipboard = QGuiApplication::clipboard();
        clipboard->clear();
        QVERIFY(!fixture.store.clipboardHasImage());
        QVERIFY(!fixture.store.addClipboard(kRunId).value(QStringLiteral("ok")).toBool());
        clipboard->setText(QStringLiteral("ordinary reflection text"));
        QVERIFY(!fixture.store.clipboardHasImage());
        QVERIFY(!fixture.store.addClipboard(kRunId).value(QStringLiteral("ok")).toBool());
        const auto setEncoded = [&](const QString &format, const QByteArray &bytes) {
            auto *mime = new QMimeData;
            mime->setData(format, bytes);
            clipboard->setMimeData(mime);
        };
        setEncoded(QStringLiteral("image/png"), QByteArrayLiteral("truncated png"));
        QVERIFY(fixture.store.clipboardHasImage());
        QVERIFY(!fixture.store.addClipboard(kRunId).value(QStringLiteral("ok")).toBool());
        setEncoded(QStringLiteral("image/png"), QByteArray(mr::NoteImageStore::kMaxImageBytes + 1, '\0'));
        auto result = fixture.store.addClipboard(kRunId);
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(result.value(QStringLiteral("error")).toString().contains(QStringLiteral("超过")));
        QByteArray bomb;
        QDataStream out(&bomb, QIODevice::WriteOnly);
        out.setByteOrder(QDataStream::LittleEndian);
        out.writeRawData("BM", 2);
        out << quint32(54) << quint32(0) << quint32(54);
        out << quint32(40) << qint32(10000) << qint32(10000);
        out << quint16(1) << quint16(24) << quint32(0) << quint32(0);
        out << qint32(2835) << qint32(2835) << quint32(0) << quint32(0);
        setEncoded(QStringLiteral("image/bmp"), bomb);
        result = fixture.store.addClipboard(kRunId);
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(result.value(QStringLiteral("error")).toString().contains(QStringLiteral("分辨率")));
        QVERIFY(fixture.store.imagesFor(kRunId).isEmpty());
        QVERIFY(!QDir(fixture.runDir()).exists());
        QVERIFY(!fixture.store.addClipboard(QStringLiteral("../outside")).value(QStringLiteral("ok")).toBool());
        clipboard->clear();
    }

    void clipboardCopyFailureCanRetryWithoutLeavingCopiesOrTemporaryFiles()
    {
        StoreFixture fixture;
        QImage image(24, 16, QImage::Format_RGB32);
        image.fill(Qt::cyan);
        QGuiApplication::clipboard()->setImage(image);
        QString temporarySource;
        fixture.store.setCopyFunctionForTesting([&](const QString &source, const QString &) {
            temporarySource = source;
            return false;
        });
        const auto failed = fixture.store.addClipboard(kRunId);
        QVERIFY(!failed.value(QStringLiteral("ok")).toBool());
        QVERIFY(!temporarySource.isEmpty());
        QVERIFY(!QFileInfo::exists(temporarySource));
        QVERIFY(fixture.store.imagesFor(kRunId).isEmpty());
        fixture.store.setCopyFunctionForTesting({});
        QVERIFY(fixture.store.addClipboard(kRunId).value(QStringLiteral("ok")).toBool());
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), 1);
        QGuiApplication::clipboard()->clear();
    }

    void clipboardAddsRespectTheSamePerRunImageLimit()
    {
        StoreFixture fixture;
        const QString picture = fixture.picture(QStringLiteral("existing.png"));
        QStringList sources;
        for (int i = 0; i < mr::NoteImageStore::kMaxImagesPerRun; ++i)
            sources.append(picture);
        QVERIFY(fixture.store.commit(kRunId, sources, {}).value(QStringLiteral("ok")).toBool());
        QImage image(24, 16, QImage::Format_RGB32);
        image.fill(Qt::yellow);
        QGuiApplication::clipboard()->setImage(image);
        const auto result = fixture.store.addClipboard(kRunId);
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(result.value(QStringLiteral("error")).toString().contains(QString::number(mr::NoteImageStore::kMaxImagesPerRun)));
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), mr::NoteImageStore::kMaxImagesPerRun);
        QGuiApplication::clipboard()->clear();
    }

    void oneStepAddAndRemoveAreCommitsOfOne()
    {
        StoreFixture fixture;
        QSignalSpy changed(&fixture.store, &mr::NoteImageStore::imagesChanged);
        const QString shot = fixture.picture(QStringLiteral("shot.png"));

        QVariantMap result = fixture.store.addFile(kRunId, shot);
        QVERIFY2(result.value(QStringLiteral("ok")).toBool(),
                 qPrintable(result.value(QStringLiteral("error")).toString()));
        QCOMPARE(result.value(QStringLiteral("added")).toStringList(), QStringList{shot});
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), 1);
        QCOMPARE(changed.count(), 1);

        result = fixture.store.addFile(kRunId, QStringLiteral("Z:/nowhere/none.png"));
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), 1);

        const QString stored =
            fixture.store.imagesFor(kRunId).first().toMap().value(QStringLiteral("path")).toString();
        result = fixture.store.removeOne(kRunId, fixture.pictures.path() + QStringLiteral("/shot.png"));
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(QFileInfo::exists(shot));
        result = fixture.store.removeOne(kRunId, stored);
        QVERIFY2(result.value(QStringLiteral("ok")).toBool(),
                 qPrintable(result.value(QStringLiteral("error")).toString()));
        QVERIFY(fixture.store.imagesFor(kRunId).isEmpty());
        QCOMPARE(changed.count(), 2);
    }

    void aRunHoldsAtMostTwentyImages()
    {
        StoreFixture fixture;
        QStringList adds;
        for (int i = 0; i < mr::NoteImageStore::kMaxImagesPerRun; ++i)
            adds.append(fixture.picture(QStringLiteral("p%1.png").arg(i)));
        QVariantMap result = fixture.store.commit(kRunId, adds, {});
        QVERIFY2(result.value(QStringLiteral("ok")).toBool(),
                 qPrintable(result.value(QStringLiteral("error")).toString()));
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), mr::NoteImageStore::kMaxImagesPerRun);

        const QString extra = fixture.picture(QStringLiteral("extra.png"));
        result = fixture.store.commit(kRunId, {extra}, {});
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(result.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("最多")));
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), mr::NoteImageStore::kMaxImagesPerRun);

        // A removal in the same commit frees the slot.
        const QString oldest =
            fixture.store.imagesFor(kRunId).first().toMap().value(QStringLiteral("path")).toString();
        result = fixture.store.commit(kRunId, {extra}, {oldest});
        QVERIFY2(result.value(QStringLiteral("ok")).toBool(),
                 qPrintable(result.value(QStringLiteral("error")).toString()));
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), mr::NoteImageStore::kMaxImagesPerRun);
    }

    void aCopyThatFailsMidBatchRollsBackAndReportsNothingAdded()
    {
        StoreFixture fixture;
        QSignalSpy changed(&fixture.store, &mr::NoteImageStore::imagesChanged);
        const QString first = fixture.picture(QStringLiteral("one.png"));
        const QString second = fixture.picture(QStringLiteral("two.png"), Qt::blue);

        // The first copy lands, the second is refused by the file system.
        int copies = 0;
        fixture.store.setCopyFunctionForTesting([&copies](const QString &source, const QString &target) {
            return ++copies == 1 && QFile::copy(source, target);
        });
        const QVariantMap result = fixture.store.commit(kRunId, {first, second}, {});
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(result.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("无法把图片复制")));
        QCOMPARE(copies, 2);

        // All-or-nothing on disk, and the report says so: a caller that trusted
        // `added` would otherwise drop the first image from its staging list
        // although its copy was just rolled back.
        QVERIFY(result.value(QStringLiteral("added")).toStringList().isEmpty());
        QVERIFY(fixture.store.imagesFor(kRunId).isEmpty());
        QVERIFY(!QDir(fixture.runDir()).exists());
        QCOMPARE(changed.count(), 0);
        QVERIFY(QFileInfo::exists(first));
        QVERIFY(QFileInfo::exists(second));

        // Back to the real copy, the same batch goes through.
        fixture.store.setCopyFunctionForTesting({});
        const QVariantMap retry = fixture.store.commit(kRunId, {first, second}, {});
        QVERIFY2(retry.value(QStringLiteral("ok")).toBool(),
                 qPrintable(retry.value(QStringLiteral("error")).toString()));
        QCOMPARE(retry.value(QStringLiteral("added")).toStringList(), QStringList({first, second}));
        QCOMPARE(fixture.store.imagesFor(kRunId).size(), 2);
    }

    void aDeclaredCanvasBeyondTheCapIsRefusedBeforeDecoding()
    {
        StoreFixture fixture;

        // A BMP header that announces 10000 x 10000 pixels with no pixel data
        // behind it: a few dozen bytes on disk, hundreds of megabytes decoded.
        const QString bomb = QDir(fixture.pictures.path()).absoluteFilePath(QStringLiteral("bomb.bmp"));
        {
            QFile file(bomb);
            QVERIFY(file.open(QIODevice::WriteOnly));
            QDataStream out(&file);
            out.setByteOrder(QDataStream::LittleEndian);
            out.writeRawData("BM", 2);
            out << quint32(54) << quint32(0) << quint32(54);     // file size, reserved, pixel offset
            out << quint32(40) << qint32(10000) << qint32(10000); // info size, width, height
            out << quint16(1) << quint16(24) << quint32(0) << quint32(0);
            out << qint32(2835) << qint32(2835) << quint32(0) << quint32(0);
        }
        const QVariantMap look = fixture.store.inspect(bomb);
        QVERIFY(!look.value(QStringLiteral("ok")).toBool());
        QVERIFY2(look.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("分辨率")),
                 qPrintable(look.value(QStringLiteral("error")).toString()));

        // Refused before any copy, so the folder never appears.
        const QVariantMap result = fixture.store.commit(kRunId, {bomb}, {});
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY(!QDir(fixture.runDir()).exists());

        // An ordinary screenshot-sized canvas is fine (the byte cap governs it).
        const QString shot = fixture.picture(QStringLiteral("shot.png"));
        QVERIFY(fixture.store.inspect(shot).value(QStringLiteral("ok")).toBool());
    }

    void aRunFolderThatIsAJunctionIsNeverWrittenOrDeletedThrough()
    {
        StoreFixture fixture;
        QVERIFY(QDir().mkpath(fixture.store.rootDirectory()));

        // Somewhere else on the disk, with a file in it, and the run folder
        // planted as a junction pointing there.
        QTemporaryDir elsewhere;
        const QString victim = QDir(elsewhere.path()).absoluteFilePath(QStringLiteral("victim.png"));
        QVERIFY(writeImage(victim));
        QProcess mklink;
        mklink.start(QStringLiteral("cmd.exe"),
                     {QStringLiteral("/c"), QStringLiteral("mklink"), QStringLiteral("/J"),
                      QDir::toNativeSeparators(fixture.runDir()),
                      QDir::toNativeSeparators(elsewhere.path())});
        if (!mklink.waitForFinished(10000) || mklink.exitCode() != 0)
            QSKIP("mklink /J is unavailable here");
        const QFileInfo planted(fixture.runDir());
        QVERIFY(planted.exists());
        QVERIFY(planted.isJunction());
        // Whatever happens below, the junction goes before the fixtures do, or
        // QTemporaryDir would clean the target through it.
        auto unlink = qScopeGuard([&] { QDir(fixture.store.rootDirectory()).rmdir(kRunId); });

        // The file shows through the junction, and its path lies inside the run
        // folder on paper; deleting it must still be refused.
        const QString throughJunction = fixture.runDir() + QStringLiteral("/victim.png");
        QVERIFY(QFileInfo::exists(throughJunction));
        QVariantMap result = fixture.store.removeOne(kRunId, throughJunction);
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QVERIFY2(result.value(QStringLiteral("error")).toString().contains(QString::fromUtf8("重定向")),
                 qPrintable(result.value(QStringLiteral("error")).toString()));
        QVERIFY(QFileInfo::exists(victim));

        // Nor is anything written through it.
        const QString shot = fixture.picture(QStringLiteral("shot.png"));
        result = fixture.store.addFile(kRunId, shot);
        QVERIFY(!result.value(QStringLiteral("ok")).toBool());
        QCOMPARE(QDir(elsewhere.path()).entryList(QDir::Files).size(), 1);
        QVERIFY(planted.exists());
    }

    // ------------------------------------------------------------ 向导 --

    void theWizardFollowsChangesMadeElsewhereWhileOpen()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        const QString first = fixture.files.picture(QStringLiteral("one.png"));
        const QString second = fixture.files.picture(QStringLiteral("two.png"), Qt::blue);
        QVERIFY(fixture.files.store.commit(kRunId, {first, second}, {}).value(QStringLiteral("ok")).toBool());
        QVERIFY(fixture.openForRun());
        auto *dialog = fixture.dialog();
        QCOMPARE(fixture.rows().size(), 2);

        // The 心得 dialog, popping up over the wizard, removes one: the wizard
        // stops showing it.
        const QString goneOne = fixture.rows().at(0).toMap().value(QStringLiteral("path")).toString();
        const QString goneTwo = fixture.rows().at(1).toMap().value(QStringLiteral("path")).toString();
        QVERIFY(fixture.files.store.removeOne(kRunId, goneOne).value(QStringLiteral("ok")).toBool());
        QCOMPARE(fixture.rows().size(), 1);
        QVERIFY(!dialog->property("imagesDirty").toBool());

        // A removal queued in the wizard whose file is then deleted elsewhere is
        // no longer a change to save.
        QVERIFY(QMetaObject::invokeMethod(dialog, "unstageNoteImage",
                                          Q_ARG(QVariant, QVariant(fixture.rows().first().toMap()))));
        QVERIFY(dialog->property("imagesDirty").toBool());
        QVERIFY(fixture.files.store.removeOne(kRunId, goneTwo).value(QStringLiteral("ok")).toBool());
        QVERIFY(fixture.rows().isEmpty());
        QVERIFY(!dialog->property("imagesDirty").toBool());

        // Added elsewhere: shown here too, as an existing image, not a pending one.
        QVERIFY(fixture.files.store.addFile(kRunId, first).value(QStringLiteral("ok")).toBool());
        QCOMPARE(fixture.rows().size(), 1);
        QVERIFY(!fixture.rows().first().toMap().value(QStringLiteral("pending")).toBool());

        // A closed dialog does not follow; reopening reads the folder afresh anyway.
        QVERIFY(QMetaObject::invokeMethod(dialog, "close"));
        QVERIFY(fixture.files.store.addFile(kRunId, second).value(QStringLiteral("ok")).toBool());
        QCOMPARE(fixture.rows().size(), 1);
        QVERIFY(fixture.openForRun());
        QCOMPARE(fixture.rows().size(), 2);
    }

    void aCopyThatFailsMidSaveKeepsEveryStagedImageInTheWizard()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun());
        auto *dialog = fixture.dialog();
        const QString first = fixture.files.picture(QStringLiteral("one.png"));
        const QString second = fixture.files.picture(QStringLiteral("two.png"), Qt::blue);
        QVERIFY(fixture.stage(first));
        QVERIFY(fixture.stage(second));

        int copies = 0;
        fixture.files.store.setCopyFunctionForTesting([&copies](const QString &source, const QString &target) {
            return ++copies == 1 && QFile::copy(source, target);
        });
        QVERIFY(fixture.submit());
        QVERIFY(dialog->property("visible").toBool());
        QCOMPARE(dialog->property("errorCode").toString(), QStringLiteral("ERR_NOTE_IMAGE"));
        // Both are still staged: the first was rolled back, not saved.
        QCOMPARE(fixture.rows().size(), 2);
        QVERIFY(fixture.rows().at(0).toMap().value(QStringLiteral("pending")).toBool());
        QVERIFY(fixture.rows().at(1).toMap().value(QStringLiteral("pending")).toBool());
        QVERIFY(fixture.files.store.imagesFor(kRunId).isEmpty());

        // Once the disk cooperates, 保存 again writes both.
        fixture.files.store.setCopyFunctionForTesting({});
        QVERIFY(fixture.submit());
        QVERIFY(!dialog->property("visible").toBool());
        QCOMPARE(fixture.files.store.imagesFor(kRunId).size(), 2);
    }

    void wizardStagesImagesAndAppliesThemOnlyOnSave()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun());
        auto *dialog = fixture.dialog();
        QSignalSpy corrections(dialog, SIGNAL(correctRequested(QVariant,QString)));
        QSignalSpy creations(dialog, SIGNAL(createRequested(QVariant,QString)));

        // Nothing yet: the save button is off as for any unchanged correction.
        QVERIFY(!dialog->property("imagesDirty").toBool());
        QVERIFY(fixture.rows().isEmpty());
        QVERIFY(QMetaObject::invokeMethod(dialog, "goToStep", Q_ARG(QVariant, QVariant(3))));
        QTRY_VERIFY(fixture.item(QStringLiteral("saveRunButton")));
        QVERIFY(!fixture.item(QStringLiteral("saveRunButton"))->isEnabled());
        QVERIFY(fixture.item(QStringLiteral("noteImageStrip")));

        // A refusal shows in the banner and stages nothing.
        const QString fake = QDir(fixture.files.pictures.path()).absoluteFilePath(QStringLiteral("bad.png"));
        {
            QFile file(fake);
            QVERIFY(file.open(QIODevice::WriteOnly));
            file.write("nope");
        }
        QVERIFY(!fixture.stage(fake));
        QCOMPARE(dialog->property("errorCode").toString(), QStringLiteral("ERR_NOTE_IMAGE"));
        QVERIFY(!dialog->property("errorText").toString().isEmpty());
        QVERIFY(!dialog->property("imagesDirty").toBool());

        // Two real ones, one staged twice: staged once, shown as 待保存, nothing copied.
        const QString first = fixture.files.picture(QStringLiteral("one.png"));
        const QString second = fixture.files.picture(QStringLiteral("two.png"), Qt::blue);
        QVERIFY(fixture.stage(first));
        QVERIFY(fixture.stage(first));
        QVERIFY(fixture.stage(second));
        QVERIFY(dialog->property("errorText").toString().isEmpty());
        QVERIFY(dialog->property("imagesDirty").toBool());
        QCOMPARE(fixture.rows().size(), 2);
        QVERIFY(fixture.rows().first().toMap().value(QStringLiteral("pending")).toBool());
        QVERIFY(!QDir(fixture.files.runDir()).exists());
        QTRY_VERIFY(fixture.item(QStringLiteral("saveRunButton"))->isEnabled());

        // 取消 throws the staging away.
        QVERIFY(QMetaObject::invokeMethod(dialog, "close"));
        QVERIFY(fixture.openForRun());
        QVERIFY(!dialog->property("imagesDirty").toBool());
        QVERIFY(!QDir(fixture.files.runDir()).exists());

        // 保存 with images only: no correction, no creation, the files land, the dialog closes.
        QVERIFY(fixture.stage(first));
        QVERIFY(fixture.stage(second));
        QVERIFY2(dialog->property("imagesDirty").toBool(),
                 qPrintable(QStringLiteral("pending=%1 existing=%2 rows=%3 savedRunId=%4 editMode=%5")
                                .arg(asList(dialog->property("pendingImageAdds")).size())
                                .arg(asList(dialog->property("existingImages")).size())
                                .arg(fixture.rows().size())
                                .arg(dialog->property("savedRunId").toString(),
                                     dialog->property("editMode").toString())));
        QVERIFY(fixture.submit());
        QCOMPARE(corrections.count(), 0);
        QCOMPARE(creations.count(), 0);
        QVERIFY2(!dialog->property("visible").toBool(),
                 qPrintable(QStringLiteral("errorCode=%1 submitting=%2 pending=%3 stored=%4 dirty=%5")
                                .arg(dialog->property("errorCode").toString(),
                                     dialog->property("submitting").toString())
                                .arg(asList(dialog->property("pendingImageAdds")).size())
                                .arg(fixture.files.store.imagesFor(kRunId).size())
                                .arg(dialog->property("imagesDirty").toString())));
        QCOMPARE(fixture.files.store.imagesFor(kRunId).size(), 2);
        QVERIFY(!dialog->property("imagesDirty").toBool());
        QCOMPARE(fixture.controller.toastMessage(), QString::fromUtf8("已添加 2 张图片"));

        // Reopened: the two are existing images now; removing one and saving deletes it.
        QVERIFY(fixture.openForRun());
        QCOMPARE(fixture.rows().size(), 2);
        QVERIFY(!fixture.rows().first().toMap().value(QStringLiteral("pending")).toBool());
        const QVariantMap doomed = fixture.rows().first().toMap();
        QVERIFY(QMetaObject::invokeMethod(dialog, "unstageNoteImage", Q_ARG(QVariant, QVariant(doomed))));
        QCOMPARE(fixture.rows().size(), 1);
        QVERIFY(dialog->property("imagesDirty").toBool());
        QVERIFY(fixture.submit());
        QCOMPARE(corrections.count(), 0);
        QVERIFY(!dialog->property("visible").toBool());
        QVERIFY(!QFileInfo::exists(doomed.value(QStringLiteral("path")).toString()));
        QCOMPARE(fixture.files.store.imagesFor(kRunId).size(), 1);
    }

    // 审查 OL-3：记录照采集服务存的样子（职能 TANK、整分钟的时间），玩家只加了一张图，
    // 中途点过一下进本时间框又离开。此前离开时间框把 HH:mm:00 写成 HH:mm，再加上表单里
    // 总与记录不同的 role，保存发出一条空修正，被拒为 ERR_NO_CHANGES，图片始终没存上。
    void anImageOnlySaveSurvivesLeavingATimeField()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVariantMap value = DialogFixture::run();
        value.insert(QStringLiteral("role"), QStringLiteral("TANK"));
        value.insert(QStringLiteral("ended_at_utc"), QStringLiteral("2026-09-04T12:59:00.000Z"));
        value.insert(QStringLiteral("duration_ms"), 1080000);
        auto *dialog = fixture.dialog();
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForRun", Q_ARG(QVariant, QVariant(value))));
        QVERIFY(QMetaObject::invokeMethod(dialog, "goToStep", Q_ARG(QVariant, QVariant(3))));
        QSignalSpy corrections(dialog, SIGNAL(correctRequested(QVariant,QString)));

        QVERIFY(fixture.stage(fixture.files.picture(QStringLiteral("shot.png"))));
        // What leaving the field does (TimeField.onEditingFinished).
        for (const char *name : {"enteredTimeField", "endedTimeField"}) {
            QQuickItem *field = fixture.item(QString::fromLatin1(name));
            QVERIFY2(field, name);
            QVERIFY(QMetaObject::invokeMethod(field, "canonicalize"));
        }
        QVERIFY(fixture.submit());
        QCOMPARE(corrections.count(), 0);
        QTRY_VERIFY(!dialog->property("visible").toBool());
        QCOMPARE(fixture.files.store.imagesFor(kRunId).size(), 1);
    }

    void imagesFollowARecordCorrectionAndAreNeverPartOfIt()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        QVERIFY(fixture.openForRun());
        auto *dialog = fixture.dialog();
        QSignalSpy corrections(dialog, SIGNAL(correctRequested(QVariant,QString)));

        const QString shot = fixture.files.picture(QStringLiteral("shot.png"));
        QVERIFY(fixture.stage(shot));
        dialog->setProperty("noteText", QString::fromUtf8("第一次带新人"));
        QVERIFY(fixture.submit());

        // The correction carries the note and nothing about the image; the file
        // waits for the Collector's answer.
        QCOMPARE(corrections.count(), 1);
        const QVariantMap changes = asMap(corrections.first().at(0));
        QCOMPARE(changes.value(QStringLiteral("note")).toString(), QString::fromUtf8("第一次带新人"));
        QVERIFY(!changes.contains(QStringLiteral("note_images")));
        QVERIFY(!changes.contains(QStringLiteral("images")));
        QVERIFY(dialog->property("submitting").toBool());
        QVERIFY(!QDir(fixture.files.runDir()).exists());

        // Someone else's reply is not ours: still waiting, still nothing copied.
        QVariant accepted;
        QVERIFY(QMetaObject::invokeMethod(dialog, "acceptSubmission", Q_RETURN_ARG(QVariant, accepted),
                                          Q_ARG(QVariant, QVariant(QStringLiteral("run-other")))));
        QVERIFY(!accepted.toBool());
        QVERIFY(!QDir(fixture.files.runDir()).exists());

        // Ours: the image is applied to that run and the dialog closes.
        QVERIFY(QMetaObject::invokeMethod(dialog, "acceptSubmission", Q_RETURN_ARG(QVariant, accepted),
                                          Q_ARG(QVariant, QVariant(kRunId))));
        QVERIFY(accepted.toBool());
        QVERIFY(!dialog->property("visible").toBool());
        QCOMPARE(fixture.files.store.imagesFor(kRunId).size(), 1);
        QVERIFY(!dialog->property("imagesDirty").toBool());
    }

    void aNewRecordGetsItsImagesUnderTheIdTheCollectorIssued()
    {
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto *dialog = fixture.dialog();
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForCreate"));
        QSignalSpy creations(dialog, SIGNAL(createRequested(QVariant,QString)));

        const QString shot = fixture.files.picture(QStringLiteral("shot.png"));
        QVERIFY(fixture.stage(shot));
        dialog->setProperty("matchedTime", QStringLiteral("20:00"));
        dialog->setProperty("enteredTime", QStringLiteral("20:05"));
        dialog->setProperty("endedTime", QStringLiteral("20:30"));
        QVERIFY(fixture.submit());
        QCOMPARE(creations.count(), 1);
        QVERIFY(!QDir(fixture.files.runDir()).exists());

        // The Collector names the new run; the folder takes that name.
        QVariant accepted;
        QVERIFY(QMetaObject::invokeMethod(dialog, "acceptSubmission", Q_RETURN_ARG(QVariant, accepted),
                                          Q_ARG(QVariant, QVariant(kRunId))));
        QVERIFY(accepted.toBool());
        QVERIFY(!dialog->property("visible").toBool());
        QCOMPARE(fixture.files.store.imagesFor(kRunId).size(), 1);
    }

    void aFailedImageSaveKeepsTheDialogOpenWithoutResendingTheRecord_data()
    {
        QTest::addColumn<bool>("editing");
        QTest::newRow("create") << false;
        QTest::newRow("correct") << true;
    }

    void aFailedImageSaveKeepsTheDialogOpenWithoutResendingTheRecord()
    {
        QFETCH(bool, editing);
        DialogFixture fixture;
        QVERIFY2(fixture.create(), qPrintable(fixture.errors));
        auto *dialog = fixture.dialog();
        if (editing)
            QVERIFY(fixture.openForRun());
        else
            QVERIFY(QMetaObject::invokeMethod(dialog, "openForCreate"));
        QSignalSpy creations(dialog, SIGNAL(createRequested(QVariant,QString)));
        QSignalSpy corrections(dialog, SIGNAL(correctRequested(QVariant,QString)));

        const QString shot = fixture.files.picture(QStringLiteral("shot.png"));
        QVERIFY(fixture.stage(shot));
        dialog->setProperty("matchedTime", QStringLiteral("20:00"));
        dialog->setProperty("enteredTime", QStringLiteral("20:05"));
        dialog->setProperty("endedTime", QStringLiteral("20:30"));
        QVERIFY(fixture.submit());
        QCOMPARE(creations.count() + corrections.count(), 1);

        // The source vanished between choosing it and the Collector's answer.
        QVERIFY(QFile::remove(shot));
        QVariant accepted;
        QVERIFY(QMetaObject::invokeMethod(dialog, "acceptSubmission", Q_RETURN_ARG(QVariant, accepted),
                                          Q_ARG(QVariant, QVariant(kRunId))));
        QVERIFY(accepted.toBool());
        QVERIFY(dialog->property("visible").toBool());
        QCOMPARE(dialog->property("errorCode").toString(), QStringLiteral("ERR_NOTE_IMAGE"));
        QCOMPARE(dialog->property("savedRunId").toString(), kRunId);
        QVERIFY(dialog->property("imagesDirty").toBool());
        for (const char *name : {"wizardStep1", "wizardStep2", "matchedDateField",
                                 "matchedTimeField", "enteredTimeField", "endedTimeField",
                                 "reasonField", "noteField", "prevStepButton"}) {
            const auto *field = dialog->findChild<QObject *>(QLatin1String(name));
            QVERIFY2(field, name);
            QVERIFY2(!field->property("enabled").toBool(), name);
        }
        const auto *strip = dialog->findChild<QObject *>(QStringLiteral("noteImageStrip"));
        QVERIFY(strip && strip->property("enabled").toBool());
        QVERIFY(strip->property("editable").toBool());
        auto *save = dialog->findChild<QObject *>(QStringLiteral("saveRunButton"));
        QVERIFY(save && save->property("enabled").toBool());
        QCOMPARE(save->property("text").toString(), QString::fromUtf8("重试图片"));
        QVERIFY(QMetaObject::invokeMethod(dialog, "prevStep"));
        QCOMPARE(dialog->property("currentStep").toInt(), 3);

        // A second 保存 must not create the record again; dropping the image
        // and saving once more just closes the dialog.
        QVERIFY(fixture.submit());
        QCOMPARE(creations.count() + corrections.count(), 1);
        QVERIFY(dialog->property("visible").toBool());
        const QVariantMap pending = fixture.rows().first().toMap();
        QVERIFY(QMetaObject::invokeMethod(dialog, "unstageNoteImage", Q_ARG(QVariant, QVariant(pending))));
        QVERIFY(!dialog->property("imagesDirty").toBool());
        QVERIFY(save->property("enabled").toBool());
        QVERIFY(fixture.submit());
        QCOMPARE(creations.count() + corrections.count(), 1);
        QVERIFY(!dialog->property("visible").toBool());
        QVERIFY(dialog->property("savedRunId").toString().isEmpty());
        QVERIFY(QMetaObject::invokeMethod(dialog, "openForCreate"));
        QVERIFY(dialog->findChild<QObject *>(QStringLiteral("noteField"))->property("enabled").toBool());
    }
};

int main(int argc, char *argv[])
{
    // Never run against the user's own Collector, database or serve lease - see
    // TestCollectorGuard.h. Must precede any construction.
    mrtest::disableCollectorLaunch();
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    NoteImageStoreTests tests;
    return QTest::qExec(&tests, argc, argv);
}

#include "NoteImageStoreTests.moc"
