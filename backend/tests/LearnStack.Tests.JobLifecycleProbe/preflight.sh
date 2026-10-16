#!/bin/sh
# ADR-0060: no managed entry until the Linux collector and inherited core limits qualify.
set -eu
umask 077

refuse() {
    printf 'LearnStackLifecycleReceipt:{"Protocol":1,"Error":"%s"}\n' "$1"
    exit 78
}

[ "$#" -eq 1 ] || [ "$#" -eq 2 ] || refuse argument_refused
if [ "$#" -eq 2 ]; then
    case "$1:$2" in
        gen_post_builder:sink|gen_constructor_before:constructor|gen_post_builder:capture_dispose|gen_dispose_second_announce:second_announce|gen_dispose_outer_complete:outer_completion) ;;
        *) refuse argument_refused ;;
    esac
fi
case "$1" in
    benign|constructor|startup_unwind|lifecycle_unwind|host_stop_then_fatal|final_exit_then_fatal) ;;
    gen_constructor_before|gen_constructor_after|gen_required_before_capture|gen_supplied_after_capture|gen_post_builder|gen_notify_succeeded|gen_wait_entry|gen_dispose_second_announce|gen_dispose_outer_complete|gen_announce_log|gen_host_stop|gen_final_exit|gen_process_exit|gen_process_exit_direct) ;;
    gen_stall_constructor|gen_stall_required|gen_stall_supplied|gen_stall_postbuilder) ;;
    *) refuse argument_refused ;;
esac

[ -r /proc/sys/kernel/core_pattern ] || refuse collector_unreadable
collector=$(cat /proc/sys/kernel/core_pattern) || refuse collector_unreadable
case "$collector" in
    \|*) refuse collector_pipe ;;
    core|core.%p) ;;
    *) refuse collector_unsupported ;;
esac

# The create request also sets core=0:0. Verify the actual inherited limits,
# then lower both again. A requested Docker setting alone is not evidence.
[ "$(ulimit -S -c)" = 0 ] || refuse soft_core_not_zero
[ "$(ulimit -H -c)" = 0 ] || refuse hard_core_not_zero
ulimit -S -c 0 || refuse soft_core_refused
ulimit -H -c 0 || refuse hard_core_refused
[ "$(ulimit -S -c)" = 0 ] || refuse soft_core_not_zero
[ "$(ulimit -H -c)" = 0 ] || refuse hard_core_not_zero
[ -r /proc/self/limits ] || refuse limits_unreadable
[ -r /proc/sys/kernel/core_uses_pid ] || refuse collector_unreadable
[ -r /proc/sys/fs/suid_dumpable ] || refuse collector_unreadable

mkdir -p /proof/home /proof/tmp || refuse scratch_refused
cd /proof || refuse scratch_refused

# Literal allowlist: do not inherit developer/CI/image variables into managed code.
exec /usr/bin/env -i \
    PATH=/usr/share/dotnet:/usr/bin:/bin \
    DOTNET_ROOT=/usr/share/dotnet \
    HOME=/proof/home TMPDIR=/proof/tmp LANG=C.UTF-8 LC_ALL=C.UTF-8 \
    LEARNSTACK_PROBE_PREFLIGHT=1 \
    DOTNET_DbgEnableMiniDump=0 COMPlus_DbgEnableMiniDump=0 \
    DOTNET_EnableCrashReport=0 COMPlus_EnableCrashReport=0 \
    DOTNET_EnableCrashReportOnly=0 COMPlus_EnableCrashReportOnly=0 \
    DOTNET_CreateDumpDiagnostics=0 COMPlus_CreateDumpDiagnostics=0 \
    DOTNET_CreateDumpVerboseDiagnostics=0 COMPlus_CreateDumpVerboseDiagnostics=0 \
    DOTNET_EnableDiagnostics=0 COMPlus_EnableDiagnostics=0 \
    /usr/share/dotnet/dotnet /opt/probe/LearnStack.Tests.JobLifecycleProbe.dll supervise "$@"
