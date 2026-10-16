#pragma once

// ---------------------------------------------------------------------------
// Game names in the interface language.
//
// Runs, statistics and the catalogues carry the Chinese client's names: the
// record is written in Simplified Chinese and stays so. This class only decides
// how such a name is SHOWN: in 繁體中文 / English / 日本語 the official game text
// of that language, from the bundled data/i18n/game-names.<date>.json (id -> name,
// see data/i18n/README.md), and in 简体中文 the record's own name. A name the
// file does not know is shown as recorded.
//
// Display only: nothing here is written back, sent to the Collector or used to
// identify a duty. Pickers keep the Chinese name as their value and show this
// as their label.
// ---------------------------------------------------------------------------

#include <QHash>
#include <QObject>
#include <QString>
#include <QVariant>

namespace mr {

class GameNames : public QObject
{
    Q_OBJECT

    Q_PROPERTY(QString dataVersion READ dataVersion CONSTANT)

public:
    explicit GameNames(QObject *parent = nullptr);

    /// Process-wide instance (the data is bundled and read-only).
    static GameNames *shared();

    QString dataVersion() const { return m_dataVersion; }

    /// The duty \a contentId in the interface language. \a recorded is the
    /// record's own (Chinese) name: returned as is in 简体中文, and used to find
    /// the duty when \a contentId is missing.
    Q_INVOKABLE QString duty(const QVariant &contentId, const QString &recorded = QString()) const;
    /// The job \a jobId; \a recorded as for duty().
    Q_INVOKABLE QString job(const QVariant &jobId, const QString &recorded = QString()) const;
    /// The roulette \a rouletteId; \a recorded as for duty().
    Q_INVOKABLE QString roulette(const QVariant &rouletteId, const QString &recorded = QString()) const;
    /// A roulette known only by its Chinese client name (the calibration dialog's list).
    Q_INVOKABLE QString rouletteByName(const QString &recorded) const;
    /// A duty category label (四人迷宫, 讨伐歼灭战 ...) in the interface language.
    Q_INVOKABLE QString category(const QString &recorded) const;
    /// An expansion, given by the catalogue's English key ("A Realm Reborn" ...).
    Q_INVOKABLE QString expansion(const QString &englishKey) const;
    /// A role group of the prototype's legend (坦克 / 治疗 / 近战 / 远程物理 / 魔法 / 其他).
    Q_INVOKABLE QString roleGroup(const QString &recorded) const;

    /// A name-only search alias for the Collector's recorded Chinese names.
    /// Outside 简体中文, text found in duty or job names becomes the Chinese name
    /// those matches share (Sastasha -> 天然要害沙斯塔夏溶洞 includes hard mode).
    /// Anything else returns unchanged. Callers keep the original in text and
    /// send a differing alias in name_text; this never replaces note words.
    Q_INVOKABLE QString recordedSearchText(const QString &text) const;

    /// The same lookups for an explicit language code (tests, speech).
    QString dutyIn(const QString &code, const QVariant &contentId, const QString &recorded) const;
    QString jobIn(const QString &code, const QVariant &jobId, const QString &recorded) const;
    QString recordedSearchTextIn(const QString &code, const QString &text) const;

private:
    using Names = QHash<QString, QString>;  // language code -> name

    void load();
    static QString pick(const QHash<qint64, Names> &table, const QString &code,
                        const QVariant &id, qint64 fallbackId, const QString &recorded);

    QHash<qint64, Names> m_duties;
    QHash<qint64, Names> m_jobs;
    QHash<qint64, Names> m_roulettes;
    QHash<QString, Names> m_categories;   // keyed by the Simplified-Chinese label
    QHash<QString, Names> m_expansions;   // keyed by the English label
    QHash<QString, qint64> m_dutyIdByName;      // Chinese client name -> content_id
    QHash<QString, qint64> m_jobIdByName;
    QHash<QString, qint64> m_rouletteIdByName;
    QString m_dataVersion;
};

} // namespace mr
