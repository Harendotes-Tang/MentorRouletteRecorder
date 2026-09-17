#pragma once

// ---------------------------------------------------------------------------
// 共享校准 / shared calibration (§5.1, §7.2), desktop half.
//
// Projects $defs/SharedCalibrationStatus - calibration.shared inside the
// capture status the AppController already owns - into the few sentences a
// player reads on the calibration card and, for 分享给其他玩家, on the 协议档案
// card that is never hidden, and sends the five requests the player's clicks
// produce. Like CalibrationController it holds no evidence of its own.
//
// It never touches the network. 分享给其他玩家 hands a github.com address to the
// system browser on an explicit click, the same way the npcap.com link does,
// and copies the code to the clipboard; whether anything is submitted is the
// player's decision in that browser. last_refusal and the candidates' short
// ids are diagnostics and never reach a sentence built here.
// ---------------------------------------------------------------------------

#include <QObject>
#include <QPointer>
#include <QQmlEngine>
#include <QString>
#include <QUrl>
#include <QVariantMap>

#include <functional>

namespace mr {
class IBackend;

class SharedCalibrationController final : public QObject
{
    Q_OBJECT
    QML_ANONYMOUS
    /// The Collector sent calibration.shared. An older Collector never does,
    /// and then nothing that would send one of the five requests is offered.
    Q_PROPERTY(bool available READ available NOTIFY changed)
    /// What the card shows: none, disabled, fetching, unavailable,
    /// none_for_build, verifying, consent, verified, rejected or user_rejected.
    Q_PROPERTY(QString view READ view NOTIFY changed)
    /// The player-facing sentence for view, and the one that explains it.
    Q_PROPERTY(QString headline READ headline NOTIFY changed)
    Q_PROPERTY(QString detail READ detail NOTIFY changed)
    /// The profile in force was rebuilt from another player's share code.
    Q_PROPERTY(bool inUse READ inUse NOTIFY changed)
    /// The shared profile in force records, but the match and the duty entry are
    /// still being audited (plans/shared-calibration.md §18.4). An older Collector
    /// never reports it, and then it stays false: "not reported", not "no audit".
    Q_PROPERTY(bool auditPending READ auditPending NOTIFY changed)
    /// Which gate set the candidates are judged by, in one word: "published" when
    /// at least one candidate that is not rejected came from an index this machine
    /// read, "imported" when every one of them was pasted and no index knows it,
    /// and empty when a Collector before 1.1.0 reports no provenance at all.
    Q_PROPERTY(QString candidateProvenance READ candidateProvenance NOTIFY changed)
    Q_PROPERTY(bool userRejected READ userRejected NOTIFY changed)
    /// Which of the card's buttons make sense right now.
    Q_PROPERTY(bool canCheck READ canCheck NOTIFY changed)
    Q_PROPERTY(bool canImport READ canImport NOTIFY changed)
    Q_PROPERTY(bool canReject READ canReject NOTIFY changed)
    Q_PROPERTY(bool canShare READ canShare NOTIFY changed)
    Q_PROPERTY(bool busy READ busy NOTIFY changed)
    /// The queue-inference trade-off, in the words the local calibration card uses.
    Q_PROPERTY(QString consentText READ consentText CONSTANT)
    /// What 分享给其他玩家 does, for whichever card offers it. One sentence, one place:
    /// the calibration card and the 协议档案 card must never word this differently.
    Q_PROPERTY(QString shareHint READ shareHint CONSTANT)

public:
    using UrlOpener = std::function<bool(const QUrl &)>;
    using ClipboardWriter = std::function<void(const QString &)>;

    explicit SharedCalibrationController(QObject *parent = nullptr);

    void setBackend(IBackend *backend);
    /// Test seams. By default the address goes to QDesktopServices and the code
    /// to the system clipboard.
    void setUrlOpener(UrlOpener opener);
    void setClipboardWriter(ClipboardWriter writer);

    bool available() const { return m_inputs.available; }
    QString view() const;
    QString headline() const;
    QString detail() const;
    bool inUse() const;
    bool auditPending() const { return m_inputs.auditPending; }
    QString candidateProvenance() const { return m_inputs.provenance; }
    bool userRejected() const { return m_inputs.userRejected; }
    bool canCheck() const;
    bool canImport() const;
    bool canReject() const;
    bool canShare() const;
    bool busy() const { return m_busy; }

    static QString consentText();
    static QString shareHint();
    /// The sentence each ERR_SHARE_CODE_UNAVAILABLE details.reason is explained with.
    static QString shareRefusalMessage(const QString &reason);
    /// True only for an https://github.com/ address without credentials or a port:
    /// the one kind of address GetCalibrationShareCode may hand to the browser.
    static bool isShareIssueUrl(const QUrl &url);

public Q_SLOTS:
    /// Adopt one $defs/CaptureStatus. A payload without calibration.shared
    /// resets this controller to "none".
    void refreshFromCaptureStatus(const QVariantMap &capture);
    /// 立即检查.
    void checkNow();
    /// 分享给其他玩家: GetCalibrationShareCode, then clipboard and browser.
    void share();
    /// 导入校准码. Surrounding whitespace is dropped; empty text is never sent.
    void importCode(const QString &text);
    /// Consent to queue inference once for this build.
    void acceptQueueInference();
    /// 不用共享的，我自己校准.
    void reject();

Q_SIGNALS:
    void changed();
    /// One sentence for the toast.
    void notice(const QString &message);
    /// Shared state changed on the Collector side; re-read the capture status.
    void refreshRequested();
    /// ImportCalibrationCode answered. \a applied closes the dialog; otherwise
    /// \a message is the Collector's own sentence, shown as it is.
    void importFinished(bool applied, const QString &message);

private:
    struct Inputs
    {
        bool available = false;
        bool userRejected = false;
        bool manualOnly = false;
        bool auditPending = false;
        /// "published", "imported" or empty when no candidate reports a provenance.
        QString provenance;
        QString phase;
        QString lastFetchStatus;
        QString calibrationState = QStringLiteral("IDLE");
        QString profileOrigin;

        bool operator==(const Inputs &) const = default;
    };

    bool calibrating() const;
    bool begin();
    void end();
    void deliverShareCode(const QVariantMap &payload);
    void explainShareFailure(const QString &code, const QString &message,
                             const QVariantMap &details);

    QPointer<IBackend> m_backend;
    UrlOpener m_openUrl;
    ClipboardWriter m_copy;
    Inputs m_inputs;
    bool m_busy = false;
};

} // namespace mr
