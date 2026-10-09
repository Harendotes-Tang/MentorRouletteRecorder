#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Negative self-test for the static hard-boundary checker.

``check.py`` passing proves nothing on its own: a checker that never matches
anything also passes. This script proves the opposite direction. It plants each
forbidden token in a throwaway repository tree and asserts that ``check.py``
*fails* on it, that it names the right rule, that it looks in every place the
boundary has to be enforced, that the ``BOUNDARY-ALLOW(<RULE-ID>): <reason>``
escape hatch lifts exactly one hit of one rule on one line and only the hit ``rules.json`` pins,
that a scan which could not read everything fails instead of passing, and - by
removing each alternative of each pattern in turn - that every spelling a pattern
lists is needed by at least one planted sample.

This file lives inside ``tools/static-boundary-check/``, which ``rules.json``
excludes from the scan, so the samples below never trip the real run.

Exit codes
----------
0   every expectation held
1   at least one expectation failed

Usage
-----
    python tools/static-boundary-check/selftest.py [-v]
"""

from __future__ import annotations

import argparse
import contextlib
import io
import json
import os
import re
import subprocess
import sys
import tempfile
from pathlib import Path
from typing import Iterable
from unittest import mock

HERE = Path(__file__).resolve().parent
CHECK_PY = HERE / "check.py"
RULES_PATH = HERE / "rules.json"
README_PATH = HERE / "README.md"
DEFAULT_SITE = "src/Collector/Capture/Sample.cs"

EXIT_OK = 0
EXIT_VIOLATION = 1
EXIT_ERROR = 2

# One line per rule that must be rejected. The key is the rule id declared in
# rules.json, or "id/variant" for a rule id declared more than once; the value is
# a snippet a careless change could plausibly produce. Every rule entry in
# rules.json must appear here, so adding a rule without a negative test fails
# this script.
SAMPLES: dict[str, str] = {
    "INJ-001": "        var ok = ReadProcessMemory(handle, address, buffer, size, out var read);",
    "INJ-002": "        var ok = WriteProcessMemory(handle, address, buffer, size, out var written);",
    "INJ-003": "        var remote = VirtualAllocEx(handle, IntPtr.Zero, 4096, Commit, ReadWrite);",
    "INJ-004": "        var thread = CreateRemoteThread(handle, IntPtr.Zero, 0, start, arg, 0, out _);",
    "INJ-005": "        var hook = SetWindowsHookExW(WhKeyboardLl, callback, module, 0);",
    "INJ-006": "        NtWriteVirtualMemory(handle, address, buffer, size, out _);",
    "INJ-007": "        using var game = OpenProcess(ProcessVmRead, false, processId);",
    "INJ-008": "        var path = process.MainModule?.FileName;",
    "INJ-009": "        started = new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);",
    "DEU-001": "        monitor.UseDeucalion = true;",
    "DEU-002": "        var client = new DeucalionClient(processId);",
    "DEU-003": '        var payload = "deucalion-1.2.3.dll";',
    "DEU-004": "        InjectLibrary(processId, payloadPath);",
    "CAP-001": "        pcap_sendpacket(handle, frame, frame.Length);",
    "CAP-002": "        pcap_inject(handle, frame, frame.Length);",
    "CAP-003": "        monitor.MonitorType = NetworkMonitorType.RawSocket;",
    "CAP-004": "        var socket = new RawCaptureSocket(adapterAddress);",
    "CAP-005": "        pcap_dump_open(handle, capturePath);",
    "CAP-006": '        [DllImport("wpcap.dll")] static extern int pcap_open_live();',
    "NET-001": "        var listener = new HttpListener();",
    "NET-002": "        var listener = new TcpListener(IPAddress.Loopback, 8080);",
    "NET-003": "        var socket = new ClientWebSocket();",
    "NET-004": "        using Microsoft.AspNetCore.Builder;",
    "NET-005": "        auto *server = new QTcpServer(this);",
    "NET-006": "        using var client = new HttpClient();",
    "NET-007/shared-calibration-hosts": '        const string Mirror = "https://cdn.jsdelivr.net/gh/owner/repo@main/index.json";',
    "NET-007/github-content-hosts": '        const string Raw = "https://raw.githubusercontent.com/owner/repo/main/index.json";',
    "NET-007/speech-host": '        const string Endpoint = "https://eastasia.tts.speech.microsoft.com/cognitiveservices/v1";',
    "NET-008": '        using var client = new TcpClient("example.invalid", 443);',
    "NET-009/downloads": "        Invoke-WebRequest -Uri $url -OutFile $target",
    "NET-009/qml-remote": '        banner.source = "https://example.invalid/banner.png"',
    "NET-009/shell-commands": "curl -fsSL $url -o $target",
    "NET-009/xml-loads": '        var feed = XDocument.Load("https://example.invalid/feed.xml");',
    "NET-010": "import urllib.request",
    "AUT-001": "        SendInput(1, ref input, Marshal.SizeOf(input));",
}

# The file a SAMPLES line is planted in, for a rule entry that reads only some file types
# (its 'only_extensions'); every other sample goes to DEFAULT_SITE.
SAMPLE_SITES: dict[str, str] = {
    "NET-009/qml-remote": "src/Desktop/qml/Sample.qml",
    "NET-009/shell-commands": "scripts/sample.ps1",
    "NET-010": "tools/some-tool/sample.py",
}

# Every further alternative of a rule's pattern, each with the rule key that must report it and
# the file it is planted in. SAMPLES proves each rule fires at all; this proves each spelling it
# claims to catch is caught. The mutation case below goes further and removes each alternative
# of each pattern in turn: some planted sample must then stop being reported, so a spelling with
# no row here fails the self-test (audit 2026-10-03: ON2-1..ON2-4, R2T-2, R2T-3).
CPP_SITE = "src/Desktop/cpp/Sample.cpp"
PS1_SITE = "scripts/sample.ps1"
QML_SITE = "src/Desktop/qml/Sample.qml"
PY_SITE = "tools/some-tool/sample.py"
MORE_SAMPLES: tuple[tuple[str, str, str], ...] = (
    ("INJ-001", "        Toolhelp32ReadProcessMemory(processId, address, buffer, size, out _);", DEFAULT_SITE),
    ("INJ-001", "        NtReadVirtualMemory(handle, address, buffer, size, out _);", DEFAULT_SITE),
    ("INJ-001", "        ZwReadVirtualMemory(handle, address, buffer, size, out _);", DEFAULT_SITE),
    ("INJ-001", "        NtWow64ReadVirtualMemory64(handle, address, buffer, size, out _);", DEFAULT_SITE),
    ("INJ-001", "        MiniDumpWriteDump(handle, processId, file, type, 0, 0, 0);", DEFAULT_SITE),
    ("INJ-003", "        var remote = VirtualAllocExNuma(handle, 0, 4096, Commit, ReadWrite, 0);", DEFAULT_SITE),
    ("INJ-003", "        var remote = VirtualAlloc2(handle, 0, 4096, Commit, ReadWrite, 0, 0);", DEFAULT_SITE),
    ("INJ-003", "        VirtualProtectEx(handle, address, size, ExecuteReadWrite, out _);", DEFAULT_SITE),
    ("INJ-003", "        NtAllocateVirtualMemory(handle, ref address, 0, ref size, Commit, ReadWrite);", DEFAULT_SITE),
    ("INJ-003", "        ZwProtectVirtualMemory(handle, ref address, ref size, ExecuteRead, out _);", DEFAULT_SITE),
    ("INJ-004", "        var thread = CreateRemoteThreadEx(handle, 0, 0, start, arg, 0, 0, out _);", DEFAULT_SITE),
    ("INJ-006", "        ZwWriteVirtualMemory(handle, address, buffer, size, out _);", DEFAULT_SITE),
    ("INJ-006", "        NtWow64WriteVirtualMemory64(handle, address, buffer, size, out _);", DEFAULT_SITE),
    ("INJ-006", "        NtCreateThreadEx(out thread, access, 0, handle, start, arg, 0, 0, 0, 0, 0);", DEFAULT_SITE),
    ("INJ-006", "        ZwCreateThreadEx(out thread, access, 0, handle, start, arg, 0, 0, 0, 0, 0);", DEFAULT_SITE),
    ("INJ-006", "        RtlCreateUserThread(handle, 0, false, 0, 0, 0, start, arg, out _, out _);", DEFAULT_SITE),
    ("INJ-006", "        QueueUserAPC(routine, thread, 0);", DEFAULT_SITE),
    ("INJ-006", "        QueueUserAPC2(routine, thread, 0, SpecialUserApc);", DEFAULT_SITE),
    ("INJ-006", "        NtQueueApcThread(thread, routine, arg, 0, 0);", DEFAULT_SITE),
    ("INJ-006", "        ZwQueueApcThreadEx(thread, 0, routine, arg, 0, 0);", DEFAULT_SITE),
    ("INJ-007", "        NtOpenProcess(out handle, access, ref attributes, ref client);", DEFAULT_SITE),
    ("INJ-007", "        ZwOpenProcess(out handle, access, ref attributes, ref client);", DEFAULT_SITE),
    ("INJ-007", "        DebugActiveProcess(processId);", DEFAULT_SITE),
    ("INJ-007", "        Process.EnterDebugMode();", DEFAULT_SITE),
    # V5-2: a process handle from a window, from the kernel's process walk, and a thread handle.
    ("INJ-007", "        var game = GetProcessHandleFromHwnd(window);", DEFAULT_SITE),
    ("INJ-007", "        NtGetNextProcess(previous, access, 0, 0, out var next);", DEFAULT_SITE),
    ("INJ-007", "        ZwGetNextProcess(previous, access, 0, 0, out var next);", DEFAULT_SITE),
    ("INJ-007", "        using var thread = OpenThread(ThreadGetContext, false, threadId);", DEFAULT_SITE),
    ("INJ-007", "        NtOpenThread(out var thread, access, ref attributes, ref client);", DEFAULT_SITE),
    ("INJ-007", "        ZwOpenThread(out var thread, access, ref attributes, ref client);", DEFAULT_SITE),
    ("INJ-008", "        foreach (ProcessModule module in process.Modules) { }", DEFAULT_SITE),
    # V5-2: whitespace after the dot, and C# property patterns, on one line or one per line.
    ("INJ-008", "        var path = process . MainModule?.FileName;", DEFAULT_SITE),
    ("INJ-008", "        if (process is { MainModule.FileName: var path }) { }", DEFAULT_SITE),
    ("INJ-008", "        if (process is { Modules: var all }) { }", DEFAULT_SITE),
    ("INJ-008", "            MainModule: { FileName: var path },", DEFAULT_SITE),
    ("INJ-009", "        var ended = process.ExitTime;", DEFAULT_SITE),
    ("INJ-009", "        var raw = process.Handle;", DEFAULT_SITE),
    ("INJ-009", "        using var safe = process.SafeHandle;", DEFAULT_SITE),
    ("INJ-009", "        if (process.HasExited) return;", DEFAULT_SITE),
    ("INJ-009", "        var code = process.ExitCode;", DEFAULT_SITE),
    ("INJ-009", "        process.EnableRaisingEvents = true;", DEFAULT_SITE),
    ("INJ-009", "        var priority = process.PriorityClass;", DEFAULT_SITE),
    ("INJ-009", "        var boost = process.PriorityBoostEnabled;", DEFAULT_SITE),
    ("INJ-009", "        var mask = process.ProcessorAffinity;", DEFAULT_SITE),
    ("INJ-009", "        var cpu = process.TotalProcessorTime;", DEFAULT_SITE),
    ("INJ-009", "        var user = process.UserProcessorTime;", DEFAULT_SITE),
    ("INJ-009", "        var kernel = process.PrivilegedProcessorTime;", DEFAULT_SITE),
    ("INJ-009", "        var most = process.MaxWorkingSet;", DEFAULT_SITE),
    ("INJ-009", "        var least = process.MinWorkingSet;", DEFAULT_SITE),
    ("INJ-009", "        process.WaitForExit(1000);", DEFAULT_SITE),
    ("INJ-009", "        await process.WaitForExitAsync(token);", DEFAULT_SITE),
    ("INJ-009", "        process.WaitForInputIdle();", DEFAULT_SITE),
    ("INJ-009", "        process.Kill();", DEFAULT_SITE),
    ("INJ-009", "        using var game = Process.GetProcessById(processId);", DEFAULT_SITE),
    ("INJ-009", "        $started = $game.StartTime", "scripts/sample.ps1"),
    # V5-2: whitespace after the dot, property patterns (after '{' or ',', or one per line) and
    # object initialisers name the same members without a dot in front of them.
    ("INJ-009", "        var started = game . StartTime;", DEFAULT_SITE),
    ("INJ-009", "        if (game is { StartTime: var started }) { }", DEFAULT_SITE),
    ("INJ-009", "            HasExited: false,", DEFAULT_SITE),
    ("INJ-009", "        if (game is { Id: 7, ExitTime: var ended }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { SafeHandle.IsInvalid: false }) { }", DEFAULT_SITE),
    ("INJ-009", "        using var game = new Process { EnableRaisingEvents = true };", DEFAULT_SITE),
    ("INJ-009", "        if (game is { PriorityClass: var priority }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { PriorityBoostEnabled: true }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { ProcessorAffinity: var mask }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { TotalProcessorTime: var cpu }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { MaxWorkingSet: var most }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { MinWorkingSet: var least }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { Handle: var raw }) { }", DEFAULT_SITE),
    ("INJ-009", "        if (game is { ExitCode: 0 }) { }", DEFAULT_SITE),
    # S33-9: a PowerShell pipeline names the member without a dot. A process source on the line
    # (Get-Process, gps, ps, [Process]::GetProcesses*) and, after a later '|', a cmdlet that reads
    # or calls a member by name, in any letter case; one row per member, cmdlet and source.
    ("INJ-009", "$started = Get-Process -Name ffxiv_dx11 | Select-Object StartTime", PS1_SITE),
    ("INJ-009", "Get-Process ffxiv_dx11 | Select-Object -First 1 -ExpandProperty ExitTime", PS1_SITE),
    ("INJ-009", "Get-Process | Select-Object -Property Id,SafeHandle", PS1_SITE),
    ("INJ-009", "gps ffxiv_dx11 | ForEach-Object Handle", PS1_SITE),
    ("INJ-009", "Get-Process | Where-Object HasExited", PS1_SITE),
    ("INJ-009", "get-process | where exitcode -ne 0", PS1_SITE),
    ("INJ-009", "Get-Process | ? EnableRaisingEvents", PS1_SITE),
    ("INJ-009", "Get-Process | Group-Object PriorityClass", PS1_SITE),
    ("INJ-009", "ps | sort PriorityBoostEnabled", PS1_SITE),
    ("INJ-009", "Get-Process | Format-Table Id, ProcessorAffinity", PS1_SITE),
    ("INJ-009", "Get-Process | Sort-Object TotalProcessorTime -Descending", PS1_SITE),
    ("INJ-009", "Get-Process | Measure-Object -Property UserProcessorTime", PS1_SITE),
    ("INJ-009", "Get-Process | Format-List Name, PrivilegedProcessorTime", PS1_SITE),
    ("INJ-009", "Get-Process | ft Name, MaxWorkingSet", PS1_SITE),
    ("INJ-009", "Get-Process | fl MinWorkingSet", PS1_SITE),
    ("INJ-009", "Get-Process ffxiv_dx11 | ForEach-Object -MemberName WaitForExit -ArgumentList 1000", PS1_SITE),
    ("INJ-009", "Get-Process ffxiv_dx11 | foreach WaitForExitAsync", PS1_SITE),
    ("INJ-009", "Get-Process ffxiv_dx11 | % WaitForInputIdle", PS1_SITE),
    ("INJ-009", "[System.Diagnostics.Process]::GetProcessesByName('ffxiv_dx11') | % Kill", PS1_SITE),
    ("INJ-009", "$game = Get-Process -Name ffxiv_dx11; $game | Select-Object 'StartTime'", PS1_SITE),
    # ...and the same pipeline handed to PowerShell in a string is still the same handle.
    ("INJ-009", '        Run("powershell", "-Command \\"Get-Process ffxiv_dx11 | Select-Object StartTime\\"");',
     DEFAULT_SITE),
    ("DEU-001", "        var monitor = Configure(UseDeucalion: true);", DEFAULT_SITE),
    ("DEU-001", "        monitor.UseDeucalion = 1;", DEFAULT_SITE),
    ("DEU-001", "        $monitor.UseDeucalion = $true", PS1_SITE),
    ("DEU-002", "        DeucalionInjector.Inject(processId, payload);", DEFAULT_SITE),
    ("DEU-002", "        void OnMessage(DeucalionMessage message) { }", DEFAULT_SITE),
    ("DEU-002", "        var broadcast = DeucalionBroadcastType.Packet;", DEFAULT_SITE),
    ("DEU-004", "        ValidateLibraryChecksum(payloadPath);", DEFAULT_SITE),
    ("CAP-001", "        pcap_sendqueue_transmit(handle, queue, 0);", DEFAULT_SITE),
    ("CAP-001", "        _device.SendPacket(frame);", DEFAULT_SITE),
    ("CAP-001", "        using var queue = new SendQueue(4096);", DEFAULT_SITE),
    ("CAP-001", "        PacketSendPacket(adapter, packet, true);", DEFAULT_SITE),
    ("CAP-005", "        pcap_breakloop(handle);", DEFAULT_SITE),
    ("CAP-005", "        using var writer = new CaptureFileWriterDevice(capturePath);", DEFAULT_SITE),
    ("CAP-006", '        [LibraryImport("wpcap")] private static partial int pcap_open_live();', DEFAULT_SITE),
    ("CAP-006", '        [DllImport(@"C:\\Windows\\System32\\Npcap\\wpcap.dll")] static extern int f();', DEFAULT_SITE),
    ("CAP-006", '        [DllImport("Packet.dll")] static extern int PacketOpenAdapter();', DEFAULT_SITE),
    ("CAP-006", '        [DllImport("npcap")] static extern int f();', DEFAULT_SITE),
    ("CAP-006", '        [DllImport(dllName: "wpcap")] static extern int f();', DEFAULT_SITE),
    ("CAP-006", '        [DllImport("""wpcap.dll""")] static extern int f();', DEFAULT_SITE),
    ("CAP-006", '        var library = NativeLibrary.Load("wpcap.dll");', DEFAULT_SITE),
    ("CAP-006", '        NativeLibrary.TryLoad("Packet.dll", out var library);', DEFAULT_SITE),
    ("CAP-006", '        HMODULE library = LoadLibraryW(L"wpcap.dll");', CPP_SITE),
    ("CAP-006", '        QLibrary pcap(QStringLiteral("wpcap"));', CPP_SITE),
    ("CAP-006", '        private const string Pcap = @"C:\\Windows\\System32\\Npcap\\wpcap.dll";', DEFAULT_SITE),
    ("CAP-006", '        private const string Driver = "Packet";', DEFAULT_SITE),
    ("CAP-006", '            "wpcap.dll", CharSet = CharSet.Ansi)]', DEFAULT_SITE),
    ("CAP-006", '            L"Packet.dll");', CPP_SITE),
    ("NET-004", "        builder.WebHost.UseKestrel();", DEFAULT_SITE),
    # V5-6: the web SDK and the minimal-hosting entry point start Kestrel without naming it.
    ("NET-004", '<Project Sdk="Microsoft.NET.Sdk.Web">', "src/Collector/Sample.csproj"),
    ("NET-004", "        var app = WebApplication.CreateBuilder(args).Build();", DEFAULT_SITE),
    ("NET-005", "        auto *server = new QHttpServer(this);", CPP_SITE),
    ("NET-005", "        auto *server = new QWebSocketServer(name, mode, this);", CPP_SITE),
    ("NET-005", "        auto *server = new QLocalServer(this);", CPP_SITE),
    ("NET-005", "        auto *server = new QSslServer(this);", CPP_SITE),
    ("NET-005", "        auto *server = new QSctpServer(this);", CPP_SITE),
    ("NET-006", "        using var client = new WebClient();", DEFAULT_SITE),
    ("NET-006", "        using var invoker = new HttpMessageInvoker(handler);", DEFAULT_SITE),
    ("NET-006", "        var handler = new SocketsHttpHandler();", DEFAULT_SITE),
    ("NET-006", "        var handler = new HttpClientHandler();", DEFAULT_SITE),
    ("NET-006", "        var handler = new WinHttpHandler();", DEFAULT_SITE),
    ("NET-006", "        auto session = WinHttpOpen(agent, access, nullptr, nullptr, 0);", CPP_SITE),
    ("NET-006", "        var request = (HttpWebRequest)Create(url);", DEFAULT_SITE),
    ("NET-006", "        var request = (FtpWebRequest)Create(url);", DEFAULT_SITE),
    ("NET-006", "        var request = WebRequest.Create(url);", DEFAULT_SITE),
    ("NET-006", "        $request = [System.Net.WebRequest]::Create($url)", PS1_SITE),
    ("NET-006", "        auto *manager = new QNetworkAccessManager(this);", CPP_SITE),
    ("NET-006", "        auto *rest = new QRestAccessManager(manager, this);", CPP_SITE),
    ("NET-006", "        auto file = InternetOpenUrlW(session, url, nullptr, 0, 0, 0);", CPP_SITE),
    ("NET-006", "        auto connection = InternetConnectW(session, host, 443, 0, 0, 3, 0, 0);", CPP_SITE),
    ("NET-006", "        auto request = HttpOpenRequestW(connection, L\"GET\", path, 0, 0, 0, 0, 0);", CPP_SITE),
    ("NET-006", "        HttpSendRequestA(request, nullptr, 0, nullptr, 0);", CPP_SITE),
    ("NET-006", "        URLDownloadToFileW(nullptr, url, target, 0, nullptr);", CPP_SITE),
    ("NET-006", "        URLOpenBlockingStreamW(nullptr, url, &stream, 0, nullptr);", CPP_SITE),
    # S33-9: PowerShell's [xml] cast of a download needs no rule of its own; the download in it
    # trips NET-006 or NET-009 (see also the NET-009 rows below).
    ("NET-006", "[xml]$feed = (New-Object System.Net.WebClient).DownloadString($url)", PS1_SITE),
    ("NET-008", "        using var client = new UdpClient(5000);", DEFAULT_SITE),
    ("NET-008", "        using var raw = new Socket(family, SocketType.Stream, ProtocolType.Tcp);", DEFAULT_SITE),
    ("NET-008", "        auto *socket = new QTcpSocket(this);", CPP_SITE),
    ("NET-008", "        auto *socket = new QUdpSocket(this);", CPP_SITE),
    ("NET-008", "        auto *socket = new QSslSocket(this);", CPP_SITE),
    ("NET-008", "        connect(s, &QAbstractSocket::connected, this, f);", CPP_SITE),
    ("NET-008", "        auto *socket = new QSctpSocket(this);", CPP_SITE),
    ("NET-008", "        auto *lookup = new QDnsLookup(QDnsLookup::A, host, this);", CPP_SITE),
    ("NET-008", "        QHostInfo::lookupHost(host, this, &Probe::resolved);", CPP_SITE),
    ("NET-008", "        const auto info = QHostInfo::fromName(host);", CPP_SITE),
    ("NET-008", "        var addresses = Dns.GetHostAddresses(host);", DEFAULT_SITE),
    ("NET-008", "        $addresses = [System.Net.Dns]::GetHostAddresses($host)", PS1_SITE),
    ("NET-008", "        using var ping = new Ping();", DEFAULT_SITE),
    ("NET-008", "        using Ping probe = new();", DEFAULT_SITE),
    ("NET-008", "        var reply = await probe.SendPingAsync(host);", DEFAULT_SITE),
    ("NET-008", "        using var mail = new SmtpClient(host);", DEFAULT_SITE),
    ("NET-008", "        await using var quic = await QuicConnection.ConnectAsync(options);", DEFAULT_SITE),
    ("NET-008", "        await using var quic = await QuicListener.ListenAsync(options);", DEFAULT_SITE),
    ("NET-008", "        await using QuicStream stream = await connection.OpenOutboundStreamAsync(kind);", DEFAULT_SITE),
    ("NET-008", "        WSAStartup(MAKEWORD(2, 2), &data);", CPP_SITE),
    ("NET-008", "        auto s = WSASocketW(AF_INET, SOCK_STREAM, 0, nullptr, 0, 0);", CPP_SITE),
    ("NET-008", "        WSAConnect(s, address, length, nullptr, nullptr, nullptr, nullptr);", CPP_SITE),
    ("NET-008", "        auto s = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);", CPP_SITE),
    ("NET-008", "        auto s = socket(PF_INET6, SOCK_DGRAM, 0);", CPP_SITE),
    ("NET-008", "        getaddrinfo(host, port, &hints, &result);", CPP_SITE),
    ("NET-008", "        auto *entry = gethostbyname(host);", CPP_SITE),
    ("NET-008", "        GetAddrInfoW(host, port, &hints, &result);", CPP_SITE),
    ("NET-009/downloads", "        invoke-restmethod $url", PS1_SITE),
    ("NET-009/downloads", "        Start-BitsTransfer -Source $url -Destination $target", PS1_SITE),
    ("NET-009/downloads", "  DownloadTemporaryFile('https://example.invalid/x.exe', 'x.exe', '', nil);", "installer/Sample.iss"),
    ("NET-009/downloads", "  downloadtemporaryfile('https://example.invalid/x.exe', 'x.exe', '', nil);", "installer/Sample.iss"),
    ("NET-009/downloads", "  Page := CreateDownloadPage(Caption, Description, nil);", "installer/Sample.iss"),
    ("NET-009/downloads", "  Request := CreateOleObject('MSXML2.ServerXMLHTTP.6.0');", "installer/Sample.iss"),
    ("NET-009/downloads", '        Set request = CreateObject("Microsoft.XMLHTTP")', "scripts/sample.cmd"),
    ("NET-009/downloads", "        var request = new XMLHttpRequest();", QML_SITE),
    ("NET-009/downloads", '        Image { source: "https://example.invalid/banner.png" }', QML_SITE),
    ("NET-009/downloads", "        Image { source: 'https://example.invalid/banner.png' }", QML_SITE),
    # V5-6: C++ handing the QML engine a remote address.
    ("NET-009/downloads", '    engine.load(QUrl(QStringLiteral("https://example.invalid/Main.qml")));', CPP_SITE),
    ("NET-009/downloads", '    view.setSource(QUrl("https://example.invalid/Main.qml"));', CPP_SITE),
    ("NET-009/downloads", "$feed = [xml](Invoke-WebRequest -Uri $url -UseBasicParsing).Content", PS1_SITE),
    ("NET-009/qml-remote", "        image.source = 'https://example.invalid/banner.png'", QML_SITE),
    ("NET-009/qml-remote", 'import "https://example.invalid/qml/Remote" 1.0', QML_SITE),
    # V5-6: a remote address in a url property, Qt.resolvedUrl or Qt.include.
    ("NET-009/qml-remote", '    property url banner: "https://example.invalid/banner.png"', QML_SITE),
    ("NET-009/qml-remote", '        Image { source: Qt.resolvedUrl("https://example.invalid/b.png") }', QML_SITE),
    ("NET-009/qml-remote", '    Qt.include("https://example.invalid/lib.js")', "src/Desktop/qml/sample.js"),
    ("NET-009/shell-commands", "$page = iwr $url", PS1_SITE),
    ("NET-009/shell-commands", 'Write-Host "fetching"; irm $url', PS1_SITE),
    ("NET-009/shell-commands", "printf 'fetching'; wget $url -O x", "scripts/sample.sh"),
    ("NET-009/shell-commands", "        run: curl -L $url -o x", ".github/workflows/sample.yml"),
    ("NET-009/shell-commands", "@bitsadmin /transfer job %URL% %OUT%", "scripts/sample.cmd"),
    ("NET-009/shell-commands", "certutil.exe -urlcache -split -f %URL% %OUT%", "scripts/sample.bat"),
    ("NET-009/shell-commands", "& curl.exe -L $url -o $target", PS1_SITE),
    ("NET-009/shell-commands", "$body = (iwr $url).Content", PS1_SITE),
    ("NET-009/shell-commands", "$urls | ForEach-Object { irm $_ }", PS1_SITE),
    ("NET-009/shell-commands", "test -f x || wget $url", "scripts/sample.sh"),
    # V5-5: after a shell keyword or sudo, in backticks, after '!', behind cmd /c or Start-Process,
    # quoted after the call operator, and called by its full path.
    ("NET-009/shell-commands", 'if curl -fsS "$url" -o x; then', "scripts/sample.sh"),
    ("NET-009/shell-commands", "test -f x; then curl -L $url -o x; fi", "scripts/sample.sh"),
    ("NET-009/shell-commands", "for u in $list; do wget $u; done", "scripts/sample.sh"),
    ("NET-009/shell-commands", "else wget $url", "scripts/sample.sh"),
    ("NET-009/shell-commands", "elif curl -fsS $url; then", "scripts/sample.sh"),
    ("NET-009/shell-commands", "while curl -fsS $url; do sleep 1; done", "scripts/sample.sh"),
    ("NET-009/shell-commands", "until curl -fsS $url; do sleep 1; done", "scripts/sample.sh"),
    ("NET-009/shell-commands", "sudo wget $url", "scripts/sample.sh"),
    ("NET-009/shell-commands", "x=`curl -s $url`", "scripts/sample.sh"),
    ("NET-009/shell-commands", "if ! curl -fsS $url; then exit 1; fi", "scripts/sample.sh"),
    ("NET-009/shell-commands", "cmd /c curl -L %URL% -o %OUT%", "scripts/sample.cmd"),
    ("NET-009/shell-commands", "Start-Process curl -ArgumentList $url", PS1_SITE),
    ("NET-009/shell-commands", 'Start-Process -FilePath "curl.exe" -ArgumentList $url', PS1_SITE),
    ("NET-009/shell-commands", '& "curl.exe" $url', PS1_SITE),
    ("NET-009/shell-commands", '& "C:\\Program Files\\Git\\mingw64\\bin\\curl.exe" -L $url', PS1_SITE),
    ("NET-009/shell-commands", "C:\\Windows\\System32\\curl.exe -L %URL% -o x", "scripts/sample.bat"),
    ("NET-009/shell-commands", "/usr/bin/wget $url", "scripts/sample.sh"),
    ("NET-009/shell-commands", "$feed = [xml](iwr $url)", PS1_SITE),
    # S33-9: an XML reader handed a remote address downloads it. '.Load(' / ']::Load(' with an
    # http(s) literal first (XmlDocument, XDocument, XElement, PowerShell, MSXML in Pascal),
    # XmlReader.Create with one, and XmlTextReader / XPathDocument whose first literal is one.
    ("NET-009/xml-loads", '        document.Load("https://example.invalid/duties.xml");', DEFAULT_SITE),
    ("NET-009/xml-loads", '        var root = XElement.Load(@"http://example.invalid/feed.xml");', DEFAULT_SITE),
    ("NET-009/xml-loads", '        var feed = XDocument.Load(uri: $"https://{host}/feed.xml");', DEFAULT_SITE),
    ("NET-009/xml-loads", "$doc.Load('https://example.invalid/feed.xml')", PS1_SITE),
    ("NET-009/xml-loads", "$doc = [xml]::new(); $doc.load('HTTPS://example.invalid/feed.xml')", PS1_SITE),
    ("NET-009/xml-loads", "$feed = [System.Xml.Linq.XDocument]::Load('https://example.invalid/feed.xml')", PS1_SITE),
    ("NET-009/xml-loads", '        using var reader = XmlReader.Create("https://example.invalid/feed.xml", settings);',
     DEFAULT_SITE),
    ("NET-009/xml-loads", "$reader = [System.Xml.XmlReader]::Create('https://example.invalid/feed.xml')", PS1_SITE),
    ("NET-009/xml-loads", '        using var reader = new XmlTextReader("https://example.invalid/feed.xml");',
     DEFAULT_SITE),
    ("NET-009/xml-loads", "$reader = New-Object System.Xml.XmlTextReader 'https://example.invalid/feed.xml'",
     PS1_SITE),
    ("NET-009/xml-loads", '        var document = new XPathDocument("https://example.invalid/feed.xml");',
     DEFAULT_SITE),
    ("NET-009/xml-loads", "  XmlDoc.load('https://example.invalid/feed.xml');", "installer/Sample.iss"),
    # S33-9: a Python file that imports a network module or calls into one (NET-010 reads .py only).
    ("NET-010", "from urllib.request import urlopen, Request", PY_SITE),
    ("NET-010", "import urllib3", PY_SITE),
    ("NET-010", "import http.client as client", PY_SITE),
    ("NET-010", "import json, requests", PY_SITE),
    ("NET-010", "from httpx import AsyncClient", PY_SITE),
    ("NET-010", "import aiohttp", PY_SITE),
    ("NET-010", "import socket", PY_SITE),
    ("NET-010", "from ftplib import FTP_TLS", PY_SITE),
    ("NET-010", "import smtplib", PY_SITE),
    ("NET-010", "import poplib", PY_SITE),
    ("NET-010", "import imaplib", PY_SITE),
    ("NET-010", "import nntplib", PY_SITE),
    ("NET-010", "import telnetlib", PY_SITE),
    ("NET-010", "from xmlrpc.client import ServerProxy", PY_SITE),
    ("NET-010", "import websockets", PY_SITE),
    ("NET-010", '    requests = __import__("requests")', PY_SITE),
    ("NET-010", '    client = importlib.import_module("http.client")', PY_SITE),
    ("NET-010", "from urllib import request", PY_SITE),
    ("NET-010", "from http import HTTPStatus, client", PY_SITE),
    ("NET-010", "from xmlrpc import client as rpc", PY_SITE),
    ("NET-010", "    body = generate.urllib.request.urlopen(url).read()", PY_SITE),
    ("NET-010", '    print("fetching"); urllib.request.urlretrieve(url, target)', PY_SITE),
    ("NET-010", "    print('connecting'); reader, writer = await asyncio.open_connection(host, 443)", PY_SITE),
    ("AUT-001", "        keybd_event(key, 0, 0, 0);", DEFAULT_SITE),
    ("AUT-001", "        mouse_event(LeftDown, 0, 0, 0, 0);", DEFAULT_SITE),
    ("AUT-001", '        SendKeys.SendWait("{ENTER}");', DEFAULT_SITE),
    ("AUT-001", "        PostMessageW(window, WmKeyDown, key, 0);", DEFAULT_SITE),
    ("AUT-001", "        PostMessage(window, WmKeyDown, key, 0);", DEFAULT_SITE),
    ("AUT-001", "        PostThreadMessage(thread, WmQuit, 0, 0);", DEFAULT_SITE),
    ("AUT-001", "        SendMessage(window, WmChar, key, 0);", DEFAULT_SITE),
    ("AUT-001", "        SendMessageW(window, WmChar, key, 0);", DEFAULT_SITE),
    ("AUT-001", "        SendMessageTimeoutW(window, WmChar, key, 0, flags, 100, out _);", DEFAULT_SITE),
    ("AUT-001", "        SendMessageCallbackW(window, WmChar, key, 0, callback, 0);", DEFAULT_SITE),
    ("AUT-001", "        SendNotifyMessageW(window, WmChar, key, 0);", DEFAULT_SITE),
    ("AUT-001", "        game.CloseMainWindow();", DEFAULT_SITE),
)

# Lines that look like a forbidden token but are not one, each in the file it would live in.
# Each must pass every rule: a rule that widened into one of these would bury the real hits.
LOOK_ALIKES: tuple[tuple[str, str], ...] = (
    ("        var transition = _machine.Handle(semanticEvent);", DEFAULT_SITE),
    ("        return Program.ExitCodeAlreadyRunning;", DEFAULT_SITE),
    ("        var clients = Process.GetProcessesByName(name);", DEFAULT_SITE),
    ("        var id = process.Id; var name = process.ProcessName;", DEFAULT_SITE),
    ("        await client.SendMessageAsync(request, token);", DEFAULT_SITE),
    ('        [DllImport("ntdll.dll", ExactSpelling = true)]', DEFAULT_SITE),
    ("        // no raw-socket fallback; a pipe, not a socket", DEFAULT_SITE),
    ("        auto *socket = new QLocalSocket(this);", "src/Desktop/cpp/Sample.cpp"),
    ("; *** TDownloadWizardPage 向导页面和 DownloadTemporaryFile", "installer/Sample.isl"),
    ("        Qt.openUrlExternally(\"https://npcap.com/\")", "src/Desktop/qml/Sample.qml"),
    # CAP-006 names the pcap libraries only where they are loaded: the Npcap detector's registry
    # key, its file-existence probes and a source kind are not loads (R2T-3).
    ('        public const string RegistryKey = @"SOFTWARE\\Npcap";', DEFAULT_SITE),
    ('        public string WpcapPath => Path.Combine(root, "System32", "Npcap", "wpcap.dll");', DEFAULT_SITE),
    ('        public const string LiveSourceKind = "machina-npcap";', DEFAULT_SITE),
    ('        [DllImport("kernel32.dll")] static extern bool Beep(); // see the Packet docs', DEFAULT_SITE),
    # NET-006 / NET-008 look-alikes: names that merely contain a network word.
    ("        var connected = InternetConnectionState(); var suffix = settings.DnsSuffix;", DEFAULT_SITE),
    ("        QLocalSocket *socket() const; m_client->socket()->flush();", "src/Desktop/cpp/Sample.cpp"),
    # NET-009/shell-commands matches a download command at command position only: prose,
    # comments, quoted text and identifiers that contain the words do not trip it.
    ("# curl and wget are never called by this script", "scripts/sample.ps1"),
    ('Write-Host "use curl (not here); irm is an alias"', "scripts/sample.ps1"),
    ("$curl = Join-Path $tools 'curl.exe'", "scripts/sample.ps1"),
    ("function Get-CurlVersion { return $wgetPath }", "scripts/sample.ps1"),
    ("$wgetPath = $null; $irmEnabled = $false", "scripts/sample.ps1"),
    ("Write-Host 'irm'", "scripts/sample.ps1"),
    ("      - name: Download the index with curl", ".github/workflows/sample.yml"),
    ("        # run: curl -L https://example.invalid/x -o x", ".github/workflows/sample.yml"),
    ('echo "curl is not used here"', "scripts/sample.sh"),
    ("REM curl, wget and certutil -urlcache are forbidden here", "scripts/sample.bat"),
    ('        subprocess.run(["curl", url])', "tools/some-tool/sample.py"),
    ("curl -fsSL $url -o $target", "tools/some-tool/sample.py"),
    ('        source: "qrc:/icons/logo.svg"; image.source = "image://icons/logo"', "src/Desktop/qml/Sample.qml"),
    # V5-2: property patterns and initialisers of our own types, whose members only start like a
    # Process member's name, and a process's id and name, which open no handle.
    ("        var run = new RunRecord { StartTimeUtc = now, Handled = true, ExitCodes = codes };", DEFAULT_SITE),
    ("        if (process is { Id: var id, ProcessName: var name }) { }", DEFAULT_SITE),
    ("            ModulesLoaded: 3,", DEFAULT_SITE),
    # V5-5: shell words in prose, a tool that only starts with a download command's name, a URL,
    # and the call operator running one of our own scripts.
    ("REM if curl fails, stop", "scripts/sample.bat"),
    ("      - name: Download if curl is present", ".github/workflows/sample.yml"),
    ("echo see /usr/bin/curl for details", "scripts/sample.sh"),
    ("curl-config --version", "scripts/sample.sh"),
    ("      https://curl.se/download.html", ".github/workflows/sample.yml"),
    ('$tool = "curl.exe"', "scripts/sample.ps1"),
    ('& "$PSScriptRoot\\build.ps1" -Configuration Release', "scripts/sample.ps1"),
    # V5-6: a link opened in the browser, a local sound, a module loaded by name, a string property.
    ('    static const QUrl kNpcapUrl(QStringLiteral("https://npcap.com/"));', "src/Desktop/cpp/Sample.cpp"),
    ("    m_effect->setSource(QUrl::fromLocalFile(path));", "src/Desktop/cpp/Sample.cpp"),
    ('    engine.loadFromModule("MentorRecorder", "Main");', "src/Desktop/cpp/Sample.cpp"),
    ('    property string help: "https://npcap.com/"', "src/Desktop/qml/Sample.qml"),
    ("        var host = new WebApplicationHostOptions();", DEFAULT_SITE),
    # S33-9, INJ-009: the same member names on objects that are not processes, a process's
    # members that open no handle, a comment, and a variable that only starts like the alias.
    ("$runs | Sort-Object StartTime -Descending | Select-Object -First 1", "scripts/sample.ps1"),
    ("$jobs | Where-Object HasExited | ForEach-Object ExitCode", "scripts/sample.ps1"),
    ("Get-Process -Id $PID | Select-Object Id, ProcessName, Handles", "scripts/sample.ps1"),
    ("# Get-Process | Sort-Object StartTime would open a handle to every process", "scripts/sample.ps1"),
    ("$ps = [PowerShell]::Create(); $results | Sort-Object ExitCode", "scripts/sample.ps1"),
    ("$orphans = @(Get-Process -Name 'MentorRecorder*' -ErrorAction SilentlyContinue)", "scripts/sample.ps1"),
    # S33-9, NET-009/xml-loads: local files, a parsed string that names a namespace URI, an address
    # that is not the reader's first literal, and loads of our own types.
    ('        var duties = XDocument.Load("data/duties/cn.xml");', DEFAULT_SITE),
    ('        var duties = XDocument.Load(Path.Combine(root, "duties.xml")); // see https://example.invalid/',
     DEFAULT_SITE),
    ('        var ssml = XDocument.Parse("<speak xmlns=\\"https://www.w3.org/2001/10/synthesis\\"/>");', DEFAULT_SITE),
    ('        using var reader = new XmlTextReader(Path.Combine(dir, "feed.xml")); Log("https://example.invalid/");',
     DEFAULT_SITE),
    ("        using var reader = XmlReader.Create(new StringReader(xml), settings);", DEFAULT_SITE),
    ("        var catalog = ProfileCatalog.Load(directory);", DEFAULT_SITE),
    ("[xml]$trx = Get-Content -LiteralPath $Path -Raw -Encoding UTF8", "scripts/sample.ps1"),
    ("$doc.Load($localPath)", "scripts/sample.ps1"),
    # S33-9, NET-010: a test that fakes the download, names it in a string or a comment, an
    # attribute that only shares a module's name, and modules that open no connection.
    ("        self.requests = []", "tools/some-tool/test_sample.py"),
    ('        with mock.patch("urllib.request.urlopen", side_effect=OSError("offline")):',
     "tools/some-tool/test_sample.py"),
    ('        with mock.patch.object(generate, "http_get", side_effect=fake_get):', "tools/some-tool/test_sample.py"),
    ("    def fake_urlopen(url, timeout=60):", "tools/some-tool/test_sample.py"),
    ('        self.assertIn("urlopen(", source)', "tools/some-tool/test_sample.py"),
    ("# urlopen(url) runs in the duty-data generator only", "tools/some-tool/sample.py"),
    ("import urllib.parse", "tools/some-tool/sample.py"),
    ("from urllib.parse import urlencode", "tools/some-tool/sample.py"),
    ("from http import HTTPStatus", "tools/some-tool/sample.py"),
    ("import ssl, hashlib", "tools/some-tool/sample.py"),
    ("import requests", "tools/some-tool/notes.txt"),
)

# The exclusions, pinned (audit 2026-10-03, R2T-1). A name here is skipped at any depth, so it
# may only be a directory that can never hold a source file of ours; every other exclusion is
# one exact repository-relative path. Changing either list fails the self-test until this
# table is changed with it.
EXCLUDED_DIR_NAMES: tuple[str, ...] = (".git", ".vs", "__pycache__", "node_modules")
EXCLUDED_PATHS: tuple[str, ...] = ("tools/static-boundary-check/",)
# Build output, skipped only directly beside a .csproj: the .NET SDK leaves out <project>/bin and
# <project>/obj and compiles a .cs in a bin/ or obj/ anywhere deeper (audit 2026-10-03, V5-7).
EXCLUDED_OUTPUT_DIR_NAMES: tuple[str, ...] = ("bin", "obj")

# Every extension the scan reads, pinned here so that dropping one from rules.json fails the
# self-test instead of quietly taking a file type out of the boundary (audit 2026-10-03, ON2-6:
# deleting ".h" used to leave the self-test green with 35 headers unscanned).
SCANNED_EXTENSIONS: tuple[str, ...] = (
    ".bat", ".cc", ".cmake", ".cmd", ".cpp", ".cs", ".csproj", ".cxx", ".h", ".hpp", ".isl",
    ".iss", ".js", ".json", ".mjs", ".props", ".ps1", ".psm1", ".py", ".qml", ".qrc", ".sh",
    ".sln", ".targets", ".txt", ".ui", ".yaml", ".yml",
)

# Tokens the specification names one by one. Each must be rejected wherever it
# appears, regardless of which rule happens to catch it.
SPEC_TOKENS: tuple[str, ...] = (
    "ReadProcessMemory",
    "WriteProcessMemory",
    "VirtualAllocEx",
    "CreateRemoteThread",
    "SetWindowsHookEx",
    "pcap_sendpacket",
    "pcap_inject",
    "NetworkMonitorType.RawSocket",
    "UseDeucalion = true",
    "HttpListener",
    "TcpListener",
    "QTcpServer",
    "QHttpServer",
    "QNetworkAccessManager",
    "WebSocket",
)

# Every location the checker has to look at, and a file in it that the include
# list accepts. A token planted in any one of these must be found.
PLANT_SITES: tuple[tuple[str, str], ...] = (
    ("src/Collector/Capture/Sample.cs", "cs"),
    ("src/Desktop/cpp/Sample.cpp", "cpp"),
    ("src/Desktop/qml/Sample.qml", "qml"),
    ("src/Desktop/CMakeLists.txt", "cmake"),
    ("tests/Collector.UnitTests/Sample.cs", "cs"),
    ("tools/some-tool/sample.py", "py"),
    ("scripts/sample.ps1", "ps1"),
    # The installer ships with every release and runs elevated, and the workflows execute
    # third-party code; both were outside every rule until the 2026-09-21 audit (finding 6).
    ("installer/Sample.iss", "iss"),
    ("installer/Sample.isl", "iss"),
    (".github/workflows/sample.yml", "yml"),
    ("CMakeLists.txt", "cmake"),
    ("Directory.Build.targets", "xml"),
)

COMMENT: dict[str, str] = {
    "cs": "// ",
    "cpp": "// ",
    "qml": "// ",
    "py": "# ",
    "ps1": "# ",
    "cmake": "# ",
    "xml": "<!-- ",
    "iss": "; ",
    "yml": "# ",
}


class Failure(Exception):
    """A self-test expectation did not hold."""


def base_rules() -> dict:
    """rules.json as configured, except that no allow marker is pinned.

    The real pins name files of the real repository; in a throwaway tree they would all be
    reported missing, and every case would fail (or pass) for that reason alone. A case that
    exercises markers pins its own.
    """
    raw = json.loads(RULES_PATH.read_text(encoding="utf-8"))
    raw["allow_markers"] = []
    return raw


def write_rules(root: Path, raw: dict | None = None,
                pins: Iterable[tuple[str, str, str]] = ()) -> Path:
    """Write ``raw`` (default: base_rules()) with ``pins`` - (file, rule, matched text) - added
    beside the tree; return its path.

    The file sits in the root itself, which the checker never walks (only scan_files are read
    there), so it cannot trip a rule.
    """
    raw = base_rules() if raw is None else raw
    pinned = [{"file": file, "rule": rule, "match": match} for file, rule, match in pins]
    if pinned:
        raw = {**raw, "allow_markers": list(raw.get("allow_markers", [])) + pinned}
    path = root / "rules.json"
    path.write_text(json.dumps(raw), encoding="utf-8")
    return path


def run_check(root: Path, raw: dict | None = None,
              pins: Iterable[tuple[str, str, str]] = ()) -> tuple[int, dict]:
    """Run check.py against ``root`` and return its exit code and JSON report."""
    completed = subprocess.run(
        [
            sys.executable,
            str(CHECK_PY),
            "--root",
            str(root),
            "--rules",
            str(write_rules(root, raw, pins)),
            "--json",
        ],
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        check=False,
    )
    try:
        report = json.loads(completed.stdout or "{}")
    except ValueError as exc:  # pragma: no cover - only on a broken checker
        raise Failure(
            f"check.py did not emit JSON (exit {completed.returncode}): "
            f"{completed.stdout[:400]}{completed.stderr[:400]}"
        ) from exc
    return completed.returncode, report


def plant(root: Path, relative: str, line: str) -> None:
    """Write a file under ``root`` containing ``line``."""
    path = root / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        "// generated by selftest\n" + line + "\n", encoding="utf-8"
    )


def clean_tree(root: Path) -> None:
    """Create the shape the checker expects - every scan root, scan file and excluded path -
    with nothing forbidden in it."""
    rules = json.loads(RULES_PATH.read_text(encoding="utf-8"))
    for relative in rules.get("scan_dirs", []):
        (root / relative).mkdir(parents=True, exist_ok=True)
    for relative in rules.get("exclude_paths", []):
        (root / relative).mkdir(parents=True, exist_ok=True)
    for relative in [site for site, _ in PLANT_SITES] + list(rules.get("scan_files", [])):
        path = root / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text("// nothing to see here\n", encoding="utf-8")


def expect_violation(
    root: Path, why: str, rule_id: str | None = None, rule_key: str | None = None
) -> None:
    code, report = run_check(root)
    if code != EXIT_VIOLATION:
        raise Failure(f"{why}: expected exit {EXIT_VIOLATION}, got {code}")
    if report.get("violation_count", 0) < 1:
        raise Failure(f"{why}: exit was 1 but no violation was reported")
    if rule_id is not None:
        found = {v["rule_id"] for v in report.get("violations", [])}
        if rule_id not in found:
            raise Failure(
                f"{why}: expected rule {rule_id}, checker reported {sorted(found)}"
            )
    if rule_key is not None:
        keys = {v.get("rule_key") for v in report.get("violations", [])}
        if rule_key not in keys:
            raise Failure(
                f"{why}: expected rule entry {rule_key}, checker reported {sorted(map(str, keys))}"
            )


def expect_clean(root: Path, why: str) -> None:
    code, report = run_check(root)
    if code != EXIT_OK:
        raise Failure(
            f"{why}: expected exit {EXIT_OK}, got {code}; "
            f"violations={report.get('violations')}"
        )


def expect_reported(root: Path, why: str, rule_id: str, pins=()) -> dict:
    """``rule_id`` is reported, whether or not a marker problem also fails the run."""
    code, report = run_check(root, pins=pins)
    found = {v["rule_id"] for v in report.get("violations", [])}
    if code not in (EXIT_VIOLATION, EXIT_ERROR) or rule_id not in found:
        raise Failure(f"{why}: expected {rule_id} reported, got exit {code} and {sorted(found)}")
    return report


def rule_key(rule: dict) -> str:
    """The key a rules.json entry is sampled under: ``id``, or ``id/variant``."""
    variant = rule.get("variant", "")
    return f"{rule['id']}/{variant}" if variant else rule["id"]


def run_check_with_rules(root: Path, raw: dict) -> int:
    """Run check.py against ``root`` with a modified copy of the rules; return its exit code."""
    return run_check(root, raw)[0]


def import_checker():
    """check.py as a module, for the cases that inject a failure or mutate a rule in process."""
    sys.path.insert(0, str(HERE))
    try:
        import check  # here, so that every other case runs check.py only as a subprocess
    finally:
        sys.path.remove(str(HERE))
    return check


def run_in_process(root: Path, attribute: str, name: str) -> tuple[int, dict]:
    """Run check.main() against ``root`` with one file-system call failing for entries called ``name``.

    ``attribute`` is ``read_bytes`` / ``stat`` of ``Path`` (files), ``scandir`` of ``os``
    (directories, as os.walk lists them) or ``os.stat`` (what the checker stats a configured
    scan root with). A file or directory that cannot be read cannot be made portably from a
    test, so the failure is injected at the one call the checker makes; every other path
    behaves normally.
    """
    check = import_checker()
    if attribute in ("scandir", "os.stat"):
        owner, attribute = check.os, attribute.removeprefix("os.")
    else:
        owner = Path
    original = getattr(owner, attribute)

    def failing(*args, **kwargs):
        target = Path(args[0]) if args else None
        if target is not None and target.name == name:
            raise PermissionError(13, "simulated by selftest", str(target))
        return original(*args, **kwargs)

    rules_path = write_rules(root)
    out, err = io.StringIO(), io.StringIO()
    try:
        with mock.patch.object(owner, attribute, failing), \
                contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = check.main(["--root", str(root), "--rules", str(rules_path), "--json"])
    except Exception as exc:  # a checker that crashes has not reported the failure either
        raise Failure(f"check.main() raised {exc!r} instead of reporting it") from exc
    try:
        return code, json.loads(out.getvalue() or "{}")
    except ValueError as exc:
        raise Failure(f"check.main() did not emit JSON (exit {code}): {out.getvalue()[:400]}") from exc


def make_directory_link(link: Path, target: Path) -> None:
    """A symbolic link to ``target``, or on Windows without the privilege for one, a junction."""
    try:
        os.symlink(target, link, target_is_directory=True)
        return
    except (OSError, NotImplementedError):
        if os.name != "nt":
            raise
    import _winapi  # CPython on Windows; a junction needs no privilege

    _winapi.CreateJunction(str(target), str(link))


# --- the mutation check (audit 2026-10-03, R2T-2) -------------------------------------------


def _class_end(pattern: str, start: int) -> int:
    """Index just past the character class that opens at ``start``."""
    index = start + 1
    if index < len(pattern) and pattern[index] == "^":
        index += 1
    if index < len(pattern) and pattern[index] == "]":
        index += 1
    while index < len(pattern) and pattern[index] != "]":
        index += 2 if pattern[index] == "\\" else 1
    return index + 1


def _group_body(pattern: str, start: int) -> int:
    """Index where the body of the group opening at ``start`` begins (its closing ')' for a
    group with no body, such as an inline flag or a back-reference)."""
    if not pattern.startswith("(?", start):
        return start + 1
    rest = pattern[start + 2:]
    if rest[:1] in (":", "=", "!", ">"):
        return start + 3
    if rest[:2] in ("<=", "<!"):
        return start + 4
    if rest.startswith("P<"):
        return pattern.index(">", start) + 1
    flags = re.match(r"[aiLmsux-]*", rest).group(0)
    after = start + 2 + len(flags)
    if pattern[after:after + 1] == ":":
        return after + 1
    return pattern.index(")", start)


def alternation_sites(pattern: str) -> list[list[tuple[int, int]]]:
    """The (start, end) spans of the alternatives of every alternation in ``pattern``: the
    top level and every group with two or more alternatives."""
    sites: list[list[tuple[int, int]]] = []
    stack: list[tuple[int, list[int]]] = [(0, [])]
    index = 0
    while index < len(pattern):
        char = pattern[index]
        if char == "\\":
            index += 2
            continue
        if char == "[":
            index = _class_end(pattern, index)
            continue
        if char == "(":
            body = _group_body(pattern, index)
            stack.append((body, []))
            index = body
            continue
        if char == "|":
            stack[-1][1].append(index)
        elif char == ")":
            start, bars = stack.pop()
            if bars:
                edges = [start - 1, *bars, index]
                sites.append([(edges[n] + 1, edges[n + 1]) for n in range(len(edges) - 1)])
        index += 1
    start, bars = stack.pop()
    if bars:
        edges = [start - 1, *bars, len(pattern)]
        sites.append([(edges[n] + 1, edges[n + 1]) for n in range(len(edges) - 1)])
    return sites


def without_alternative(pattern: str, site: list[tuple[int, int]], which: int) -> str:
    """``pattern`` with alternative ``which`` of ``site`` and one adjacent '|' removed."""
    start, end = site[which]
    if which == 0:
        return pattern[:start] + pattern[site[1][0]:]
    return pattern[:site[which - 1][1]] + pattern[end:]


def planted_samples(key: str) -> list[tuple[str, str]]:
    """(site, line) of every sample planted for rule entry ``key``."""
    samples = [(SAMPLE_SITES.get(key, DEFAULT_SITE), SAMPLES[key])] if key in SAMPLES else []
    return samples + [(site, line) for sample_key, line, site in MORE_SAMPLES if sample_key == key]


def reported(check, root: Path, raw: dict, key: str) -> set[str]:
    """Files ``check.run`` reports a violation of rule entry ``key`` in, under rules ``raw``."""
    result = check.run(root, write_rules(root, raw))
    if result.errors:
        raise Failure(f"{key}: the scan was incomplete: {result.errors}")
    return {Path(v.path).relative_to(root).as_posix() for v in result.violations if v.rule.key == key}


def cases() -> Iterable[tuple[str, callable]]:
    """Yield (name, thunk) for every self-test case."""
    rules = json.loads(RULES_PATH.read_text(encoding="utf-8"))
    allow_marker = rules["allow_marker"]
    declared = [rule_key(rule) for rule in rules["rules"]]

    missing = [rule_id for rule_id in declared if rule_id not in SAMPLES]
    stale = [rule_id for rule_id in SAMPLES if rule_id not in declared]

    def coverage() -> None:
        if missing:
            raise Failure(
                "rules.json declares rules with no negative sample: " + ", ".join(missing)
            )
        if stale:
            raise Failure(
                "selftest has samples for rules that no longer exist: " + ", ".join(stale)
            )

    yield "every rule has a negative sample", coverage

    # The baseline: a tree with nothing forbidden in it must pass, otherwise
    # every case below would "pass" for the wrong reason.
    def baseline() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            expect_clean(root, "a tree with no forbidden token")

    yield "a clean tree passes", baseline

    for key in declared:
        if key not in SAMPLES:
            continue
        sample = SAMPLES[key]

        def one_rule(key: str = key, sample: str = sample) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, SAMPLE_SITES.get(key, DEFAULT_SITE), sample)
                expect_violation(root, f"rule {key}", key.split("/", 1)[0], key)

        yield f"{key} is rejected", one_rule

    # --- every alternative of every pattern is needed by a sample (audit 2026-10-03, R2T-2) ---
    # Each alternative - at the top level and inside every group - is removed in turn from an
    # in-memory copy of its rule; at least one planted sample of that rule must then stop being
    # reported. An alternative no sample depends on is a spelling the self-test never proves.
    def every_alternative_is_needed() -> None:
        check = import_checker()
        uncovered: list[str] = []
        for entry in rules["rules"]:
            key = rule_key(entry)
            samples = planted_samples(key)
            if not samples:
                continue  # reported by "every rule has a negative sample"
            sites = alternation_sites(entry["pattern"])
            if not sites:
                continue
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                planted = set()
                for number, (site, line) in enumerate(samples):
                    stem, dot, suffix = site.rpartition(".")
                    relative = f"{stem}_m{number}{dot}{suffix}"
                    plant(root, relative, line)
                    planted.add(relative)
                single = {**base_rules(), "rules": [entry]}
                baseline = reported(check, root, single, key)
                if baseline != planted:
                    raise Failure(f"{key}: samples not reported by the unmutated rule: "
                                  f"{sorted(planted - baseline)}")
                for site in sites:
                    for which, (start, end) in enumerate(site):
                        mutated = {**entry, "pattern": without_alternative(entry["pattern"], site, which)}
                        if reported(check, root, {**single, "rules": [mutated]}, key) == baseline:
                            uncovered.append(f"{key}: {entry['pattern'][start:end]!r}")
        if uncovered:
            raise Failure("no planted sample needs these alternatives (add a MORE_SAMPLES row "
                          "only it catches): " + "; ".join(uncovered))

    yield "every alternative of every pattern is needed by a planted sample", every_alternative_is_needed

    for key, sample, site in MORE_SAMPLES:

        def one_spelling(key: str = key, sample: str = sample, site: str = site) -> None:
            if key not in declared:
                raise Failure(f"MORE_SAMPLES names {key}, which rules.json does not declare")
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, site, sample)
                expect_violation(root, f"{key} in {site}: {sample.strip()!r}",
                                 key.split("/", 1)[0], key)

        yield f"{key} rejects {sample.strip()!r}", one_spelling

    for line, site in LOOK_ALIKES:

        def look_alike(line: str = line, site: str = site) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, site, line)
                expect_clean(root, f"look-alike {line.strip()!r} in {site}")

        yield f"look-alike {line.strip()!r} passes", look_alike

    for token in SPEC_TOKENS:

        def one_token(token: str = token) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, "src/Collector/Capture/Sample.cs", "        x = " + token + ";")
                expect_violation(root, f"token {token!r}")

        yield f"token {token!r} is rejected", one_token

    for relative, kind in PLANT_SITES:

        def one_site(relative: str = relative) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, relative, "        x = CreateRemoteThread(h);")
                expect_violation(root, f"planted in {relative}", "INJ-004")

        yield f"{relative} is scanned", one_site

    # --- the scan scope itself (audit 2026-09-21, finding 6) --------------------------
    # The cases above prove a token planted in each location is found; this one pins the
    # configuration that makes them reachable, so narrowing the scope again fails here
    # instead of silently switching the boundary off for what actually ships.
    def scan_scope_covers_what_ships() -> None:
        configured = {str(d) for d in rules.get("scan_dirs", [])}
        for required in ("src", "tests", "tools", "scripts", "installer", ".github"):
            if required not in configured:
                raise Failure(
                    f"scan_dirs no longer covers {required!r}; it is {sorted(configured)}"
                )
        extensions = {str(e).lower() for e in rules.get("include_extensions", [])}
        for suffix in (".iss", ".isl"):
            if suffix not in extensions:
                raise Failure(
                    f"include_extensions no longer covers {suffix!r}, so the installer "
                    "script would not be read"
                )

    yield "the scan still covers the installer and the workflows", scan_scope_covers_what_ships

    def extensions_are_pinned() -> None:
        configured = sorted(str(e) for e in rules.get("include_extensions", []))
        if configured != sorted(SCANNED_EXTENSIONS):
            dropped = sorted(set(SCANNED_EXTENSIONS) - set(configured))
            added = sorted(set(configured) - set(SCANNED_EXTENSIONS))
            raise Failure(
                f"include_extensions differs from the pinned set: dropped {dropped}, "
                f"added {added}; update SCANNED_EXTENSIONS only together with the scan scope"
            )

    yield "include_extensions is exactly the pinned set", extensions_are_pinned

    for suffix in SCANNED_EXTENSIONS:

        def one_extension(suffix: str = suffix) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, f"src/Collector/Planted{suffix}", "        x = CreateRemoteThread(h);")
                expect_violation(root, f"a {suffix} file", "INJ-004")

        yield f"a {suffix} file is scanned", one_extension

    # --- exclusions: names only for what never holds our sources (audit 2026-10-03, R2T-1) ---
    def exclusions_are_pinned() -> None:
        names = sorted(rules.get("exclude_dir_names", []))
        paths = sorted(rules.get("exclude_paths", []))
        outputs = sorted(rules.get("exclude_output_dir_names", []))
        if names != sorted(EXCLUDED_DIR_NAMES) or paths != sorted(EXCLUDED_PATHS) \
                or outputs != sorted(EXCLUDED_OUTPUT_DIR_NAMES):
            raise Failure(
                f"exclusions differ from the pinned sets: exclude_dir_names {names} (pinned "
                f"{sorted(EXCLUDED_DIR_NAMES)}), exclude_paths {paths} (pinned {sorted(EXCLUDED_PATHS)}), "
                f"exclude_output_dir_names {outputs} (pinned {sorted(EXCLUDED_OUTPUT_DIR_NAMES)})"
            )

    yield ("exclude_dir_names, exclude_output_dir_names and exclude_paths are exactly the pinned sets",
           exclusions_are_pinned)

    # A directory of one of these names deep inside a scan root holds files the build reads (the
    # SDK compiles every .cs under the project folder, CMake lists its sources by path), so it is
    # scanned like any other; only the checker's own directory is excluded, by its exact path.
    for inside in (
        "src/Collector/build/Planted.cs",
        "src/Collector/packages/Planted.cs",
        "src/Collector/artifacts/Planted.cs",
        "src/Desktop/Testing/Planted.cpp",
        "tests/Collector.UnitTests/TestResults/Planted.cs",
        "tools/other/static-boundary-check/check.py",
        "src/Collector/.hidden/Planted.cs",
        "tools/shared-calibration/public-repo/.github/workflows/planted.yml",
    ):

        def deep_directory_is_scanned(inside: str = inside) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, inside, "        x = CreateRemoteThread(h);")
                expect_violation(root, f"planted in {inside}", "INJ-004")

        yield f"{inside} is scanned", deep_directory_is_scanned

    def named_exclusions_apply_at_any_depth() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            for name in EXCLUDED_DIR_NAMES:
                plant(root, f"src/Collector/{name}/deep/Planted.cs", "        CreateRemoteThread(h);")
            plant(root, "tools/static-boundary-check/Planted.py", "        CreateRemoteThread(h)")
            expect_clean(root, "the excluded names and the checker's own directory")

    yield "the excluded names and paths are not scanned", named_exclusions_apply_at_any_depth

    for name, mutate in (
        ("an exclude_paths entry that does not exist",
         lambda raw: raw.setdefault("exclude_paths", []).append("tools/gone/")),
        ("an exclude_paths entry in the wrong letter case",
         lambda raw: raw.__setitem__("exclude_paths", ["tools/Static-Boundary-Check/"])),
    ):

        def missing_exclusion(mutate=mutate, name: str = name) -> None:
            raw = base_rules()
            mutate(raw)
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                code, report = run_check(root, raw)
                if code != EXIT_ERROR or not report.get("errors"):
                    raise Failure(f"{name}: expected exit 2 with an error, got {code}")

        yield f"{name} fails the run", missing_exclusion

    for key, bad in (
        ("exclude_paths", "src/"),
        ("exclude_paths", ".github/"),
        ("exclude_paths", "build/"),
        ("exclude_paths", "tools/static-boundary-check"),
        ("exclude_paths", "tools/../tools/x/"),
        ("exclude_dir_names", "src/Collector"),
        ("exclude_dir_names", ""),
        ("exclude_dir_names", "obj"),
        ("exclude_output_dir_names", "src/bin"),
        ("exclude_output_dir_names", ".."),
        ("exclude_dirs", "build"),
    ):

        def malformed_exclusion(key: str = key, bad: str = bad) -> None:
            raw = base_rules()
            raw[key] = list(raw.get(key, [])) + [bad]
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                if run_check_with_rules(root, raw) != EXIT_ERROR:
                    raise Failure(f"{key} entry {bad!r}: expected exit 2 (unusable rules)")

        yield f"{key} entry {bad!r} is refused as a rules error", malformed_exclusion

    # --- configured roots that are missing, and links (audit 2026-10-03, R2T-6) --------------
    for gone in ("installer", ".github", "MentorRecorder.sln"):

        def missing_root(gone: str = gone) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                target = root / gone
                if target.is_dir():
                    for path in sorted(target.rglob("*"), reverse=True):
                        path.rmdir() if path.is_dir() else path.unlink()
                    target.rmdir()
                else:
                    target.unlink()
                code, report = run_check(root)
                if code != EXIT_ERROR or not any(gone in error for error in report.get("errors", [])):
                    raise Failure(f"a missing {gone}: expected exit 2 naming it, got {code}: "
                                  f"{report.get('errors')}")

        yield f"a configured {gone} that does not exist fails the run", missing_root

    def unstatable_root() -> None:
        # Path.is_dir() answers False for an entry it cannot stat on Python 3.14, so a scan root
        # behind such an error used to be skipped there without a word.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            code, report = run_in_process(root, "os.stat", "installer")
            if code != EXIT_ERROR or not any("installer" in e for e in report.get("errors", [])):
                raise Failure(f"a scan root os.stat() fails on: expected exit 2, got {code}")

    yield "a scan root that cannot be stat-ed fails the run", unstatable_root

    def linked_directory() -> None:
        with tempfile.TemporaryDirectory() as outside, tempfile.TemporaryDirectory() as tmp:
            plant(Path(outside), "Planted.cs", "        x = CreateRemoteThread(h);")
            root = Path(tmp)
            clean_tree(root)
            link = root / "src" / "Collector" / "Linked"
            make_directory_link(link, Path(outside))
            try:
                code, report = run_check(root)
            finally:
                os.rmdir(link) if os.name == "nt" else os.unlink(link)
            if code != EXIT_ERROR or not any("Linked" in e for e in report.get("errors", [])):
                raise Failure(f"a linked directory: expected exit 2 naming it, got {code}: {report}")

    yield "a symbolic link or junction to a directory is reported, not skipped", linked_directory

    # --- path allowances -------------------------------------------------------------
    # The one outbound client is allowed by its exact repository-relative path, and the
    # public repository's script mirror by an explicit directory prefix. Both must be
    # exact: a file of the same name elsewhere, a sibling, or a look-alike directory is
    # still a violation, and an allowance lifts one rule, never the others.
    client = "src/Collector/Protocol/Sharing/SharedCalibrationClient.cs"
    http_line = "        using var client = new HttpClient();"
    host_line = '        var index = "https://raw.githubusercontent.com/owner/repo/main/index.json";'
    cdn_line = '        var mirror = "https://cdn.jsdelivr.net/gh/owner/repo@main/index.json";'

    def exact_path_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, client, http_line + "\n" + host_line)
            expect_clean(root, f"NET-006 and NET-007 in {client}")

    yield "NET-006/NET-007 allow the fetch client by exact path", exact_path_is_allowed

    for elsewhere in (
        "tests/Collector.UnitTests/SharedCalibrationClient.cs",
        "src/Collector/Capture/SharedCalibrationClient.cs",
        "tools/mirror/src/Collector/Protocol/Sharing/SharedCalibrationClient.cs",
        "src/Collector/Protocol/Sharing/SharedCalibrationStore.cs",
    ):

        def same_name_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, http_line)
                expect_violation(root, f"HttpClient in {elsewhere}", "NET-006")

        yield f"NET-006 still rejects {elsewhere}", same_name_elsewhere

    def allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, client, "        x = CreateRemoteThread(h);")
            expect_violation(root, f"CreateRemoteThread in {client}", "INJ-004")

    yield "an allowed path is still checked by every other rule", allowance_lifts_one_rule_only

    for inside in ("tools/shared-calibration/publish.py", "tools/shared-calibration/lib/index.py"):

        def prefix_is_allowed(inside: str = inside) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, inside, "        MIRROR = 'https://cdn.jsdelivr.net/gh/owner/repo@main/'")
                expect_clean(root, f"a host literal in {inside}")

        yield f"NET-007 allows {inside}", prefix_is_allowed

    for outside in (
        "tools/shared-calibration-old/publish.py",
        "tools/other/shared-calibration/publish.py",
        "scripts/fetch.ps1",
        "tests/Collector.UnitTests/SharedCalibrationClientTests.cs",
    ):

        def prefix_ends_at_the_boundary(outside: str = outside) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, outside, "        x = 'HTTPS://FASTLY.JSDELIVR.NET/gh/owner/repo@main/'")
                expect_violation(root, f"a host literal in {outside}", "NET-007")

        yield f"NET-007 still rejects {outside}", prefix_ends_at_the_boundary

    def prefix_does_not_open_net006() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "tools/shared-calibration/fetch.py", http_line)
            expect_violation(root, "HttpClient under tools/shared-calibration/", "NET-006")

    yield "the NET-007 directory allowance does not lift NET-006", prefix_does_not_open_net006

    # --- the online-speech client (docs/privacy-boundary.md section 8.3) ---------------
    # The second outbound client is allowed HttpClient and the speech host, by exact path,
    # and nothing else: the CDN hosts stay refused there, the speech host stays refused in
    # the shared-calibration client, and siblings or look-alikes stay refused everywhere.
    speech = "src/Collector/Speech/OnlineSpeechClient.cs"
    speech_host_line = '        const string HostSuffix = ".TTS.Speech.Microsoft.com";'

    def speech_client_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, http_line + "\n" + speech_host_line)
            expect_clean(root, f"NET-006 and the speech host in {speech}")

    yield "NET-006/NET-007 allow the online-speech client by exact path", speech_client_is_allowed

    def speech_client_may_not_name_the_cdn() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, cdn_line)
            expect_violation(
                root, f"a CDN host in {speech}", "NET-007", "NET-007/shared-calibration-hosts"
            )

    yield "NET-007 still rejects the CDN hosts in the online-speech client", speech_client_may_not_name_the_cdn

    def speech_client_may_not_name_the_content_host() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, host_line)
            expect_violation(
                root, f"the content host in {speech}", "NET-007", "NET-007/github-content-hosts"
            )

    yield (
        "NET-007 still rejects the content host in the online-speech client",
        speech_client_may_not_name_the_content_host,
    )

    # --- the update-check client (docs/privacy-boundary.md section 8.4) -----------------
    # The third outbound client is allowed HttpClient and the content host, by exact path, and
    # nothing else: the CDN hosts and the speech host stay refused there, the content host stays
    # refused in every other file of the feature, and a sibling is refused everywhere.
    updates = "src/Collector/Update/UpdateCheckClient.cs"

    def update_client_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, updates, http_line + "\n" + host_line)
            expect_clean(root, f"NET-006 and the content host in {updates}")

    yield "NET-006/NET-007 allow the update-check client by exact path", update_client_is_allowed

    for line, variant, what in (
        (cdn_line, "NET-007/shared-calibration-hosts", "a CDN host"),
        (speech_host_line, "NET-007/speech-host", "the speech host"),
    ):

        def update_client_may_not_name_it(
            line: str = line, variant: str = variant, what: str = what
        ) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, updates, line)
                expect_violation(root, f"{what} in {updates}", "NET-007", variant)

        yield f"NET-007 still rejects {what} in the update-check client", update_client_may_not_name_it

    for elsewhere in (
        "src/Collector/Update/UpdateCheckService.cs",
        "src/Collector/Update/UpdateCheckClient.cs.bak.cs",
        "src/Collector/Capture/UpdateCheckClient.cs",
        "tests/Collector.UnitTests/UpdateCheckClientTests.cs",
        "src/Collector/Speech/OnlineSpeechClient.cs",
    ):

        def content_host_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, host_line)
                expect_violation(
                    root, f"the content host in {elsewhere}", "NET-007", "NET-007/github-content-hosts"
                )

        yield f"NET-007 rejects the content host in {elsewhere}", content_host_elsewhere

    def http_beside_the_update_client() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "src/Collector/Update/UpdateCheckService.cs", http_line)
            expect_violation(root, "HttpClient in the update-check service", "NET-006")

    yield "NET-006 still rejects HttpClient beside the update-check client", http_beside_the_update_client

    def update_allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, updates, "        var socket = new ClientWebSocket();")
            expect_violation(root, f"a WebSocket in {updates}", "NET-003")

    yield "the update-check client is still checked by every other rule", update_allowance_lifts_one_rule_only

    for elsewhere in (
        client,
        "src/Collector/Speech/OnlineSpeechCache.cs",
        "src/Collector/Speech/OnlineSpeechClient.cs.bak.cs",
        "src/Collector/Capture/OnlineSpeechClient.cs",
        "tests/Collector.UnitTests/OnlineSpeechClientTests.cs",
        "src/Desktop/cpp/TtsService.cpp",
        "src/Desktop/qml/pages/SettingsPage.qml",
        "tools/shared-calibration/speech.py",
        "tools/duty-data-generator/generate.py",
        "scripts/verify.ps1",
    ):

        def speech_host_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, speech_host_line)
                expect_violation(
                    root, f"the speech host in {elsewhere}", "NET-007", "NET-007/speech-host"
                )

        yield f"NET-007 rejects the speech host in {elsewhere}", speech_host_elsewhere

    for elsewhere in (
        "src/Collector/Speech/OnlineSpeechCache.cs",
        "src/Collector/Speech/OnlineSpeechService.cs",
        "tests/Collector.UnitTests/OnlineSpeechClient.cs",
        "src/Desktop/cpp/TtsService.cpp",
    ):

        def http_beside_the_speech_client(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, http_line)
                expect_violation(root, f"HttpClient in {elsewhere}", "NET-006")

        yield f"NET-006 still rejects {elsewhere}", http_beside_the_speech_client

    def speech_allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, speech, "        var socket = new ClientWebSocket();")
            expect_violation(root, f"a WebSocket in {speech}", "NET-003")

    yield "the online-speech client is still checked by every other rule", speech_allowance_lifts_one_rule_only

    # Offline OCR probes only manage the tesseract process they just started.
    # Keep this exception at one exact script path; it does not permit network
    # downloads or game-process handles, and sibling helper paths remain refused.
    ocr_probe = "scripts/ocr-runtime.ps1"
    ocr_wait = "$process.WaitForExit(30000); $process.Kill($true); $code = $process.ExitCode"

    def owned_ocr_process_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, ocr_probe, ocr_wait)
            expect_clean(root, f"our own OCR process in {ocr_probe}")

    yield "INJ-009 allows the owned OCR probe by exact path", owned_ocr_process_is_allowed

    for elsewhere in ("scripts/ocr-runtime-helper.ps1", "scripts/sub/ocr-runtime.ps1",
                      "scripts/bootstrap-ocr.ps1", "src/Collector/ocr-runtime.ps1"):

        def ocr_process_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, ocr_wait)
                expect_violation(root, f"OCR process members in {elsewhere}", "INJ-009")

        yield f"INJ-009 still rejects OCR look-alike {elsewhere}", ocr_process_elsewhere

    for rule, text in (("INJ-007", "OpenProcess(1, false, gamePid)"),
                       ("NET-009", "Invoke-WebRequest -Uri $url -OutFile $target")):

        def ocr_allowance_is_narrow(rule: str = rule, text: str = text) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, ocr_probe, text)
                expect_violation(root, f"unrelated {rule} in {ocr_probe}", rule)

        yield f"the OCR probe allowance still enforces {rule}", ocr_allowance_is_narrow

    # --- INJ-009: our own processes only (audit 2026-10-03, ON2-1) -----------------------
    # The parent watchdog waits on the Desktop and reads its start time; that file is allowed by
    # exact path. The game locator, a sibling of the watchdog and a look-alike path are not,
    # and the watchdog's allowance lifts INJ-009 only.
    watchdog = "src/Collector/Diagnostics/ParentProcessWatchdog.cs"
    start_line = "        if (StartTimeMatches(parent.StartTime, expected)) return;"

    def watchdog_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, watchdog, start_line + "\n        await parent.WaitForExitAsync(token);")
            expect_clean(root, f"INJ-009 in {watchdog}")

    yield "INJ-009 allows the parent watchdog by exact path", watchdog_is_allowed

    for elsewhere in (
        "src/Collector/Capture/GameProcessLocator.cs",
        "src/Collector/Diagnostics/ParentProcessWatchdogHelper.cs",
        "src/Collector/Capture/ParentProcessWatchdog.cs",
        "tests/Collector.UnitTests/GameProcessLocatorTests.cs",
        # A file with one hit that is not a Process member carries a pinned line marker, not a
        # whole-file allowance, so a real Process member added there later is still refused
        # (audit 2026-10-03, R2T-5).
        "src/Collector/CommandLineOptions.cs",
        "tests/Collector.IntegrationTests/RecoveryEndToEndTests.cs",
    ):

        def start_time_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, start_line)
                expect_violation(root, f"Process.StartTime in {elsewhere}", "INJ-009")

        yield f"INJ-009 still rejects {elsewhere}", start_time_elsewhere

    def watchdog_allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, watchdog, "        using var parent = OpenProcess(Synchronize, false, pid);")
            expect_violation(root, f"OpenProcess in {watchdog}", "INJ-007")

    yield "the parent watchdog is still checked by every other rule", watchdog_allowance_lifts_one_rule_only

    # --- NET-009: the installer's pinned Npcap download (ON2-4; V5-4: one pinned line, not the file) ---
    installer = "installer/MentorRecorder.iss"
    download_call = "  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), Desc, @OnProgress);"
    download_line = download_call + " // " + rules["allow_marker"] + "(NET-009): the pinned Npcap download"
    pin_download = [(installer, "NET-009", "CreateDownloadPage(")]

    def installer_download_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, installer, download_line)
            code, report = run_check(root, pins=pin_download)
            if code != EXIT_OK or report.get("allow_marker_count") != 1:
                raise Failure(f"the marked download page in {installer}: exit {code}, {report}")

    yield "NET-009 allows the installer's Npcap download on its one pinned line", installer_download_is_allowed

    for name, text in (
        ("an unmarked download page", download_call),
        ("a second download beside the marked one",
         download_line + "\n  DownloadTemporaryFile('https://example.invalid/x.exe', 'x.exe', '', nil);"),
    ):

        def installer_download_elsewhere(name: str = name, text: str = text) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, installer, text)
                expect_reported(root, f"{name} in {installer}", "NET-009", pins=pin_download)

        yield f"NET-009 rejects {name} in the installer", installer_download_elsewhere

    def installer_has_no_whole_file_allowance() -> None:
        for entry in rules["rules"]:
            if entry["id"] == "NET-009" and (entry.get("allow_paths") or entry.get("allow_path_prefixes")):
                raise Failure(f"NET-009/{entry.get('variant')} allows whole files again: "
                              f"{entry.get('allow_paths')} {entry.get('allow_path_prefixes')}")
        pins = [pin for pin in rules.get("allow_markers", []) if pin.get("file") == installer]
        if pins != [{"file": installer, "rule": "NET-009", "match": "CreateDownloadPage("}]:
            raise Failure(f"the installer's download is not pinned as one marker: {pins}")

    yield "the installer's download is one pinned marker, not a whole-file allowance", installer_has_no_whole_file_allowance

    for elsewhere in ("installer/Other.iss", "installer/sub/MentorRecorder.iss", "scripts/sample.ps1"):

        def download_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, download_call)
                expect_violation(root, f"a download page in {elsewhere}", "NET-009")

        yield f"NET-009 still rejects {elsewhere}", download_elsewhere

    def installer_marker_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, installer, download_call + " Exec('x', 'Get-Process | % { $_.Kill() }', '', 0, 0, R); // "
                  + rules["allow_marker"] + "(NET-009): the pinned Npcap download")
            expect_reported(root, f"Kill on the marked line of {installer}", "INJ-009", pins=pin_download)

    yield "the installer's marked line is still checked by every other rule", installer_marker_lifts_one_rule_only

    # --- NET-010: the one Python file that downloads (audit 2026-10-03, S33-9) -------------------
    # The duty-data generator downloads public game data at development time and is allowed by exact
    # path; its tests, its siblings, the shared-calibration tools and a look-alike path are not, and
    # the allowance lifts NET-010 only.
    generator = "tools/duty-data-generator/generate.py"
    generator_lines = ("import urllib.request\n"
                       "    with urllib.request.urlopen(request, timeout=timeout, context=context) as response:")

    def generator_is_allowed() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, generator, generator_lines)
            expect_clean(root, f"NET-010 in {generator}")

    yield "NET-010 allows the duty-data generator by exact path", generator_is_allowed

    for elsewhere in (
        "tools/duty-data-generator/test_generate.py",
        "tools/duty-data-generator/fetch.py",
        "tools/other/duty-data-generator/generate.py",
        "tools/shared-calibration/publish.py",
        "scripts/generate.py",
    ):

        def python_download_elsewhere(elsewhere: str = elsewhere) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, elsewhere, generator_lines)
                expect_violation(root, f"a Python download in {elsewhere}", "NET-010")

        yield f"NET-010 still rejects {elsewhere}", python_download_elsewhere

    def generator_allowance_lifts_one_rule_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, generator, "    raw = socket.socket(socket.AF_INET, socket.SOCK_STREAM)")
            expect_violation(root, f"a raw socket in {generator}", "NET-008")

    yield "the duty-data generator is still checked by every other rule", generator_allowance_lifts_one_rule_only

    def python_rule_reads_python_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "tools/some-tool/notes.txt", "import urllib.request")
            expect_clean(root, "a Python import in a .txt file")
            plant(root, "tools/some-tool/fetch.py", "import urllib.request")
            expect_violation(root, "a Python import in a .py file", "NET-010")

    yield "NET-010 reads Python files only", python_rule_reads_python_only

    # --- variants: one id, several entries, each with its own pattern and allowances ----
    def variant_rules(mutate) -> dict:
        raw = base_rules()
        mutate(raw["rules"])
        return raw

    def drop_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                del entry["variant"]

    def duplicate_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                entry["variant"] = "shared-calibration-hosts"

    def bad_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                entry["variant"] = "Speech Host"

    def non_string_variant(entries: list) -> None:
        for entry in entries:
            if entry.get("variant") == "speech-host":
                entry["variant"] = 7

    for name, mutate in (
        ("an entry of a repeated id without a variant", drop_variant),
        ("two entries with the same id and variant", duplicate_variant),
        ("a variant that is not a lower-case token", bad_variant),
        ("a variant that is not a string", non_string_variant),
    ):

        def malformed_variant(mutate=mutate, name: str = name) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                code = run_check_with_rules(root, variant_rules(mutate))
                if code != 2:
                    raise Failure(f"{name}: expected exit 2 (unusable rules), got {code}")

        yield f"{name} is refused as a rules error", malformed_variant

    for key, bad in (
        ("allow_paths", "src\\Collector\\Protocol\\Sharing\\SharedCalibrationClient.cs"),
        ("allow_paths", "/src/Collector/Protocol/Sharing/SharedCalibrationClient.cs"),
        ("allow_paths", "src/Collector/../Collector/SharedCalibrationClient.cs"),
        ("allow_paths", "src/Collector/Protocol/Sharing/"),
        ("allow_path_prefixes", "tools/shared-calibration"),
        ("allow_path_prefixes", "./tools/shared-calibration/"),
        ("allow_paths", ""),
    ):

        def malformed_allowance(key: str = key, bad: str = bad) -> None:
            raw = base_rules()
            for rule in raw["rules"]:
                if rule["id"] == "NET-006":
                    rule[key] = [bad]
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                code = run_check_with_rules(root, raw)
                if code != 2:
                    raise Failure(f"{key}={bad!r}: expected exit 2 (unusable rules), got {code}")

        yield f"{key} {bad!r} is refused as a rules error", malformed_allowance

    # --- only_extensions: a rule entry that reads some file types only (R2T-3) ----------
    for bad in ([], [".PS1"], ["ps1"], [".rb"], ".ps1"):

        def malformed_only_extensions(bad=bad) -> None:
            raw = base_rules()
            for rule in raw["rules"]:
                if rule["id"] == "NET-001":
                    rule["only_extensions"] = bad
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                if run_check_with_rules(root, raw) != EXIT_ERROR:
                    raise Failure(f"only_extensions {bad!r}: expected exit 2 (unusable rules)")

        yield f"only_extensions {bad!r} is refused as a rules error", malformed_only_extensions

    def download_commands_only_in_scripts() -> None:
        # The same command line is a violation in a script and nothing at all in Python source,
        # where only the rules written for Python syntax apply.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "tools/some-tool/fetch.py", "curl -fsSL $url -o $target")
            expect_clean(root, "a download command in a .py file")
            plant(root, "tools/some-tool/fetch.ps1", "curl -fsSL $url -o $target")
            expect_violation(root, "a download command in a .ps1 file", "NET-009",
                             "NET-009/shell-commands")

    yield "NET-009/shell-commands reads script and workflow files only", download_commands_only_in_scripts

    # --- the allow marker: one rule, one line, a reason, pinned, and always listed ----------
    # (ON2-5; R2T-4: every marker is pinned in rules.json and must lift a hit; V5-3: the pin names
    # the hit's text too)
    remote = "        x = CreateRemoteThread(h);"
    pin_remote = [(DEFAULT_SITE, "INJ-004", "CreateRemoteThread")]
    pin_open = (DEFAULT_SITE, "INJ-007", "OpenProcess")

    def allow_marker_works() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE, remote + " // " + allow_marker + "(INJ-004): enforcement only")
            code, report = run_check(root, pins=pin_remote)
            if code != EXIT_OK:
                raise Failure(f"a named marker: expected exit 0, got {code}; {report.get('violations')}")
            listed = report.get("allow_markers", [])
            if report.get("allow_marker_count") != 1 or len(listed) != 1:
                raise Failure(f"the honoured marker is not listed once: {report}")
            entry = listed[0]
            expected = {"file": DEFAULT_SITE, "line": 2, "rule_id": "INJ-004",
                        "reason": "enforcement only", "match": "CreateRemoteThread", "lifted": 1}
            if entry != expected:
                raise Failure(f"marker entry {entry} != {expected}")

    yield f"{allow_marker}(INJ-004) lifts INJ-004 on its line and is listed", allow_marker_works

    def marker_is_listed_in_text_output() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE, remote + " // " + allow_marker + "(INJ-004): enforcement only")
            completed = subprocess.run(
                [sys.executable, str(CHECK_PY), "--root", str(root),
                 "--rules", str(write_rules(root, pins=pin_remote))],
                capture_output=True, text=True, encoding="utf-8", errors="replace", check=False,
            )
            out = completed.stdout
            if completed.returncode != EXIT_OK:
                raise Failure(f"expected exit 0, got {completed.returncode}: {out}")
            for needed in ("1 allow marker(s) honoured", f"{DEFAULT_SITE}:2: [INJ-004] enforcement only"):
                if needed not in out:
                    raise Failure(f"text output lacks {needed!r}:\n{out}")

    yield "honoured markers are listed with a count in the text output", marker_is_listed_in_text_output

    for name, line, rule_id in (
        ("a bare marker", remote + " // " + allow_marker + ": enforcement only", "INJ-004"),
        ("a bare marker with no text", remote + " // " + allow_marker, "INJ-004"),
        ("a marker without a reason", remote + " // " + allow_marker + "(INJ-004):", "INJ-004"),
        ("a marker whose reason is punctuation", remote + " /* " + allow_marker + "(INJ-004): */",
         "INJ-004"),
        ("a marker naming another rule", remote + " // " + allow_marker + "(INJ-007): wrong rule",
         "INJ-004"),
        ("a marker for rule A beside a hit of rule B",
         remote + " OpenProcess(a, b, c); // " + allow_marker + "(INJ-004): enforcement only",
         "INJ-007"),
    ):

        def marker_lifts_nothing(name: str = name, line: str = line, rule_id: str = rule_id) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, DEFAULT_SITE, line)
                # Pinned, so that what is proved is the marker's own shape, not a missing pin.
                expect_reported(root, name, rule_id, pins=pin_remote + [pin_open])

        yield f"{name} does not hide {rule_id}", marker_lifts_nothing

    def marker_for_a_hides_only_a() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE,
                  remote + " OpenProcess(a, b, c); // " + allow_marker + "(INJ-004): enforcement only")
            _, report = run_check(root, pins=pin_remote)
            found = sorted(v["rule_id"] for v in report.get("violations", []))
            if found != ["INJ-007"]:
                raise Failure(f"expected only INJ-007 reported, got {found}")

    yield "a marker for INJ-004 lifts INJ-004 and nothing else", marker_for_a_hides_only_a

    def two_markers_on_one_line() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE,
                  remote + " OpenProcess(a, b, c); // " + allow_marker + "(INJ-004): first "
                  + allow_marker + "(INJ-007): second")
            code, report = run_check(root, pins=pin_remote + [pin_open])
            if code != EXIT_OK or report.get("allow_marker_count") != 2:
                raise Failure(f"two named markers: exit {code}, report {report}")
            reasons = sorted(m["reason"] for m in report["allow_markers"])
            if reasons != ["first", "second"]:
                raise Failure(f"each marker keeps its own reason, got {reasons}")

    yield "two markers on one line lift their two rules", two_markers_on_one_line

    def reason_is_not_another_markers_text() -> None:
        # "<marker>(A):<marker>(B): text" used to lift A with B's marker as its reason, and B
        # was never seen. A has no reason of its own, so it lifts nothing; B lifts B.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE,
                  remote + " OpenProcess(a, b, c); // " + allow_marker + "(INJ-004):"
                  + allow_marker + "(INJ-007): enforcement only")
            _, report = run_check(root, pins=[pin_open])
            found = sorted(v["rule_id"] for v in report.get("violations", []))
            if found != ["INJ-004"]:
                raise Failure(f"expected only INJ-004 reported, got {found}")

    yield "a marker's reason cannot be the next marker's text", reason_is_not_another_markers_text

    def marker_reaches_its_own_line_only() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE,
                  "        // " + allow_marker + "(INJ-004): the next line is enforcement\n" + remote)
            expect_reported(root, "a marker on the line above", "INJ-004", pins=pin_remote)

    yield "a marker does not reach the next line", marker_reaches_its_own_line_only

    def unpinned_marker() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE, remote + " // " + allow_marker + "(INJ-004): enforcement only")
            report = expect_reported(root, "an unpinned marker", "INJ-004")
            if not any("not pinned" in error for error in report.get("errors", [])):
                raise Failure(f"an unpinned marker is not reported as one: {report.get('errors')}")

    yield "an unpinned marker lifts nothing and fails the run", unpinned_marker

    for name, text, pins in (
        ("a pinned marker that lifts no hit", "        // " + allow_marker + "(INJ-004): nothing here",
         pin_remote),
        ("a pinned marker that is not in the tree", "        x = 1;", pin_remote),
        ("two markers where one is pinned",
         remote + " // " + allow_marker + "(INJ-004): one\n" + remote + " // " + allow_marker
         + "(INJ-004): two", pin_remote),
    ):

        def marker_mismatch(name: str = name, text: str = text, pins=pins) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, DEFAULT_SITE, text)
                code, report = run_check(root, pins=pins)
                if code != EXIT_ERROR or not report.get("errors"):
                    raise Failure(f"{name}: expected exit 2, got {code}: {report.get('errors')}")

        yield f"{name} fails the run", marker_mismatch

    # --- the pin names the one hit a marker lifts (audit 2026-10-03, V5-3) ------------------
    # A marker used to lift every hit of its rule on its line, and its pin said only "file and
    # rule": a second call added to a marked line passed. Now the pin carries the matched text, and
    # the marker lifts that hit only when it is the line's one hit of the rule.
    for name, line in (
        ("a second hit of the marked rule on the marked line",
         remote + " y = CreateRemoteThread(g); // " + allow_marker + "(INJ-004): enforcement only"),
        ("a marked line whose one hit is not the pinned text",
         "        x = CreateRemoteThreadEx(h); // " + allow_marker + "(INJ-004): enforcement only"),
    ):

        def pinned_text_must_match(name: str = name, line: str = line) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, DEFAULT_SITE, line)
                code, report = run_check(root, pins=pin_remote)
                found = [v["rule_id"] for v in report.get("violations", [])]
                if code != EXIT_ERROR or "INJ-004" not in found or report.get("allow_marker_count"):
                    raise Failure(f"{name}: expected INJ-004 reported, no marker honoured and exit 2; "
                                  f"got exit {code}, {found}, {report.get('errors')}")

        yield f"{name} is reported and fails the run", pinned_text_must_match

    def each_pin_lifts_its_own_hit() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE, remote + " // " + allow_marker + "(INJ-004): one\n"
                  + "        x = CreateRemoteThreadEx(h); // " + allow_marker + "(INJ-004): two")
            code, report = run_check(
                root, pins=pin_remote + [(DEFAULT_SITE, "INJ-004", "CreateRemoteThreadEx")])
            matches = sorted(str(m.get("match")) for m in report.get("allow_markers", []))
            if code != EXIT_OK or matches != ["CreateRemoteThread", "CreateRemoteThreadEx"]:
                raise Failure(f"two pins in one file: exit {code}, matches {matches}, "
                              f"errors {report.get('errors')}")

        yield "two markers in one file each lift the hit their own pin names", each_pin_lifts_its_own_hit

    for bad in (
        {"file": DEFAULT_SITE, "rule": "INJ-04", "match": "CreateRemoteThread"},
        {"file": "src\\Collector\\Capture\\Sample.cs", "rule": "INJ-004", "match": "CreateRemoteThread"},
        {"file": DEFAULT_SITE, "rule": "INJ-004", "match": "CreateRemoteThread", "reason": "extra key"},
        {"file": DEFAULT_SITE, "rule": "INJ-004"},
        {"file": DEFAULT_SITE, "rule": "INJ-004", "match": ""},
        {"file": DEFAULT_SITE, "rule": "INJ-004", "match": 7},
        {"file": DEFAULT_SITE, "rule": "INJ-004", "match": "Create\nRemoteThread"},
        {"file": DEFAULT_SITE},
        "src/Collector/Capture/Sample.cs",
    ):

        def malformed_pin(bad=bad) -> None:
            raw = base_rules()
            raw["allow_markers"] = [bad]
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, DEFAULT_SITE, remote + " // " + allow_marker + "(INJ-004): enforcement")
                if run_check_with_rules(root, raw) != EXIT_ERROR:
                    raise Failure(f"allow_markers entry {bad!r}: expected exit 2 (unusable rules)")

        yield f"allow_markers entry {bad!r} is refused as a rules error", malformed_pin

    def marker_naming_an_unknown_rule() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE, remote + " // " + allow_marker + "(INJ-04): typo")
            code, report = run_check(root)
            if code != EXIT_ERROR or not report.get("errors"):
                raise Failure(f"a marker naming no declared rule: expected exit 2, got {code}")

    yield "a marker naming an undeclared rule fails the run", marker_naming_an_unknown_rule

    # --- a scan that could not read everything fails (ON2-6) --------------------------
    def nothing_scanned() -> None:
        # Every configured root and exclusion exists, so the one thing wrong is that no file in them
        # is of a scanned type (V5-8: an empty root failed on the missing roots, never reaching the
        # "no file was scanned" check this case is named after).
        raw = {**base_rules(), "scan_files": []}
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for relative in raw["scan_dirs"] + raw["exclude_paths"]:
                (root / relative).mkdir(parents=True, exist_ok=True)
            plant(root, "src/Collector/notes.md", "nothing the scan reads")
            code, report = run_check(root, raw)
            errors = report.get("errors", [])
            if (code, report.get("files_scanned")) != (EXIT_ERROR, 0) or len(errors) != 1 \
                    or "no file was scanned" not in errors[0]:
                raise Failure(f"a tree with no scanned file: expected exit 2 with only the "
                              f"'no file was scanned' error, got {code}: {errors}")

    yield "a run that scanned no file fails", nothing_scanned

    def oversize_file() -> None:
        raw = base_rules()
        raw["max_file_bytes"] = 64
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, DEFAULT_SITE, "        // padding " + "x" * 80)
            code = run_check_with_rules(root, raw)
            if code != EXIT_ERROR:
                raise Failure(f"a file over max_file_bytes: expected exit 2, got {code}")

    yield "a file over max_file_bytes fails the run", oversize_file

    def utf16_file() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            path = root / DEFAULT_SITE
            path.write_bytes(("x = CreateRemoteThread(h);\r\n").encode("utf-16"))
            code, report = run_check(root)
            if code != EXIT_ERROR:
                raise Failure(f"a UTF-16 file: expected exit 2, got {code}: {report}")

    yield "a file with NUL bytes (UTF-16) fails the run", utf16_file

    for attribute, name in (("read_bytes", "Unreadable.cs"), ("stat", "Unstatable.cs")):

        def file_system_failure(attribute: str = attribute, name: str = name) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                plant(root, f"src/Collector/{name}", "        x = 1;")
                code, report = run_in_process(root, attribute, name)
                if code != EXIT_ERROR or not report.get("errors"):
                    raise Failure(f"{attribute}() failing on {name}: expected exit 2, got {code}")

        yield f"a file whose {attribute}() fails fails the run", file_system_failure

    def unlistable_directory() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "src/Collector/Locked/Hidden.cs", "        x = CreateRemoteThread(h);")
            code, report = run_in_process(root, "scandir", "Locked")
            if code != EXIT_ERROR or not report.get("errors"):
                raise Failure(f"a directory that cannot be listed: expected exit 2, got {code}")

    yield "a directory that cannot be listed fails the run", unlistable_directory

    for bad in ("h", ".H", "", ".c s", 7, None):

        def malformed_extension(bad=bad) -> None:
            raw = base_rules()
            raw["include_extensions"] = list(raw["include_extensions"]) + [bad]
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                code = run_check_with_rules(root, raw)
                if code != EXIT_ERROR:
                    raise Failure(f"include_extensions entry {bad!r}: expected exit 2, got {code}")

        yield f"include_extensions entry {bad!r} is refused as a rules error", malformed_extension

    for bad in (0, -1, "4194304", True):

        def malformed_size(bad=bad) -> None:
            raw = base_rules()
            raw["max_file_bytes"] = bad
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                code = run_check_with_rules(root, raw)
                if code != EXIT_ERROR:
                    raise Failure(f"max_file_bytes {bad!r}: expected exit 2, got {code}")

        yield f"max_file_bytes {bad!r} is refused as a rules error", malformed_size

    # --- the README's rule table names every rule entry (ON2-10) ------------------------
    def readme_names_every_rule() -> None:
        text = README_PATH.read_text(encoding="utf-8")
        absent = [key for key in declared if f"`{key}`" not in text]
        if absent:
            raise Failure("README.md's rule table does not name: " + ", ".join(absent))

    yield "README.md names every rule entry", readme_names_every_rule

    def excluded_dirs_are_skipped() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            plant(root, "src/Collector/MentorRecorder.Collector.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />")
            plant(root, "src/Collector/bin/Release/Sample.cs", "        CreateRemoteThread(h);")
            plant(root, "src/Collector/obj/Debug/Sample.cs", "        CreateRemoteThread(h);")
            expect_clean(root, "build output directories beside the project file")

    yield "bin/ and obj/ beside a .csproj are not scanned", excluded_dirs_are_skipped

    # V5-7: the SDK compiles every .cs under the project folder except <project>/bin and
    # <project>/obj, so a bin/ or obj/ anywhere else is source like any other directory.
    for inside in (
        "src/Collector/Capture/obj/Hook.cs",
        "src/Collector/Capture/bin/Hook.cs",
        "src/Collector/bin/Hook.cs",
        "tools/some-tool/obj/fetch.py",
    ):

        def output_name_elsewhere_is_scanned(inside: str = inside) -> None:
            with tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                clean_tree(root)
                if not inside.startswith("src/Collector/bin/"):
                    plant(root, "src/Collector/MentorRecorder.Collector.csproj", "<Project />")
                plant(root, inside, "        x = CreateRemoteThread(h);")
                expect_violation(root, f"planted in {inside}", "INJ-004")

        yield f"{inside} is scanned", output_name_elsewhere_is_scanned

    def unscanned_extension() -> None:
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            clean_tree(root)
            # Documentation names the forbidden APIs on purpose; it is not code.
            plant(root, "src/Collector/notes.md", "we never call CreateRemoteThread")
            expect_clean(root, "a markdown file")

    yield "documentation is not scanned", unscanned_extension


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Negative self-test for the static boundary checker."
    )
    parser.add_argument("-v", "--verbose", action="store_true", help="print every case")
    args = parser.parse_args(argv)

    for stream in (sys.stdout, sys.stderr):
        try:
            stream.reconfigure(encoding="utf-8", errors="replace")  # type: ignore[union-attr]
        except (AttributeError, OSError, ValueError):
            pass

    if not CHECK_PY.is_file():
        print(f"error: checker not found: {CHECK_PY}", file=sys.stderr)
        return EXIT_VIOLATION

    passed = 0
    failures: list[str] = []
    for name, thunk in cases():
        try:
            thunk()
        except Failure as exc:
            failures.append(f"{name}: {exc}")
            print(f"FAIL {name}", file=sys.stderr)
            continue
        passed += 1
        if args.verbose:
            print(f"ok   {name}")

    print()
    if failures:
        for failure in failures:
            print("  " + failure, file=sys.stderr)
        print(f"FAIL: {len(failures)} of {passed + len(failures)} self-test case(s) failed.")
        return EXIT_VIOLATION

    print(f"OK: {passed} self-test case(s) passed; the checker rejects what it must.")
    return EXIT_OK


if __name__ == "__main__":
    sys.exit(main())
