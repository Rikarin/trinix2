# Runs the platform self-test at boot and reports the verdict in one line.
#
# A file rather than an inline ExecStart=, because a PowerShell script embedded
# in a unit file has to survive systemd's own expansion of `$` and its quoting
# rules on the way to a shell-less exec. The first version of this did, and
# systemd read `$ok` as one of its own variables and refused to start the unit.
#
# Output goes to the console as well as the journal (see the unit), so this is
# also what the unattended boot check reads.

$ErrorActionPreference = 'Stop'

try
{
    $checks = Test-TrinixSystem
}
catch
{
    # A self-test that cannot run is a failure, and the reason is worth more
    # than the verdict. Reaching here means the module did not load, which
    # means something is wrong with .NET or with the image itself.
    Write-Output "TRINIX-SELFTEST-False"
    Write-Output "  self-test could not run: $( $_.Exception.Message )"
    exit 1
}

$ok = -not ($checks | Where-Object { -not $_.Ok })

Write-Output "TRINIX-SELFTEST-$ok"

# Detail only when something is wrong. A healthy boot should cost one line;
# a broken one should say everything it can, because the console may be the
# only place anyone can read it.
if (-not $ok)
{
    foreach ($check in $checks)
    {
        Write-Output ("  {0}: {1} ({2})" -f $check.Component, $check.Ok, $check.Detail)
    }
    exit 1
}
