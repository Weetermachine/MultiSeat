using MultiSeat.Service.Sessions;
using Xunit;

namespace MultiSeat.Tests.Sessions;

/// <summary>
/// rdpwrap.ini is keyed by termsrv.dll's file version — [10.0.26100.8737] — which is a
/// different identifier from the Windows build number (26200). The check used to substring
/// -search the ini for the OS build, which was wrong in both directions: it passed on any
/// ini containing those digits anywhere, and would fail a correctly-patched host whose ini
/// had no section from that servicing branch.
/// </summary>
public class RdpWrapperTests
{
    // A realistic slice: several builds, an offset table full of version-like numbers,
    // and a sub-section that shares the prefix of a version we do NOT have offsets for.
    private static readonly string[] SampleIni =
    [
        "[Main]",
        "Updated=2026-08-02",
        "",
        "[10.0.26100.8521]",
        "LocalOnlyPatch.x64=1",
        "LocalOnlyOffset.x64=1B4A3C",
        "",
        "[10.0.26100.8737]",
        "LocalOnlyPatch.x64=1",
        "SingleUserOffset.x64=26200",
        "",
        "[10.0.26100.8749-SLInit]",
        "bServerSku=1",
    ];

    [Fact]
    public void IniHasOffsetsFor_FindsTheSectionForTheInstalledTermsrv()
    {
        Assert.True(RdpWrapper.IniHasOffsetsFor(SampleIni, "10.0.26100.8737"));
    }

    [Fact]
    public void IniHasOffsetsFor_RejectsAVersionWithNoSection()
    {
        // The ini covers .8521 and .8737 but not .8115 — the stale version this host's
        // FileVersion STRING reports. Looking that up must not claim coverage.
        Assert.False(RdpWrapper.IniHasOffsetsFor(SampleIni, "10.0.26100.8115"));
    }

    [Fact]
    public void IniHasOffsetsFor_DoesNotMatchTheOsBuildAppearingAsAnOffsetValue()
    {
        // The regression: "26200" is the OS build and appears inside an offset value, so the
        // old substring search reported coverage. It is not a section and proves nothing.
        Assert.False(RdpWrapper.IniHasOffsetsFor(SampleIni, "26200"));
    }

    [Fact]
    public void IniHasOffsetsFor_DoesNotAcceptASubSectionAsTheVersionItself()
    {
        // [10.0.26100.8749-SLInit] is a sub-section; there is no [10.0.26100.8749] here, so
        // the wrapper has no offsets for that build.
        Assert.False(RdpWrapper.IniHasOffsetsFor(SampleIni, "10.0.26100.8749"));
    }

    [Fact]
    public void IniHasOffsetsFor_ToleratesSurroundingWhitespace()
    {
        Assert.True(RdpWrapper.IniHasOffsetsFor(["  [10.0.26100.8737]  "], "10.0.26100.8737"));
    }

    [Fact]
    public void IniHasOffsetsFor_ReturnsFalseForAnEmptyIni()
    {
        Assert.False(RdpWrapper.IniHasOffsetsFor([], "10.0.26100.8737"));
    }

    // ── Multi-session patch detection ─────────────────────────────────
    // Detection is "ServiceDll is not stock termsrv.dll", not a vendor filename. Matching on
    // "rdpwrap" reported a working TermWrap install as missing while three sessions ran.

    [Fact]
    public void IsMultiSessionPatchPresent_DetectsTermWrap()
    {
        Assert.True(RdpWrapper.IsMultiSessionPatchPresent(
            @"C:\Program Files\TermWrap\termwrap.dll"));
    }

    [Fact]
    public void IsMultiSessionPatchPresent_DetectsRdpWrapper()
    {
        Assert.True(RdpWrapper.IsMultiSessionPatchPresent(
            @"C:\Program Files\RDP Wrapper\rdpwrap.dll"));
    }

    [Fact]
    public void IsMultiSessionPatchPresent_RejectsStockTermsrv()
    {
        Assert.False(RdpWrapper.IsMultiSessionPatchPresent(
            @"%SystemRoot%\System32\termsrv.dll"));
    }

    [Fact]
    public void IsMultiSessionPatchPresent_RejectsStockTermsrvRegardlessOfCaseOrDirectory()
    {
        // Compare the filename only: System32 vs SysWOW64 and casing are not signal.
        Assert.False(RdpWrapper.IsMultiSessionPatchPresent(@"C:\Windows\SysWOW64\TermSrv.DLL"));
    }

    [Fact]
    public void IsMultiSessionPatchPresent_HandlesQuotedAndPaddedValues()
    {
        Assert.False(RdpWrapper.IsMultiSessionPatchPresent("  \"%SystemRoot%\\System32\\termsrv.dll\"  "));
        Assert.True(RdpWrapper.IsMultiSessionPatchPresent("  \"C:\\Program Files\\TermWrap\\termwrap.dll\"  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMultiSessionPatchPresent_ReturnsFalseWhenServiceDllIsMissing(string? serviceDll)
    {
        // No ServiceDll value at all is "unpatched", not "some other shim".
        Assert.False(RdpWrapper.IsMultiSessionPatchPresent(serviceDll));
    }
}
