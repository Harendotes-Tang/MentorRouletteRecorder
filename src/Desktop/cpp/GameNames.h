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
// Display only: nothing here is written back or used to identify a duty, and
// the only thing sent to the Collector is a search's recorded names
// (recordedSearchNames). Pickers keep the Chinese name as their value and show
// this as their label.
// ---------------------------------------------------------------------------

#include <QHash>
#include <QObject>
#include <QString>
#include <QStringList>
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
    /// Whether the CN catalogue gives this identity a name shared by other duties.
    /// Used only to distinguish selectable labels; no record is changed.
    Q_INVOKABLE bool dutyNameAmbiguous(const QVariant &contentId) const;
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

    /// The recorded (Chinese) names of the duties and jobs whose name in the
    /// interface language contains \a text, sorted: "Sastasha" gives
    /// 天然要害沙斯塔夏溶洞 and its hard mode, "Paladin" gives 骑士 alone, not
    /// 暗黑骑士. Empty in 简体中文, for no match, or for more than
    /// kMaxSearchNames. Callers keep the original in text, so note words still
    /// match, and send these in name_exact.
    Q_INVOKABLE QStringList recordedSearchNames(const QString &text) const;

    /// The most names one search sends ($defs/RunFilter.name_exact maxItems).
    static constexpr int kMaxSearchNames = 1000;

    /// The same lookups for an explicit language code (tests, speech).
    QString dutyIn(const QString &code, const QVariant &contentId, const QString &recorded) const;
    QString jobIn(const QString &code, const QVariant &jobId, const QString &recorded) const;
    QStringList recordedSearchNamesIn(const QString &code, const QString &text) const;

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
