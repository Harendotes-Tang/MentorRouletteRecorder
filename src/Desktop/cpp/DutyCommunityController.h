#pragma once

#include <QObject>
#include <QPointer>
#include <QString>
#include <QUrl>
#include <QVariantMap>

namespace mr {

class IBackend;

class DutyCommunityController final : public QObject
{
    Q_OBJECT
public:
    explicit DutyCommunityController(IBackend *backend, QObject *parent = nullptr);
    /// The duty's guide page, or the site's list of duties when the duty is unknown or has no
    /// page. The site follows the interface language: 新大陆见闻录 in both Chinese interfaces,
    /// Console Games Wiki in English and, until it has a site of its own, in Japanese.
    static QUrl guideUrl(const QVariantMap &run);
    static QUrl guideUrl(const QVariantMap &run, const QString &languageCode);
    /// The name of the site guideUrl() opens for \a languageCode; a proper name, not translated.
    static QString guideSite(const QString &languageCode);
    /// guideSite() for the interface language.
    Q_INVOKABLE QString guideSiteName() const;
    Q_INVOKABLE bool hasGuide(const QVariantMap &run) const;
    Q_INVOKABLE bool openGuide(const QVariantMap &run) const;
    Q_INVOKABLE QVariantMap dutyIdentity(const QVariantMap &run) const;
    Q_INVOKABLE QString newId() const;
    Q_INVOKABLE void request(int ticket, const QString &action, const QVariantMap &payload = {});
    Q_INVOKABLE QString exportFile(const QVariantList &items) const;
    Q_INVOKABLE QVariantMap pickImport() const;
    Q_INVOKABLE bool copy(const QString &value) const;
    /// "like" opens the comment `comment` of Issue `issue`, the one whose 👍 count an entry shows.
    Q_INVOKABLE bool openGitHub(const QString &target, int issue = 0, qint64 comment = 0) const;

signals:
    void answered(int ticket, const QVariantMap &result, const QString &error);

private:
    QPointer<IBackend> m_backend;
};

} // namespace mr
