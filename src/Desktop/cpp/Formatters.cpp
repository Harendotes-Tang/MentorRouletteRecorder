#include "Formatters.h"

#include "CollectorText.h"
#include "GameNames.h"

#include <QDateTime>
#include <QJSValue>
#include <QDir>
#include <QHash>
#include <QLocale>
#include <QRegularExpression>
#include <QStandardPaths>
#include <QStringList>
#include <QTimeZone>

namespace mr {

Formatters::Formatters(QObject *parent) : QObject(parent) {}

QDateTime Formatters::parse(const QVariant &utcIso)
{
    if (!utcIso.isValid() || utcIso.isNull())
        return {};
    const QString text = utcIso.toString();
    if (text.isEmpty())
        return {};

    QDateTime dt = QDateTime::fromString(text, Qt::ISODateWithMs);
    if (!dt.isValid())
        dt = QDateTime::fromString(text, Qt::ISODate);
    if (!dt.isValid())
        return {};
    if (dt.timeSpec() == Qt::LocalTime)
        dt.setTimeZone(QTimeZone::UTC);
    return dt.toLocalTime();
}

QString Formatters::localTime(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    return dt.isValid() ? dt.toString(QStringLiteral("HH:mm:ss")) : dash();
}

QString Formatters::localDate(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    return dt.isValid() ? dt.toString(QStringLiteral("yyyy-MM-dd")) : dash();
}

QString Formatters::localDateTime(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    return dt.isValid() ? dt.toString(QStringLiteral("yyyy-MM-dd HH:mm:ss")) : dash();
}

QString Formatters::dateWithWeekday(const QVariant &utcIso)
{
    const QDateTime dt = parse(utcIso);
    if (!dt.isValid())
        return dash();
    // Translated at the point of use, so the weekday follows the interface
    // language while the date itself stays yyyy-MM-dd in every language.
    static const char *const kWeekdays[] = {
        QT_TR_NOOP("周一"), QT_TR_NOOP("周二"), QT_TR_NOOP("周三"), QT_TR_NOOP("周四"),
        QT_TR_NOOP("周五"), QT_TR_NOOP("周六"), QT_TR_NOOP("周日")
    };
    const int index = dt.date().dayOfWeek() - 1; // 1..7 -> 0..6
    const QString weekday = (index >= 0 && index < 7)
                                ? tr(kWeekdays[index])
                                : QString();
    return dt.toString(QStringLiteral("yyyy-MM-dd")) + QLatin1Char(' ') + weekday;
}

QString Formatters::duration(const QVariant &milliseconds)
{
    if (!milliseconds.isValid() || milliseconds.isNull())
        return dash();
    bool okConversion = false;
    const double ms = milliseconds.toDouble(&okConversion);
    if (!okConversion || ms < 0)
        return dash();

    const qint64 totalSeconds = static_cast<qint64>(ms / 1000.0);
    const qint64 hours = totalSeconds / 3600;
    const qint64 minutes = (totalSeconds % 3600) / 60;
    const qint64 seconds = totalSeconds % 60;

    if (hours > 0) {
        return QStringLiteral("%1:%2:%3")
            .arg(hours)
            .arg(minutes, 2, 10, QLatin1Char('0'))
            .arg(seconds, 2, 10, QLatin1Char('0'));
    }
    return QStringLiteral("%1:%2")
        .arg(totalSeconds / 60, 2, 10, QLatin1Char('0'))
        .arg(seconds, 2, 10, QLatin1Char('0'));
}

QString Formatters::percent(const QVariant &ratio, int decimals)
{
    if (!ratio.isValid() || ratio.isNull())
        return dash();
    bool okConversion = false;
    const double value = ratio.toDouble(&okConversion);
    if (!okConversion)
        return dash();
    return QString::number(value * 100.0, 'f', decimals) + QLatin1Char('%');
}

QString Formatters::count(const QVariant &value)
{
    if (!value.isValid() || value.isNull())
        return dash();
    bool okConversion = false;
    const qlonglong number = value.toLongLong(&okConversion);
    if (!okConversion)
        return dash();
    return QString::number(number);
}

QString Formatters::gameVersionLabel(const QVariant &build)
{
    if (!build.isValid() || build.isNull())
        return dash();
    const QString text = build.toString().trimmed();
    if (text.isEmpty())
        return dash();
    // 2026.09.01.0000.0000 -> 2026.09.01. Anything else is shown as it is: a
    // version this build does not recognise is still the truth, and inventing
    // an em dash for it would hide the one fact the player asked for.
    const QStringList parts = text.split(QLatin1Char('.'));
    if (parts.size() < 4)
        return text;
    for (int index = 0; index < 3; ++index) {
        bool numeric = false;
        parts.at(index).toInt(&numeric);
        if (!numeric || parts.at(index).isEmpty())
            return text;
    }
    return parts.mid(0, 3).join(QLatin1Char('.'));
}

QString Formatters::npcapVersionLabel(const QVariant &version)
{
    if (!version.isValid() || version.isNull())
        return {};
    const QString text = version.toString().trimmed();
    if (text.isEmpty())
        return {};
    return text.front().isDigit() ? QStringLiteral("v") + text : text;
}

QString Formatters::resultLabel(const QString &code)
{
    if (code == QLatin1String("COMPLETED"))              return tr("通关");
    if (code == QLatin1String("LEFT_OR_ABANDONED"))      return tr("退出/放弃");
    if (code == QLatin1String("CANCELLED_BEFORE_ENTRY")) return tr("进本前取消");
    if (code == QLatin1String("DISCONNECTED"))           return tr("断线");
    if (code == QLatin1String("INTERRUPTED"))            return tr("中断");
    return tr("未知");
}

QString Formatters::joinedInProgressLabel(const QVariant &value)
{
    if (!value.isValid() || value.isNull() || value.typeId() != QMetaType::Bool)
        return tr("未知");
    return value.toBool() ? tr("中途加入") : tr("开局进入");
}

bool Formatters::runInProgress(const QVariantMap &run)
{
    const QString source = run.value(QStringLiteral("source")).toString();
    return (source.isEmpty() || source == QLatin1String("AUTO_NETWORK"))
        && run.value(QStringLiteral("result"), QStringLiteral("UNKNOWN")).toString() == QLatin1String("UNKNOWN")
        && run.value(QStringLiteral("ended_at_utc")).toString().isEmpty()
        // An unfinished run crash recovery handed over for review is over, not in progress.
        && !run.value(QStringLiteral("pending_review")).toBool()
        && (!run.value(QStringLiteral("entered_at_utc")).toString().isEmpty()
            || !run.value(QStringLiteral("matched_at_utc")).toString().isEmpty());
}

QString Formatters::runResultLabel(const QVariantMap &run)
{
    if (runInProgress(run))
        return tr("进行中");
    return resultLabel(run.value(QStringLiteral("result"), QStringLiteral("UNKNOWN")).toString());
}

QString Formatters::resultColorToken(const QString &code)
{
    if (code == QLatin1String("COMPLETED"))              return QStringLiteral("green");
    if (code == QLatin1String("LEFT_OR_ABANDONED"))      return QStringLiteral("orange");
    if (code == QLatin1String("CANCELLED_BEFORE_ENTRY")) return QStringLiteral("yellow");
    if (code == QLatin1String("DISCONNECTED"))           return QStringLiteral("red");
    if (code == QLatin1String("INTERRUPTED"))            return QStringLiteral("neutral500");
    return QStringLiteral("neutral400");
}

QString Formatters::resultTagVariant(const QString &code)
{
    if (code == QLatin1String("COMPLETED"))              return QStringLiteral("ink");
    if (code == QLatin1String("LEFT_OR_ABANDONED"))      return QStringLiteral("accent");
    if (code == QLatin1String("CANCELLED_BEFORE_ENTRY")) return QStringLiteral("outline");
    return QStringLiteral("neutral");
}

QString Formatters::sourceLabel(const QString &code)
{
    if (code == QLatin1String("MANUAL")) return tr("手动");
    if (code == QLatin1String("IMPORT")) return tr("导入");
    return tr("自动");
}

QString Formatters::stateLabel(const QString &code)
{
    if (code == QLatin1String("IDLE"))           return tr("空闲 · 等待匹配");
    if (code == QLatin1String("MENTOR_MATCHED")) return tr("已匹配导随");
    if (code == QLatin1String("ENTERED_DUTY"))   return tr("已进入副本");
    if (code == QLatin1String("INTERRUPTED_PENDING_REVIEW"))
        return tr("中断 · 待复核");
    // The final states share the RunResult vocabulary; UNKNOWN_FINAL_STATE and
    // anything newer read 未知 rather than the raw token.
    return resultLabel(code);
}

QString Formatters::runConfidenceLabel(const QVariantMap &run)
{
    if (runInProgress(run))
        return tr("结束时评定");
    return confidenceLabel(run.value(QStringLiteral("detection_confidence")).toString());
}

QString Formatters::confidenceLabel(const QString &code)
{
    if (code.isEmpty())                  return dash();
    if (code == QLatin1String("HIGH"))   return tr("高");
    if (code == QLatin1String("MEDIUM")) return tr("中");
    if (code == QLatin1String("LOW"))    return tr("低");
    if (code == QLatin1String("NONE"))   return tr("无");
    return tr("未知");
}

QString Formatters::roleLabel(const QString &code)
{
    if (code == QLatin1String("TANK"))   return tr("坦克");
    if (code == QLatin1String("HEALER")) return tr("治疗");
    if (code == QLatin1String("DPS"))    return tr("输出");
    return tr("未知");
}

QString Formatters::roleColorToken(const QString &code)
{
    if (code == QLatin1String("TANK"))   return QStringLiteral("blue");
    if (code == QLatin1String("HEALER")) return QStringLiteral("green");
    if (code == QLatin1String("DPS"))    return QStringLiteral("red");
    return QStringLiteral("neutral400");
}

namespace {
/// The capture failures a player can actually do something about. Anything
/// missing from this table is deliberately answered with a generic sentence
/// rather than with the raw token: an ERR_ code is not an instruction.
// Source texts only: Formatters::tr() translates them where they are read, so the
// sentence follows the interface language.
const QHash<QString, const char *> &captureErrorTable()
{
    static const QHash<QString, const char *> kTable{
        {QStringLiteral("UNAVAILABLE"),
         QT_TRANSLATE_NOOP("mr::Formatters", "自动记录暂时无法启动，请检查 Npcap 驱动和网卡是否正常。")},
        {QStringLiteral("ERR_NPCAP_MISSING"),
         QT_TRANSLATE_NOOP("mr::Formatters", "没有检测到 Npcap 驱动，装好之后回到捕获诊断页点“重新检测”。")},
        {QStringLiteral("ERR_FFXIV_NOT_RUNNING"),
         QT_TRANSLATE_NOOP("mr::Formatters", "没有检测到正在运行的游戏。")},
        {QStringLiteral("ERR_CAPTURE_ALREADY_RUNNING"),
         QT_TRANSLATE_NOOP("mr::Formatters", "游戏数据正在读取中，请先停止，再重新开始。")},
        {QStringLiteral("ERR_CAPTURE_NOT_RUNNING"),
         QT_TRANSLATE_NOOP("mr::Formatters", "监听没有在运行。")},
        {QStringLiteral("ERR_PROFILE_UNSUPPORTED"),
         QT_TRANSLATE_NOOP("mr::Formatters", "当前游戏版本暂不支持自动记录。")},
        {QStringLiteral("ERR_DB_BUSY"),
         QT_TRANSLATE_NOOP("mr::Formatters", "记录数据库正在使用中，软件会自动重试，请稍候。")},
        // 采集服务在启动时遇到这个码就拒绝启动，管道从不打开，这句话因而到不了界面；
        // 留着是为了防御，措辞必须仍然属实：此时设置里的备份也用不了，只能自己去复制
        // 数据库文件（审查 2026-09-21 第 15 条）。
        {QStringLiteral("ERR_DB_INTEGRITY"),
         QT_TRANSLATE_NOOP("mr::Formatters", "记录数据库可能已损坏，后台服务无法启动。请先复制数据库文件保留一份，再联系维护者排查。")},
        // Also the answer to a capture start that failed for a reason other than
        // Npcap (review OA-7), so it must not claim a capture that never began.
        {QStringLiteral("ERR_INTERNAL"),
         QT_TRANSLATE_NOOP("mr::Formatters", "自动记录服务出现错误，本次记录未开始或已经停止。")},
    };
    return kTable;
}
} // namespace

bool Formatters::captureErrorKnown(const QString &code)
{
    return captureErrorTable().contains(code);
}

QString Formatters::captureErrorLabel(const QString &code)
{
    const char *source = captureErrorTable().value(code);
    return source ? tr(source) : tr("自动记录服务遇到了暂时无法识别的问题，请导出诊断报告。");
}

QString Formatters::eventKindLabel(const QString &code)
{
    // ipc-v1 $defs/CaptureStatus.last_valid_event_kind. ZONE_INITIALIZATION is
    // every zone load, not only a duty entry, so it is named for what it is.
    // MATCH_ANNOUNCED joined the enum on 2026-09-19 (contracts/CHANGELOG.md):
    // it is the server announcing that the match is made, not the finder message
    // CONTENT_FINDER_POP stands for, so it is named apart from it. Without a name
    // of its own the capture page would show the time alone (2026-09-21 audit,
    // finding 20).
    static const QHash<QString, const char *> kLabels{
        {QStringLiteral("CONTENT_FINDER_POP"), QT_TR_NOOP("匹配成功")},
        {QStringLiteral("ZONE_INITIALIZATION"), QT_TR_NOOP("进入区域")},
        {QStringLiteral("ZONE_TERRITORY"), QT_TR_NOOP("识别所在区域")},
        {QStringLiteral("DUTY_RESULT"), QT_TR_NOOP("副本结算")},
        {QStringLiteral("PLAYER_JOB"), QT_TR_NOOP("识别职业")},
        {QStringLiteral("ZONE_LEFT"), QT_TR_NOOP("离开副本区域")},
        {QStringLiteral("INSTANCE_LEFT"), QT_TR_NOOP("退出副本")},
        {QStringLiteral("MATCH_CANCELLED"), QT_TR_NOOP("匹配取消")},
        {QStringLiteral("MATCH_ANNOUNCED"), QT_TR_NOOP("匹配成功通知")},
        {QStringLiteral("DUTY_COMMENCED"), QT_TR_NOOP("副本开始")},
        {QStringLiteral("DUTY_TIME_REMAINING"), QT_TR_NOOP("副本剩余时间")},
    };
    const char *source = kLabels.value(code);
    return source ? tr(source) : QString();
}

QString Formatters::parserErrorLabel(const QString &code)
{
    // ipc-v1 $defs/ParserErrorEntry.code. Every refusal ends the same way for
    // the player: the message is dropped and nothing is recorded from it.
    static const QHash<QString, const char *> kLabels{
        {QStringLiteral("E_UNKNOWN_OPCODE"), QT_TR_NOOP("暂时无法识别这条游戏数据，已跳过")},
        {QStringLiteral("E_LEN_MISMATCH"), QT_TR_NOOP("游戏数据的格式不符合预期，已跳过")},
        {QStringLiteral("E_OFFSET_OOB"), QT_TR_NOOP("这条游戏数据不完整，已跳过")},
        {QStringLiteral("E_FIELD_CONSTRAINT"), QT_TR_NOOP("这条游戏数据的内容不符合预期，已跳过")},
        {QStringLiteral("E_PROFILE_UNSUPPORTED"), QT_TR_NOOP("当前游戏版本暂不支持，未读取")},
        {QStringLiteral("E_INTERNAL"), QT_TR_NOOP("读取这条游戏数据时出错，已跳过")},
    };
    const char *source = kLabels.value(code);
    return source ? tr(source) : tr("这条游戏数据没能解析，已忽略");
}

QString Formatters::foldUserPath(const QString &path)
{
    if (path.isEmpty())
        return path;

    QString folded = QDir::fromNativeSeparators(path);
    const QString home =
        QDir::fromNativeSeparators(QStandardPaths::writableLocation(QStandardPaths::HomeLocation));
    if (!home.isEmpty() && folded.startsWith(home, Qt::CaseInsensitive)) {
        folded = QStringLiteral("%USERPROFILE%") + folded.mid(home.size());
    } else {
        // The profile root of *another* account, or of this one under a name
        // QStandardPaths does not report (a redirected profile). Fold the user
        // name out of the well-known Windows layout rather than printing it.
        static const QRegularExpression users(
            QStringLiteral("^([A-Za-z]:)?/Users/[^/]+"),
            QRegularExpression::CaseInsensitiveOption);
        folded.replace(users, QStringLiteral("%USERPROFILE%"));
    }
    return QDir::toNativeSeparators(folded);
}

QString Formatters::fieldLabel(const QString &field)
{
    static const QHash<QString, const char *> kLabels{
        {QStringLiteral("result"), QT_TR_NOOP("结果")},
        {QStringLiteral("duty_name"), QT_TR_NOOP("副本")},
        {QStringLiteral("duty_category"), QT_TR_NOOP("类型")},
        {QStringLiteral("job_id"), QT_TR_NOOP("职业")},
        {QStringLiteral("job_name"), QT_TR_NOOP("职业")},
        {QStringLiteral("role"), QT_TR_NOOP("职能")},
        {QStringLiteral("detection_confidence"), QT_TR_NOOP("识别可信度")},
        {QStringLiteral("source"), QT_TR_NOOP("来源")},
        {QStringLiteral("matched_at_utc"), QT_TR_NOOP("匹配时间")},
        {QStringLiteral("entered_at_utc"), QT_TR_NOOP("进本时间")},
        {QStringLiteral("ended_at_utc"), QT_TR_NOOP("结束时间")},
        {QStringLiteral("duration_ms"), QT_TR_NOOP("耗时")},
        {QStringLiteral("contributes_to_goal"), QT_TR_NOOP("成就计数设置")},
        {QStringLiteral("joined_in_progress"), QT_TR_NOOP("进入方式")},
        {QStringLiteral("note"), QT_TR_NOOP("备注")},
        {QStringLiteral("soft_deleted"), QT_TR_NOOP("删除状态")},
        {QStringLiteral("pending_review"), QT_TR_NOOP("待复核")},
    };
    const char *source = kLabels.value(field);
    return source ? tr(source) : field;
}

QString Formatters::revisionValue(const QVariant &value)
{
    if (!value.isValid() || value.isNull())
        return tr("空");
    if (value.typeId() == QMetaType::Bool)
        return value.toBool() ? tr("是") : tr("否");
    const QString text = value.toString();
    if (text.isEmpty())
        return tr("空");
    // Timestamps are stored as UTC ISO-8601 and read in local time everywhere
    // else in this UI; the audit trail must not be the one exception.
    if (text.size() >= 20 && text.contains(QLatin1Char('T'))
        && text.endsWith(QLatin1Char('Z'))) {
        const QString local = localDateTime(text);
        if (local != dash())
            return local;
    }
    return text;
}

bool Formatters::revisionFieldVisible(const QString &field)
{
    // What a record says, as 详情 shows it. Revision 1 also lists the run's
    // identifiers and bookkeeping (SemanticEventProcessor.DescribeCreation,
    // RunMutationService.InitialChanges); job_id and content_id travel with the
    // job_name / duty_name rows that name the same change.
    static const QStringList kShown{
        QStringLiteral("result"),          QStringLiteral("duty_name"),
        QStringLiteral("duty_category"),   QStringLiteral("job_name"),
        QStringLiteral("role"),            QStringLiteral("matched_at_utc"),
        QStringLiteral("entered_at_utc"),  QStringLiteral("ended_at_utc"),
        QStringLiteral("duration_ms"),     QStringLiteral("contributes_to_goal"),
        QStringLiteral("joined_in_progress"),
        QStringLiteral("note"),            QStringLiteral("soft_deleted"),
        QStringLiteral("pending_review"),  QStringLiteral("detection_confidence"),
        QStringLiteral("source"),
    };
    return kShown.contains(field);
}

QString Formatters::revisionFieldValue(const QString &field, const QVariant &value)
{
    // Null is a value of its own here: "not known", not "empty".
    if (field == QLatin1String("joined_in_progress"))
        return joinedInProgressLabel(value);
    if (!value.isValid() || value.isNull() || value.toString().isEmpty())
        return revisionValue(value);
    if (field == QLatin1String("result"))
        return resultLabel(value.toString());
    if (field == QLatin1String("role"))
        return roleLabel(value.toString());
    if (field == QLatin1String("source"))
        return sourceLabel(value.toString());
    if (field == QLatin1String("detection_confidence"))
        return confidenceLabel(value.toString());
    if (field == QLatin1String("duration_ms"))
        return duration(value);
    // Game names are recorded in Chinese; the log shows them in the interface
    // language, found by that recorded name. The Collector's placeholders for
    // an unknown duty / job (DutyCatalog.UnknownDutyName, JobCatalog.UnknownJobName)
    // are this program's words.
    if (field == QLatin1String("duty_name")) {
        const QString name = value.toString();
        return name == QString::fromUtf8("未知副本") ? tr("未知副本")
                                                     : GameNames::shared()->duty(QVariant(), name);
    }
    if (field == QLatin1String("job_name")) {
        const QString name = value.toString();
        return name == QString::fromUtf8("未知") ? tr("未知")
                                                 : GameNames::shared()->job(QVariant(), name);
    }
    if (field == QLatin1String("duty_category"))
        return GameNames::shared()->category(value.toString());
    return revisionValue(value);
}

QString Formatters::changeKindLabel(const QString &code)
{
    // $defs/RunRevision.change_kind. An undo is written as a CORRECT revision.
    static const QHash<QString, const char *> kLabels{
        {QStringLiteral("CREATE_AUTO"), QT_TR_NOOP("自动记录")},
        {QStringLiteral("CREATE_MANUAL"), QT_TR_NOOP("手动新增")},
        {QStringLiteral("CORRECT"), QT_TR_NOOP("修正")},
        {QStringLiteral("SOFT_DELETE"), QT_TR_NOOP("删除记录")},
        {QStringLiteral("RESTORE"), QT_TR_NOOP("恢复")},
        {QStringLiteral("IMPORT"), QT_TR_NOOP("导入")},
    };
    const char *source = kLabels.value(code);
    return source ? tr(source) : tr("修改");
}

QString Formatters::revisionActorLabel(const QString &code)
{
    return code == QLatin1String("SYSTEM") ? tr("软件自动")
                                           : tr("用户操作");
}

QString Formatters::runEventLabel(const QString &code)
{
    // The capture page's names where the two vocabularies meet, then the
    // events the Collector writes itself (Domain/Events/SemanticEvent.cs,
    // crash recovery's PROCESS_RESTART).
    const QString shared = eventKindLabel(code);
    if (!shared.isEmpty())
        return shared;
    static const QHash<QString, const char *> kLabels{
        {QStringLiteral("CONNECTION_LOST"), QT_TR_NOOP("游戏连接中断")},
        {QStringLiteral("CAPTURE_STOPPED"), QT_TR_NOOP("监听停止")},
        {QStringLiteral("EVENT_SEQUENCE_GAP"), QT_TR_NOOP("部分游戏数据来不及处理")},
        {QStringLiteral("TIMEOUT_TICK"), QT_TR_NOOP("等待超时")},
        {QStringLiteral("PROCESS_RESTART"), QT_TR_NOOP("软件重启后收尾")},
        {QStringLiteral("PROFILE_LOST"), QT_TR_NOOP("当前游戏版本的支持文件已失效")},
    };
    const char *source = kLabels.value(code);
    return source ? tr(source) : tr("其他事件");
}

QString Formatters::oodleModeLabel(const QString &code)
{
    // ICaptureSource.OodleMode, sent by name (CaptureWire / CaptureDiagnostics).
    if (code == QLatin1String("FfxivTcp"))
        return tr("游戏自带的解压函数");
    if (code == QLatin1String("LibraryTcp"))
        return tr("用户提供的解压库");
    return code.isEmpty() ? dash() : tr("未知方式");
}

QString Formatters::collectorText(const QVariant &ref, const QString &chinese)
{
    // A reference read from a QML property can arrive as a JavaScript object:
    // the renderer reads plain maps, nested ones included.
    if (ref.metaType() == QMetaType::fromType<QJSValue>())
        return collector_text::render(
            ref.value<QJSValue>().toVariant(QJSValue::ConvertJSObjects), chinese);
    return collector_text::render(ref, chinese);
}

QStringList Formatters::resultCodes()
{
    return {
        QStringLiteral("COMPLETED"),
        QStringLiteral("LEFT_OR_ABANDONED"),
        QStringLiteral("CANCELLED_BEFORE_ENTRY"),
        QStringLiteral("DISCONNECTED"),
        QStringLiteral("INTERRUPTED"),
        QStringLiteral("UNKNOWN"),
    };
}

} // namespace mr
