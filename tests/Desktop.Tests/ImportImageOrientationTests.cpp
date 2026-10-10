// Loads both real Image objects from ImportRecordsDialog and supplies synthetic EXIF JPEGs.
#include <QBuffer>
#include <QDir>
#include <QFile>
#include <QGuiApplication>
#include <QImage>
#include <QImageReader>
#include <QPainter>
#include <QQuickItemGrabResult>
#include <QQuickWindow>
#include <QQmlComponent>
#include <QQmlEngine>
#include <QQuickItem>
#include <QQuickStyle>
#include <QTemporaryDir>
#include <QTest>
#include <memory>

class ImportImageOrientationTests final : public QObject
{
    Q_OBJECT
private slots:
    void initTestCase()
    {
        const QString root = QString::fromUtf8(MR_DESKTOP_QML_DIR);
        qmlRegisterSingletonType(QUrl::fromLocalFile(root + "/Theme.qml"), "MentorRecorder", 1, 0, "Theme");
        for (const QString &directory : {QStringLiteral("/components"), QStringLiteral("/dialogs")})
            for (const QString &file : QDir(root + directory).entryList({"*.qml"}, QDir::Files))
                qmlRegisterType(QUrl::fromLocalFile(root + directory + '/' + file), "MentorRecorder", 1, 0, file.chopped(4).toUtf8().constData());
    }
    void sourceAndWholeImageUseTheSameOrientationAsOcr_data()
    {
        QTest::addColumn<int>("orientation");
        QTest::addColumn<QSize>("expectedSize");
        QTest::addColumn<int>("filenameDpr");
        for (int dpr : {1, 2})
            for (int orientation = 0; orientation <= 8; ++orientation) {
                const QByteArray name = (orientation == 0 ? QStringLiteral("png")
                    : QStringLiteral("jpeg-exif-%1").arg(orientation)).toUtf8()
                    + "-dpr" + QByteArray::number(dpr);
                const QSize size = orientation >= 5 ? QSize(80, 160) : QSize(160, 80);
                QTest::newRow(name.constData()) << orientation << size << dpr;
            }
    }
    void sourceAndWholeImageUseTheSameOrientationAsOcr()
    {
        QFETCH(int, orientation);
        QFETCH(QSize, expectedSize);
        QFETCH(int, filenameDpr);
        QTemporaryDir directory;
        QVERIFY(directory.isValid());
        QImage raw(160, 80, QImage::Format_RGB32);
        raw.fill(Qt::red);
        {
            QPainter painter(&raw);
            painter.fillRect(QRect(80, 0, 80, 80), Qt::green);
            painter.fillRect(QRect(0, 40, 80, 40), Qt::blue);
            painter.fillRect(QRect(80, 40, 80, 40), Qt::yellow);
        }
        QByteArray encoded;
        QBuffer buffer(&encoded);
        QVERIFY(buffer.open(QIODevice::WriteOnly));
        QVERIFY(raw.save(&buffer, orientation == 0 ? "PNG" : "JPEG"));
        QByteArray exif = QByteArray::fromHex("45786966000049492a0008000000010012010300010000000100000000000000");
        exif[24] = char(orientation);
        const int length = exif.size() + 2;
        if (orientation != 0)
            encoded.insert(2, QByteArray::fromHex("ffe1") + char(length >> 8) + char(length & 255) + exif);
        const QString suffix = filenameDpr == 1 ? QString() : QStringLiteral("@2x");
        const QString path = directory.filePath(orientation == 0
            ? QStringLiteral("plain%1.png").arg(suffix) : QStringLiteral("oriented%1.jpg").arg(suffix));
        QFile file(path);
        QVERIFY(file.open(QIODevice::WriteOnly));
        QCOMPARE(file.write(encoded), encoded.size());
        file.close();
        QImageReader reader(path);
        reader.setAutoTransform(true); // Same policy used by the OCR/controller source readers.
        const QImage oriented = reader.read();
        QCOMPARE(oriented.size(), expectedSize);
        QCOMPARE(oriented.devicePixelRatio(), qreal(filenameDpr));

        QQmlEngine engine;
        QQmlComponent component(&engine);
        component.setData(R"(import QtQuick
import QtQuick.Controls
import MentorRecorder
ApplicationWindow {
    width: 120; height: 160; visible: true
    ImportRecordsDialog { controller: null }
})", QUrl::fromLocalFile(QString::fromUtf8(MR_DESKTOP_QML_DIR) + "/dialogs/OrientationProbe.qml"));
        std::unique_ptr<QObject> root(component.create());
        QVERIFY2(root != nullptr, qPrintable(component.errorString()));
        auto *crop = root->findChild<QQuickItem *>(QStringLiteral("importSourceImage"));
        auto *whole = root->findChild<QQuickItem *>(QStringLiteral("importWholeImage"));
        QVERIFY(crop && whole);
        for (QQuickItem *image : {crop, whole}) {
            QVERIFY2(image->property("autoTransform").toBool(), qPrintable(image->objectName()));
            QVERIFY(image->setProperty("sourceClipRect", QRectF()));
            QVERIFY(image->setProperty("source", QUrl::fromLocalFile(path)));
            QTRY_COMPARE(image->property("status").toInt(), 1); // Image.Ready
            // @2x changes logical implicit dimensions, never the OCR pixel
            // rectangle. Keep sourceSize at its default full decoded resolution.
            QCOMPARE(image->property("sourceSize").toSize(), expectedSize);
            QCOMPARE(qRound(image->implicitWidth() * filenameDpr), expectedSize.width());
            QCOMPARE(qRound(image->implicitHeight() * filenameDpr), expectedSize.height());
        }
        auto *viewport = root->findChild<QQuickItem *>(QStringLiteral("importSourceCrop"));
        auto *window = qobject_cast<QQuickWindow *>(root.get());
        QVERIFY(viewport && window);
        // Display the real crop visual independently of row selection. Its image
        // is still the production Image from the production import dialog.
        viewport->setParentItem(window->contentItem());
        viewport->setVisible(true);
        QVERIFY(QTest::qWaitForWindowExposed(window));
        const QRect sourceRect(10, 20, 30, 40);
        QVERIFY(viewport->setProperty("sourceRect", QRectF(sourceRect)));
        QCOMPARE(viewport->property("cropRect").toRectF(), QRectF(sourceRect));
        const qreal scale = viewport->property("imageScale").toReal();
        QVERIFY(scale > 0);
        QCOMPARE(viewport->width(), sourceRect.width() * scale);
        QCOMPARE(viewport->height(), sourceRect.height() * scale);
        QCOMPARE(crop->x(), -sourceRect.x() * scale);
        QCOMPARE(crop->y(), -sourceRect.y() * scale);
        QTest::qWait(50);
        const auto grabbed = viewport->grabToImage();
        QVERIFY(grabbed);
        QTRY_VERIFY(!grabbed->image().isNull());
        const QImage actual = grabbed->image();
        const QImage expected = oriented.copy(sourceRect);
        for (qreal fx : {0.25, 0.75})
            for (qreal fy : {0.25, 0.75}) {
                const QColor a = actual.pixelColor(int(actual.width() * fx), int(actual.height() * fy));
                const QColor e = expected.pixelColor(int(expected.width() * fx), int(expected.height() * fy));
                QVERIFY2(qAbs(a.red() - e.red()) + qAbs(a.green() - e.green()) + qAbs(a.blue() - e.blue()) < 20,
                         qPrintable(QStringLiteral("orientation %1: actual %2 expected %3").arg(orientation).arg(a.name(), e.name())));
            }
        const QString evidence = qEnvironmentVariable("MR_DESKTOP_FIX_SCREENSHOTS");
        if (!evidence.isEmpty()) {
            QVERIFY(QDir().mkpath(evidence));
            QVERIFY(actual.save(QDir(evidence).filePath(QStringLiteral("crop-exif-%1-dpr%2.png").arg(orientation).arg(filenameDpr))));
        }
        // Out-of-image OCR rectangles are intersected; unset source images have
        // no size yet and must not produce infinities or negative geometry.
        QVERIFY(viewport->setProperty("sourceRect", QRectF(-10, -20, 30, 40)));
        QCOMPARE(viewport->property("cropRect").toRectF(), QRectF(0, 0, 20, 20));
        QVERIFY(viewport->setProperty("sourceRect", QRectF(500, 500, 30, 40)));
        QCOMPARE(viewport->width(), 0.0);
        QCOMPARE(viewport->height(), 0.0);
        QVERIFY(crop->setProperty("source", QUrl()));
        QTRY_COMPARE(crop->property("status").toInt(), 0);
        QCOMPARE(viewport->width(), 0.0);
        QCOMPARE(viewport->height(), 0.0);
    }
};
int main(int argc, char **argv)
{
    qputenv("QT_QPA_PLATFORM", "offscreen");
    QQuickStyle::setStyle(QStringLiteral("Basic"));
    QGuiApplication app(argc, argv);
    ImportImageOrientationTests tests;
    return QTest::qExec(&tests, argc, argv);
}
#include "ImportImageOrientationTests.moc"
