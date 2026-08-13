using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MultiSeat.Service.Sessions;

/// <summary>
/// Detects and validates the multi-session patch for Terminal Services.
///
/// Windows client editions allow one interactive session; MultiSeat needs several, so a shim
/// must be loaded in place of the stock <c>termsrv.dll</c>. Without it, background sessions
/// fail to create. More than one product does this, and MultiSeat works with any of them:
///
///   - <b>RDP Wrapper</b> (<c>rdpwrap.dll</c>) — looks its patch offsets up in
///     <c>rdpwrap.ini</c>, keyed by the exact termsrv.dll build. Every cumulative update that
///     ships a new termsrv.dll breaks it until a matching ini entry is published.
///   - <b>TermWrap</b> (llccd/TermWrap) — disassembles termsrv.dll at load and finds the
///     offsets itself, so it needs no per-build ini and survives Windows updates.
///
/// Detection is therefore "TermService's ServiceDll is NOT the stock termsrv.dll", never a
/// vendor filename: matching on "rdpwrap" reported a working TermWrap install as missing,
/// sending the reader down the wrong path while three sessions ran fine.
///
/// (The class name is historical — it predates there being more than one such product.)
/// </summary>
public sealed class RdpWrapper
{
    private readonly ILogger<RdpWrapper> _logger;

    public RdpWrapper(ILogger<RdpWrapper> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Verify that multi-session support is available.
    /// Checks:
    ///   1. TermService is running (the shim is loaded by it, so nothing is knowable before that)
    ///   2. TermService's ServiceDll points at a shim rather than the stock termsrv.dll
    ///   3. For RDP Wrapper specifically, that rdpwrap.ini covers the installed termsrv.dll
    ///
    /// On a cold boot, call <see cref="WaitForTermServiceAsync"/> first — otherwise this runs
    /// before TermService has started and reports a present patch as missing.
    /// </summary>
    public bool EnsureMultiSession()
    {
        var build = Environment.OSVersion.Version.Build;
        _logger.LogInformation("Windows build: {Build}", build);

        // Check if TermService is running
        try
        {
            using var sc = new System.ServiceProcess.ServiceController("TermService");
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Running)
            {
                _logger.LogError(
                    "TermService is {Status}, not Running — the multi-session shim is loaded by " +
                    "TermService, so its state cannot be determined yet", sc.Status);
                return false;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Cannot query TermService");
            return false;
        }

        var serviceDll = ReadServiceDll();

        if (!IsMultiSessionPatchPresent(serviceDll))
        {
            _logger.LogWarning(
                "TermService ServiceDll is the stock termsrv.dll ({Dll}) — no multi-session " +
                "patch is installed, so concurrent sessions will not work. Install TermWrap " +
                "(https://github.com/llccd/TermWrap — no per-build config, survives Windows " +
                "updates) or RDP Wrapper via prerequisites\\install-prerequisites.ps1.",
                serviceDll ?? "(ServiceDll unset)");
            return false;
        }

        var expanded = ExpandServiceDll(serviceDll!);
        var dllName = Path.GetFileName(expanded);
        _logger.LogInformation(
            "Multi-session patch detected — TermService ServiceDll is {Dll} (not stock termsrv.dll)",
            expanded);

        // Only RDP Wrapper is ini-driven. TermWrap and anything else that resolves its own
        // offsets have nothing to validate, so a missing ini is not a fault.
        if (!dllName.Contains("rdpwrap", StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogDebug(
                "{Dll} is not RDP Wrapper — no rdpwrap.ini validation applies", dllName);
            return true;
        }

        var iniPath = FindRdpWrapIni(expanded);
        if (iniPath != null)
            return ValidateIniForTermsrv(iniPath);

        _logger.LogInformation("rdpwrap.ini not found — assuming wrapper is active");
        return true;
    }

    /// <summary>
    /// Poll until TermService reports Running, or the timeout expires.
    /// Returns true if it reached Running.
    ///
    /// On a cold boot the worker used to evaluate the patch before TermService had started and
    /// log "patch not detected" and "TermService is not running" in the same second. The patch
    /// was present; the check simply ran too early.
    /// </summary>
    public async Task<bool> WaitForTermServiceAsync(TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var logged = false;

        while (sw.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                using var sc = new System.ServiceProcess.ServiceController("TermService");
                if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running)
                {
                    if (logged)
                        _logger.LogInformation(
                            "TermService reached Running after {Ms}ms", sw.ElapsedMilliseconds);
                    return true;
                }

                if (!logged)
                {
                    _logger.LogInformation(
                        "TermService is {Status} — waiting up to {Seconds}s for it to start " +
                        "before evaluating the multi-session patch",
                        sc.Status, (int)timeout.TotalSeconds);
                    logged = true;
                }
            }
            catch (Exception ex)
            {
                // Service may not be queryable yet during early boot — keep polling.
                _logger.LogDebug(ex, "Could not query TermService while waiting");
            }

            await Task.Delay(500, ct);
        }

        return false;
    }

    /// <summary>
    /// True when TermService's ServiceDll points at a multi-session shim rather than the stock
    /// termsrv.dll. Compares the filename only, case-insensitively — full-path comparison is
    /// brittle across System32 vs SysWOW64 and differing quoting.
    ///
    /// Pure and static so it can be tested without a patched Windows install.
    /// </summary>
    public static bool IsMultiSessionPatchPresent(string? serviceDll)
    {
        if (string.IsNullOrWhiteSpace(serviceDll)) return false;

        var file = Path.GetFileName(ExpandServiceDll(serviceDll));
        if (string.IsNullOrEmpty(file)) return false;

        return !file.Equals("termsrv.dll", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ServiceDll is typically REG_EXPAND_SZ (<c>%SystemRoot%\...</c>) and may be quoted.</summary>
    private static string ExpandServiceDll(string serviceDll) =>
        Environment.ExpandEnvironmentVariables(serviceDll.Trim().Trim('"'));

    private string? ReadServiceDll()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\TermService\Parameters");
            return key?.GetValue("ServiceDll") as string;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read TermService ServiceDll registry key");
            return null;
        }
    }

    /// <summary>
    /// Locate rdpwrap.ini for an RDP Wrapper install — next to the DLL (classic System32
    /// install) or in the Program Files install dir. Returns null if neither exists.
    /// </summary>
    private static string? FindRdpWrapIni(string wrapperDllPath)
    {
        var beside = Path.Combine(Path.GetDirectoryName(wrapperDllPath) ?? "", "rdpwrap.ini");
        if (File.Exists(beside)) return beside;

        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "RDP Wrapper", "rdpwrap.ini");
        return File.Exists(installed) ? installed : null;
    }

    /// <summary>
    /// The version of <c>termsrv.dll</c> that rdpwrap.ini is keyed by — e.g. "10.0.26100.8737".
    ///
    /// Read the build/revision from the numeric VS_FIXEDFILEINFO fields, NOT from the
    /// FileVersion string. Windows servicing updates the binary version resource but can
    /// leave the localized string block stale, so the two disagree on a patched machine:
    /// this host's string reads "10.0.26100.8115" while the numeric fields (and the file's
    /// actual identity, confirmed by hashing it against the WinSxS copies) are
    /// 10.0.26100.8737. Trusting the string means looking up a termsrv.dll that is not the
    /// one installed.
    ///
    /// The major/minor come from <see cref="Environment.OSVersion"/> instead, because this
    /// process has no application manifest: Windows then applies its compatibility shim and
    /// reports the major/minor of OS files as 6.2 (Windows 8). Observed in production — the
    /// service resolved "6.2.26100.8737" for a file PowerShell reads as "10.0.26100.8737",
    /// and no such ini section exists, so a correctly-patched host was reported broken. The
    /// shim leaves build and revision untouched, and .NET's OSVersion is honest (it calls
    /// RtlGetVersion), so composing the two gives the true version.
    ///
    /// Note OSVersion's own build (26200 here) is the OS build and is NOT interchangeable
    /// with termsrv.dll's build (26100) — only major/minor are taken from it.
    ///
    /// The deeper fix is to ship an app manifest declaring supportedOS, which would remove
    /// the shim; that is a broader change than this check warrants.
    ///
    /// Returns null if the file or its version resource cannot be read.
    /// </summary>
    private string? ResolveTermsrvVersion()
    {
        try
        {
            var termsrv = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "termsrv.dll");
            if (!File.Exists(termsrv)) return null;

            var vi = FileVersionInfo.GetVersionInfo(termsrv);
            var os = Environment.OSVersion.Version;
            return $"{os.Major}.{os.Minor}.{vi.FileBuildPart}.{vi.FilePrivatePart}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not read termsrv.dll version");
            return null;
        }
    }

    /// <summary>
    /// Check that rdpwrap.ini carries offsets for the termsrv.dll actually installed.
    /// Only applies to RDP Wrapper installs — TermWrap has no ini.
    ///
    /// The ini is keyed by termsrv.dll's file version (<c>[10.0.26100.8737]</c>), which is a
    /// different identifier from the Windows build number (26200) — different numbering
    /// scheme, different value. This used to substring-search the ini for the OS build, which
    /// was wrong in both directions: it passed on any ini that happened to contain those
    /// digits anywhere, and it would fail a correctly-patched host whose ini simply had no
    /// section from that servicing branch. It only appeared to work here because the current
    /// upstream ini happens to carry an unrelated [10.0.26200.5001] section.
    /// </summary>
    private bool ValidateIniForTermsrv(string iniPath)
    {
        var version = ResolveTermsrvVersion();
        if (version is null)
        {
            // Can't identify the DLL, so we can't judge the ini. Don't claim breakage we
            // haven't established — the wrapper may well be fine.
            _logger.LogWarning(
                "Could not determine termsrv.dll version — skipping rdpwrap.ini validation");
            return true;
        }

        try
        {
            if (IniHasOffsetsFor(File.ReadLines(iniPath), version))
            {
                _logger.LogInformation(
                    "rdpwrap.ini has offsets for termsrv.dll {Version}", version);
                return true;
            }

            var section = $"[{version}]";
            _logger.LogError(
                "rdpwrap.ini has no {Section} section, so it carries no offsets for the " +
                "termsrv.dll installed on this machine — multi-session will not work. " +
                "Update rdpwrap.ini from https://github.com/sebaxakerhtc/rdpwrap.ini " +
                "and restart TermService, or switch to TermWrap " +
                "(https://github.com/llccd/TermWrap), which resolves offsets by disassembling " +
                "termsrv.dll and so needs no per-build ini at all. (Note: termsrv.dll's " +
                "FileVersion STRING may report a different, stale version — {Version} is from " +
                "the binary version fields.)",
                section, version);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to read rdpwrap.ini");
            return false;
        }
    }

    /// <summary>
    /// True when rdpwrap.ini declares a section for this exact termsrv.dll version.
    ///
    /// Matches the section header <c>[10.0.26100.8737]</c> as a whole line, not as a
    /// substring: the ini is full of version-like numbers (other builds' sections, offset
    /// tables, sub-sections such as <c>[10.0.26100.8737-SLInit]</c>), so a substring test
    /// reports coverage that does not exist.
    ///
    /// Pure and static so it can be tested without a real Windows install — same rationale
    /// as <see cref="Streaming.ApolloManager.ResolveLogPath"/>.
    /// </summary>
    public static bool IniHasOffsetsFor(IEnumerable<string> iniLines, string termsrvVersion)
    {
        var section = $"[{termsrvVersion}]";

        foreach (var line in iniLines)
        {
            if (line.AsSpan().Trim().Equals(section, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
